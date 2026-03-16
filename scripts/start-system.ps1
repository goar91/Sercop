param(
    [switch]$Build,
    [switch]$SkipDocker,
    [switch]$SkipNgrok,
    [switch]$SkipCrmTunnel,
    [int]$CrmPort = 5050
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$runDir = Join-Path $root 'run'
$logDir = Join-Path $root 'logs'
$pidFile = Join-Path $runDir 'crm.pid'
$envFile = Join-Path $root '.env'
$frontendIndex = Join-Path $root 'frontend\dist\frontend\browser\index.html'
$backendDll = Join-Path $root 'backend\bin\Debug\net10.0\backend.dll'

function Assert-LastExitCode {
    param([string]$Action)

    if ($LASTEXITCODE -ne 0) {
        throw "$Action fallo con codigo de salida $LASTEXITCODE."
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-PrimaryNetworkCategory {
    try {
        return Get-NetConnectionProfile |
            Where-Object { $_.IPv4Connectivity -eq 'Internet' } |
            Select-Object -ExpandProperty NetworkCategory -First 1
    }
    catch {
        return $null
    }
}

function Resolve-PublicBaseIp {
    param([hashtable]$Config)

    if ($Config.ContainsKey('PUBLIC_BASE_IP') -and -not [string]::IsNullOrWhiteSpace($Config['PUBLIC_BASE_IP']) -and $Config['PUBLIC_BASE_IP'] -ne 'auto') {
        return $Config['PUBLIC_BASE_IP']
    }

    try {
        return (Invoke-RestMethod -Uri 'https://api.ipify.org?format=json' -TimeoutSec 10).ip
    }
    catch {
        return 'localhost'
    }
}

function Ensure-FirewallRule {
    param(
        [string]$Name,
        [int]$Port
    )

    if (-not (Test-IsAdministrator)) {
        return
    }

    try {
        $existing = Get-NetFirewallRule -DisplayName $Name -ErrorAction SilentlyContinue
        if (-not $existing) {
            New-NetFirewallRule -DisplayName $Name -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
        }
    }
    catch {
        Write-Warning "No se pudo crear/verificar la regla de firewall '$Name' para el puerto $Port."
    }
}

function Read-EnvFile {
    param([string]$Path)

    $map = @{}
    if (-not (Test-Path $Path)) {
        return $map
    }

    Get-Content $Path | ForEach-Object {
        if ($_ -match '^[A-Za-z0-9_]+=' -and -not $_.StartsWith('#')) {
            $parts = $_.Split('=', 2)
            $map[$parts[0]] = $parts[1]
        }
    }

    return $map
}

function Export-EnvConfig {
    param([hashtable]$Config)

    foreach ($entry in $Config.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}

function Get-RunningCrmProcess {
    param([string]$PidPath)

    if (-not (Test-Path $PidPath)) {
        return $null
    }

    $rawPid = (Get-Content $PidPath -ErrorAction SilentlyContinue | Select-Object -First 1).Trim()
    if (-not $rawPid) {
        Remove-Item $PidPath -Force -ErrorAction SilentlyContinue
        return $null
    }

    $process = Get-Process -Id ([int]$rawPid) -ErrorAction SilentlyContinue
    if (-not $process) {
        Remove-Item $PidPath -Force -ErrorAction SilentlyContinue
        return $null
    }

    return $process
}

function Get-ListeningCrmPids {
    param([int]$Port)

    try {
        return @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop |
            Select-Object -ExpandProperty OwningProcess -Unique)
    }
    catch {
        return @()
    }
}

function Test-CrmHasPublicBinding {
    param([int]$Port)

    try {
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop)
        if (-not $listeners) {
            return $false
        }

        return $listeners.LocalAddress | Where-Object { $_ -notin @('127.0.0.1', '::1') } | Select-Object -First 1
    }
    catch {
        return $false
    }
}

function Stop-StaleCrmListeners {
    param(
        [int]$Port,
        [string]$PidPath
    )

    foreach ($pid in (Get-ListeningCrmPids -Port $Port)) {
        $process = Get-Process -Id $pid -ErrorAction SilentlyContinue
        if (-not $process) {
            continue
        }

        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "No se pudo detener el proceso CRM existente con PID $($process.Id)."
        }
    }

    Remove-Item $PidPath -Force -ErrorAction SilentlyContinue
}

function Wait-HttpEndpoint {
    param(
        [string]$Uri,
        [int]$TimeoutSeconds = 60
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 5
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                return $true
            }
        }
        catch {
            $statusCode = $_.Exception.Response.StatusCode.value__
            if ($statusCode -and $statusCode -lt 500) {
                return $true
            }
            Start-Sleep -Seconds 2
        }
    }

    return $false
}

function Get-CrmExternalAccessMode {
    param([hashtable]$Config)

    if ($Config.ContainsKey('CRM_EXTERNAL_ACCESS_MODE') -and -not [string]::IsNullOrWhiteSpace($Config['CRM_EXTERNAL_ACCESS_MODE'])) {
        return $Config['CRM_EXTERNAL_ACCESS_MODE'].Trim().ToLowerInvariant()
    }

    return 'none'
}

function Test-PublicIpCrmUrl {
    param(
        [string]$PublicBaseIp,
        [int]$Port
    )

    if ([string]::IsNullOrWhiteSpace($PublicBaseIp) -or $PublicBaseIp -eq 'localhost') {
        return $null
    }

    $uri = "http://$PublicBaseIp`:$Port"
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $uri -TimeoutSec 10
        if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
            return $uri
        }
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode -and $statusCode -lt 500) {
            return $uri
        }
    }

    return $null
}

function Get-NgrokAuthToken {
    param([hashtable]$Config)

    if ($Config.ContainsKey('NGROK_AUTHTOKEN') -and -not [string]::IsNullOrWhiteSpace($Config['NGROK_AUTHTOKEN'])) {
        return $Config['NGROK_AUTHTOKEN'].Trim()
    }

    $candidatePaths = @(
        (Join-Path $env:USERPROFILE 'AppData\Local\ngrok\ngrok.yml'),
        (Join-Path $env:USERPROFILE '.config\ngrok\ngrok.yml'),
        (Join-Path $env:USERPROFILE '.ngrok2\ngrok.yml')
    )

    foreach ($path in $candidatePaths) {
        if (-not (Test-Path $path)) {
            continue
        }

        foreach ($line in (Get-Content $path)) {
            if ($line -match '^\s*authtoken:\s*(.+?)\s*$') {
                return $Matches[1].Trim()
            }
        }
    }

    return $null
}

function Wait-NgrokUrl {
    param([int]$TimeoutSeconds = 60)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $endpoints = Invoke-RestMethod -Uri 'http://127.0.0.1:4041/api/tunnels' -TimeoutSec 5
            $url = $endpoints.tunnels | ForEach-Object { $_.public_url } | Where-Object { $_ -match '^https://' } | Select-Object -First 1
            if ($url) {
                return $url
            }
        }
        catch {
        }

        try {
            $logs = docker compose --profile crm-ngrok logs --no-color --tail=50 crm-ngrok 2>$null
            if ($LASTEXITCODE -eq 0 -and $logs) {
                $matches = [regex]::Matches(($logs -join [Environment]::NewLine), 'https://[a-z0-9.-]+\.ngrok(?:-free)?\.app', 'IgnoreCase')
                if ($matches.Count -gt 0) {
                    return $matches[$matches.Count - 1].Value
                }
            }
        }
        catch {
        }

        Start-Sleep -Seconds 2
    }

    return $null
}

function Get-NgrokFailureMessage {
    try {
        $logs = docker compose --profile crm-ngrok logs --no-color --tail=80 crm-ngrok 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $logs) {
            return $null
        }

        $joined = $logs -join [Environment]::NewLine
        if ($joined -match 'ERR_NGROK_108') {
            return 'ERR_NGROK_108: la cuenta ngrok ya tiene otra sesion activa y el plan actual permite solo una.'
        }

        $errorLine = $logs | Where-Object { $_ -match 'ERROR:' } | Select-Object -Last 1
        if ($errorLine) {
            return $errorLine.Trim()
        }
    }
    catch {
    }

    return $null
}

function Wait-CloudflareQuickUrl {
    param([int]$TimeoutSeconds = 60)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $logs = docker compose --profile crm-tunnel logs --no-color --tail=50 crm-cloudflared 2>$null
            if ($LASTEXITCODE -eq 0 -and $logs) {
                $matches = [regex]::Matches(($logs -join [Environment]::NewLine), 'https://[a-z0-9-]+\.trycloudflare\.com', 'IgnoreCase')
                if ($matches.Count -gt 0) {
                    return $matches[$matches.Count - 1].Value
                }
            }
        }
        catch {
        }

        Start-Sleep -Seconds 2
    }

    return $null
}

function Start-CloudflareQuickTunnel {
    param([int]$Port)

    docker compose --profile crm-ngrok stop crm-ngrok | Out-Null
    $env:CRM_EXTERNAL_TARGET_PORT = "$Port"
    docker compose --profile crm-tunnel up -d crm-cloudflared | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo iniciar el tunel externo del CRM con Cloudflare.'
    }

    return Wait-CloudflareQuickUrl -TimeoutSeconds 60
}

function Start-CrmExternalTunnel {
    param(
        [hashtable]$Config,
        [int]$Port,
        [string]$PublicBaseIp
    )

    $mode = Get-CrmExternalAccessMode -Config $Config
    switch ($mode) {
        'none' {
            return $null
        }
        'public_ip_or_ngrok' {
            $publicUrl = Test-PublicIpCrmUrl -PublicBaseIp $PublicBaseIp -Port $Port
            if ($publicUrl) {
                docker compose --profile crm-tunnel stop crm-cloudflared | Out-Null
                docker compose --profile crm-ngrok stop crm-ngrok | Out-Null
                return $publicUrl
            }

            $token = Get-NgrokAuthToken -Config $Config
            if ([string]::IsNullOrWhiteSpace($token)) {
                throw 'No se encontro authtoken de ngrok para publicar el CRM externamente.'
            }

            $env:NGROK_AUTHTOKEN = $token
            $env:CRM_EXTERNAL_TARGET_PORT = "$Port"
            docker compose --profile crm-tunnel stop crm-cloudflared | Out-Null
            docker compose --profile crm-ngrok up -d crm-ngrok | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw 'No se pudo iniciar el tunel ngrok del CRM.'
            }

            $ngrokUrl = Wait-NgrokUrl -TimeoutSeconds 60
            if ($ngrokUrl) {
                return $ngrokUrl
            }

            $failure = Get-NgrokFailureMessage
            docker compose --profile crm-ngrok stop crm-ngrok | Out-Null
            if ([string]::IsNullOrWhiteSpace($failure)) {
                $failure = 'ngrok no devolvio una URL publica valida para el CRM.'
            }
            Write-Warning $failure
            return Start-CloudflareQuickTunnel -Port $Port
        }
        'ngrok' {
            $token = Get-NgrokAuthToken -Config $Config
            if ([string]::IsNullOrWhiteSpace($token)) {
                throw 'No se encontro authtoken de ngrok para publicar el CRM externamente.'
            }

            $env:NGROK_AUTHTOKEN = $token
            $env:CRM_EXTERNAL_TARGET_PORT = "$Port"
            docker compose --profile crm-tunnel stop crm-cloudflared | Out-Null
            docker compose --profile crm-ngrok up -d crm-ngrok | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw 'No se pudo iniciar el tunel ngrok del CRM.'
            }

            $ngrokUrl = Wait-NgrokUrl -TimeoutSeconds 60
            if ($ngrokUrl) {
                return $ngrokUrl
            }

            $failure = Get-NgrokFailureMessage
            docker compose --profile crm-ngrok stop crm-ngrok | Out-Null
            if ([string]::IsNullOrWhiteSpace($failure)) {
                $failure = 'ngrok no devolvio una URL publica valida para el CRM.'
            }
            throw $failure
        }
        'cloudflare_quick' {
            return Start-CloudflareQuickTunnel -Port $Port
        }
        default {
            throw "Modo de acceso externo no soportado: $mode"
        }
    }
}

New-Item -ItemType Directory -Force -Path $runDir | Out-Null
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

Push-Location $root
try {
    $config = Read-EnvFile -Path $envFile
    Export-EnvConfig -Config $config
    $publicBaseIp = Resolve-PublicBaseIp -Config $config
    $crmExternalAccessMode = Get-CrmExternalAccessMode -Config $config
    $isAdministrator = Test-IsAdministrator
    $primaryNetworkCategory = Get-PrimaryNetworkCategory
    $n8nPort = if ($config.ContainsKey('N8N_PORT') -and $config['N8N_PORT']) { $config['N8N_PORT'] } else { '5678' }
    $mailpitUiPort = '8025'
    $mailpitSmtpPort = '1025'
    $crmExternalUrlFile = Join-Path $runDir 'crm-external-url.txt'

    if ($crmExternalAccessMode -eq 'public_ip_or_ngrok' -and -not $isAdministrator -and $primaryNetworkCategory -eq 'Public') {
        Write-Warning 'La red activa esta en perfil Public y esta consola no tiene permisos de administrador. El CRM puede escuchar en 0.0.0.0:5050, pero Windows probablemente bloquee acceso entrante por IP publica.'
    }

    $env:N8N_HOST = $publicBaseIp
    $env:WEBHOOK_URL = "http://$publicBaseIp`:$n8nPort/"
    $env:N8N_EDITOR_BASE_URL = "http://$publicBaseIp`:$n8nPort/"

    Ensure-FirewallRule -Name 'Automatizacion CRM 5050' -Port $CrmPort
    Ensure-FirewallRule -Name 'Automatizacion n8n 5678' -Port ([int]$n8nPort)
    Ensure-FirewallRule -Name 'Automatizacion Mailpit UI 8025' -Port 8025
    Ensure-FirewallRule -Name 'Automatizacion Mailpit SMTP 1025' -Port 1025

    if ($Build -or -not (Test-Path $frontendIndex) -or -not (Test-Path $backendDll)) {
        Write-Host 'Compilando CRM antes de iniciar...'
        powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\build-crm.ps1')
        Assert-LastExitCode -Action 'La compilacion del CRM'
    }

    if (-not $SkipDocker) {
        Write-Host 'Iniciando servicios Docker: qdrant, ollama, mailpit y n8n...'
        docker compose up -d qdrant ollama mailpit n8n
        Assert-LastExitCode -Action 'El arranque de servicios Docker'

        $hasNgrokToken = $config.ContainsKey('NGROK_AUTHTOKEN') -and -not [string]::IsNullOrWhiteSpace($config['NGROK_AUTHTOKEN'])
        if (-not $SkipNgrok -and $hasNgrokToken) {
            Write-Host 'Iniciando tunel ngrok...'
            docker compose --profile tunnel up -d ngrok
            Assert-LastExitCode -Action 'El arranque del tunel ngrok'
        }

        if (Wait-HttpEndpoint -Uri "http://localhost:$n8nPort" -TimeoutSeconds 90) {
            Write-Host 'Sincronizando workflows de n8n...'
            powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\import-workflows.ps1')
            Assert-LastExitCode -Action 'La sincronizacion de workflows de n8n'
        }
        else {
            Write-Warning 'n8n no respondio a tiempo para sincronizar workflows automaticamente.'
        }
    }

    $existingCrm = Get-RunningCrmProcess -PidPath $pidFile
    if ($existingCrm -and -not (Test-CrmHasPublicBinding -Port $CrmPort)) {
        Write-Host "Se detecto un CRM escuchando solo en loopback. Reiniciando PID $($existingCrm.Id)..."
        Stop-StaleCrmListeners -Port $CrmPort -PidPath $pidFile
        $existingCrm = $null
    }

    if (-not $existingCrm -and (Get-ListeningCrmPids -Port $CrmPort)) {
        if (-not (Test-CrmHasPublicBinding -Port $CrmPort)) {
            Write-Host 'Se detecto un listener previo del CRM sin exposicion de red. Reiniciando...'
            Stop-StaleCrmListeners -Port $CrmPort -PidPath $pidFile
        }
    }

    if ($existingCrm) {
        $crmExternalUrl = $null
        Remove-Item $crmExternalUrlFile -Force -ErrorAction SilentlyContinue
        if (-not $SkipDocker -and -not $SkipCrmTunnel) {
            try {
                $crmExternalUrl = Start-CrmExternalTunnel -Config $config -Port $CrmPort -PublicBaseIp $publicBaseIp
                if ($crmExternalUrl) {
                    Set-Content -Path $crmExternalUrlFile -Value $crmExternalUrl -Encoding ASCII
                }
            }
            catch {
                Write-Warning $_.Exception.Message
            }
        }

        Write-Host "CRM ya esta iniciado con PID $($existingCrm.Id)."
        if ($crmExternalUrl) {
            Write-Host "CRM externo publicado en $crmExternalUrl"
        }
        return
    }

    $env:ASPNETCORE_URLS = "http://0.0.0.0:$CrmPort"
    $env:ASPNETCORE_ENVIRONMENT = 'Production'

    $stdoutLog = Join-Path $logDir 'crm.out.log'
    $stderrLog = Join-Path $logDir 'crm.err.log'

    $process = Start-Process dotnet -ArgumentList 'backend\\bin\\Debug\\net10.0\\backend.dll' -WorkingDirectory $root -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru
    Start-Sleep -Seconds 5

    if ($process.HasExited) {
        $errorText = if (Test-Path $stderrLog) { (Get-Content $stderrLog -Raw).Trim() } else { '' }
        throw "El CRM no pudo iniciar. $errorText"
    }

    Set-Content -Path $pidFile -Value $process.Id -Encoding ASCII
    Remove-Item $crmExternalUrlFile -Force -ErrorAction SilentlyContinue

    $crmExternalUrl = $null
    if (-not $SkipDocker -and -not $SkipCrmTunnel) {
        try {
            $crmExternalUrl = Start-CrmExternalTunnel -Config $config -Port $CrmPort -PublicBaseIp $publicBaseIp
            if ($crmExternalUrl) {
                Set-Content -Path $crmExternalUrlFile -Value $crmExternalUrl -Encoding ASCII
            }
        }
        catch {
            Write-Warning $_.Exception.Message
        }
    }

    Write-Host "CRM iniciado en http://${publicBaseIp}:$CrmPort con PID $($process.Id)."
    if ($crmExternalUrl) {
        Write-Host "CRM externo publicado en $crmExternalUrl"
    }
    Write-Host "n8n publicado en http://${publicBaseIp}:$n8nPort"
    Write-Host "Mailpit publicado en http://${publicBaseIp}:$mailpitUiPort"
    Write-Host "SMTP local publicado en ${publicBaseIp}:$mailpitSmtpPort"
    Write-Host 'Logs:'
    Write-Host "  $stdoutLog"
    Write-Host "  $stderrLog"
}
finally {
    Pop-Location
}

