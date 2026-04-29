param(
    [switch]$Build,
    [switch]$SkipDocker,
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
$crmExternalUrlFile = Join-Path $runDir 'crm-external-url.txt'
$stdoutLog = Join-Path $logDir 'crm.out.log'
$stderrLog = Join-Path $logDir 'crm.err.log'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-DotnetExecutable {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidate = Join-Path ${env:ProgramFiles} 'dotnet\dotnet.exe'
    if (Test-Path $candidate) {
        return $candidate
    }

    throw 'No se encontro dotnet en PATH ni en C:\Program Files\dotnet\dotnet.exe.'
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

function Test-CrmHasNetworkBinding {
    param([int]$Port)

    try {
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop)
        if (-not $listeners) {
            return $false
        }

        return ($listeners.LocalAddress | Where-Object { $_ -notin @('127.0.0.1', '::1') } | Select-Object -First 1) -ne $null
    }
    catch {
        return $false
    }
}

function Stop-CrmListeners {
    param([int]$Port)

    foreach ($owningProcessId in (Get-ListeningCrmPids -Port $Port)) {
        $process = Get-Process -Id $owningProcessId -ErrorAction SilentlyContinue
        if (-not $process) {
            continue
        }

        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            Write-Host "Listener CRM detenido. PID $($process.Id)."
        }
        catch {
            Write-Warning "No se pudo detener el proceso CRM existente con PID $($process.Id)."
        }
    }
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
        }

        Start-Sleep -Seconds 2
    }

    return $false
}

function Wait-CrmReady {
    param(
        [int]$Port,
        [int]$TimeoutSeconds = 90
    )

    $healthUri = "http://127.0.0.1:$Port/api/health"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Wait-HttpEndpoint -Uri $healthUri -TimeoutSeconds 5) -and (Test-CrmHasNetworkBinding -Port $Port)) {
            return $true
        }

        Start-Sleep -Seconds 2
    }

    return $false
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

function Get-NgrokApiKey {
    param([hashtable]$Config)

    if ($Config.ContainsKey('NGROK_API_KEY') -and -not [string]::IsNullOrWhiteSpace($Config['NGROK_API_KEY'])) {
        return $Config['NGROK_API_KEY'].Trim()
    }

    return $null
}

function Test-TruthyValue {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }

    return $Value.Trim().ToLowerInvariant() -in @('1', 'true', 'yes', 'on')
}

function Should-ForceStopNgrokSessions {
    param([hashtable]$Config)

    if (-not $Config.ContainsKey('NGROK_FORCE_STOP_EXISTING_SESSIONS')) {
        return $false
    }

    return Test-TruthyValue -Value $Config['NGROK_FORCE_STOP_EXISTING_SESSIONS']
}

function New-NgrokApiHeaders {
    param([string]$ApiKey)

    return @{
        authorization = "Bearer $ApiKey"
        'ngrok-version' = '2'
    }
}

function Stop-ExistingNgrokSessions {
    param([hashtable]$Config)

    if (-not (Should-ForceStopNgrokSessions -Config $Config)) {
        return $false
    }

    $apiKey = Get-NgrokApiKey -Config $Config
    if ([string]::IsNullOrWhiteSpace($apiKey)) {
        Write-Warning 'NGROK_FORCE_STOP_EXISTING_SESSIONS esta habilitado pero NGROK_API_KEY no esta configurado.'
        return $false
    }

    try {
        $headers = New-NgrokApiHeaders -ApiKey $apiKey
        $response = Invoke-RestMethod -Method Get -Uri 'https://api.ngrok.com/tunnel_sessions' -Headers $headers -TimeoutSec 20
        $sessions = @()
        if ($null -ne $response.tunnel_sessions) {
            $sessions = @($response.tunnel_sessions)
        }
        elseif ($null -ne $response.sessions) {
            $sessions = @($response.sessions)
        }

        foreach ($session in $sessions) {
            if ([string]::IsNullOrWhiteSpace($session.id)) {
                continue
            }

            Invoke-RestMethod -Method Post -Uri "https://api.ngrok.com/tunnel_sessions/$($session.id)/stop" -Headers $headers -TimeoutSec 20 | Out-Null
            Write-Host "Sesion ngrok remota detenida mediante API. ID $($session.id)."
        }

        if ($sessions.Count -gt 0) {
            Start-Sleep -Seconds 3
        }

        return $sessions.Count -gt 0
    }
    catch {
        Write-Warning "No se pudieron cerrar sesiones remotas de ngrok mediante API: $($_.Exception.Message)"
        return $false
    }
}

function Wait-NgrokUrl {
    param([int]$TimeoutSeconds = 60)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-RestMethod -Uri 'http://127.0.0.1:4041/api/tunnels' -TimeoutSec 5
            $url = $response.tunnels |
                ForEach-Object { $_.public_url } |
                Where-Object { $_ -match '^https://' } |
                Select-Object -First 1
            if ($url) {
                return $url
            }
        }
        catch {
        }

        $urlFromLogs = Get-NgrokUrlFromLogs
        if ($urlFromLogs) {
            return $urlFromLogs
        }

        Start-Sleep -Seconds 2
    }

    return $null
}

function Get-NgrokUrlFromLogs {
    try {
        $logs = docker compose --profile crm-ngrok logs --no-color --tail=120 crm-ngrok 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $logs) {
            return $null
        }

        $logLines = @($logs)
        [array]::Reverse($logLines)

        foreach ($line in $logLines) {
            if ($line -match '"url":"(https://[^"]+)"') {
                return $Matches[1]
            }

            if ($line -match 'https://[A-Za-z0-9.-]+\.ngrok(-free)?\.app') {
                return $Matches[0]
            }
        }
    }
    catch {
    }

    return $null
}

function Wait-ExternalUrlReady {
    param(
        [string]$Uri,
        [int]$TimeoutSeconds = 60
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 10 -MaximumRedirection 5
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                return $true
            }
        }
        catch {
            $statusCode = $_.Exception.Response.StatusCode.value__
            if ($statusCode -and $statusCode -lt 500) {
                return $true
            }
        }

        Start-Sleep -Seconds 2
    }

    return $false
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

function Stop-CrmTunnel {
    docker compose --profile crm-ngrok stop crm-ngrok | Out-Null
}

function Stop-LocalNgrokProcesses {
    $localNgrokProcesses = @(Get-Process -Name 'ngrok' -ErrorAction SilentlyContinue)
    foreach ($process in $localNgrokProcesses) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            Write-Host "Proceso ngrok residual detenido. PID $($process.Id)."
        }
        catch {
            Write-Warning "No se pudo detener el proceso ngrok residual con PID $($process.Id)."
        }
    }
}

function Start-CrmExternalTunnel {
    param(
        [hashtable]$Config,
        [int]$Port
    )

    $token = Get-NgrokAuthToken -Config $Config
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'No se encontro authtoken de ngrok para publicar el CRM externamente.'
    }

    $env:NGROK_AUTHTOKEN = $token
    if ([string]::IsNullOrWhiteSpace($env:CRM_EXTERNAL_TARGET_PORT)) {
        $env:CRM_EXTERNAL_TARGET_PORT = "$Port"
    }
    else {
        $env:CRM_EXTERNAL_TARGET_PORT = $env:CRM_EXTERNAL_TARGET_PORT.Trim()
    }

    if ($env:CRM_EXTERNAL_TARGET_PORT -eq '80') {
        Write-Host 'Publicando gateway compartido (Nutri + CRM) en el puerto 80...'
        docker compose --profile public-edge up -d edge-gateway | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw 'No se pudo iniciar el gateway publico (edge-gateway) en el puerto 80.'
        }
    }

    Stop-CrmTunnel
    Stop-LocalNgrokProcesses
    $closedRemoteSessions = Stop-ExistingNgrokSessions -Config $Config
    docker compose --profile crm-ngrok up -d crm-ngrok | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo iniciar el tunel ngrok del CRM.'
    }

    $ngrokUrl = Wait-NgrokUrl -TimeoutSeconds 45
    if (-not $ngrokUrl) {
        $ngrokUrl = Get-NgrokUrlFromLogs
    }

    if (-not $ngrokUrl) {
        $failure = Get-NgrokFailureMessage
        Stop-CrmTunnel
        if ([string]::IsNullOrWhiteSpace($failure)) {
            $failure = 'ngrok no devolvio una URL publica valida para el CRM.'
        }
        elseif ($failure -match 'ERR_NGROK_108') {
            if (-not $closedRemoteSessions) {
                $failure = "$failure Cierra la sesion activa en https://dashboard.ngrok.com/agents o configura NGROK_API_KEY y NGROK_FORCE_STOP_EXISTING_SESSIONS=true para que el script pueda detenerla automaticamente."
            }
        }

        throw $failure
    }

    if (-not (Wait-ExternalUrlReady -Uri $ngrokUrl -TimeoutSeconds 90)) {
        Stop-CrmTunnel
        throw "El tunel ngrok respondio pero el CRM no quedo accesible en $ngrokUrl."
    }

    return $ngrokUrl
}

function Get-LogTail {
    param(
        [string]$Path,
        [int]$Lines = 60
    )

    if (-not (Test-Path $Path)) {
        return $null
    }

    return (Get-Content $Path -Tail $Lines | Out-String).Trim()
}

function Stop-StaleCrmState {
    param([int]$Port)

    $existingCrm = Get-RunningCrmProcess -PidPath $pidFile
    if ($existingCrm) {
        try {
            Stop-Process -Id $existingCrm.Id -Force -ErrorAction Stop
            Write-Host "CRM detenido. PID $($existingCrm.Id)."
        }
        catch {
            Write-Warning "No se pudo detener el proceso CRM con PID $($existingCrm.Id)."
        }
    }

    Stop-CrmListeners -Port $Port
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force -Path $runDir | Out-Null
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

Push-Location $root
try {
    if (-not (Test-Path $envFile)) {
        throw 'No existe .env. Ejecuta scripts\bootstrap.ps1 primero.'
    }

    $config = Read-EnvFile -Path $envFile
    Export-EnvConfig -Config $config

    if ([string]::IsNullOrWhiteSpace($env:OLLAMA_BASE_URL)) {
        $env:OLLAMA_BASE_URL = 'http://127.0.0.1:11434'
    }

    if ([string]::IsNullOrWhiteSpace($env:OLLAMA_CHAT_MODEL)) {
        if ($config.ContainsKey('OLLAMA_CHAT_MODEL') -and -not [string]::IsNullOrWhiteSpace($config['OLLAMA_CHAT_MODEL'])) {
            $env:OLLAMA_CHAT_MODEL = $config['OLLAMA_CHAT_MODEL'].Trim()
        }
        elseif ($config.ContainsKey('OLLAMA_GENERAL_MODEL') -and -not [string]::IsNullOrWhiteSpace($config['OLLAMA_GENERAL_MODEL'])) {
            $env:OLLAMA_CHAT_MODEL = $config['OLLAMA_GENERAL_MODEL'].Trim()
        }
    }

    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $dotnet = Resolve-DotnetExecutable

    $publicBaseIp = Resolve-PublicBaseIp -Config $config
    $isAdministrator = Test-IsAdministrator
    $primaryNetworkCategory = Get-PrimaryNetworkCategory
    $n8nPort = if ($config.ContainsKey('N8N_PORT') -and $config['N8N_PORT']) { [int]$config['N8N_PORT'] } else { 5678 }
    $mailpitUiPort = 8025
    $mailpitSmtpPort = 1025
    $healthUri = "http://127.0.0.1:$CrmPort/api/health"

    if (-not $isAdministrator -and $primaryNetworkCategory -eq 'Public') {
        Write-Warning 'La red activa usa perfil Public y esta consola no tiene permisos de administrador. Windows puede bloquear accesos entrantes locales por IP aunque el CRM este arriba.'
    }

    $env:N8N_HOST = $publicBaseIp
    $env:WEBHOOK_URL = "http://$publicBaseIp`:$n8nPort/"
    $env:N8N_EDITOR_BASE_URL = "http://$publicBaseIp`:$n8nPort/"
    $env:ASPNETCORE_URLS = "http://0.0.0.0:$CrmPort"
    $env:ASPNETCORE_ENVIRONMENT = 'Production'

    Ensure-FirewallRule -Name 'Automatizacion CRM 5050' -Port $CrmPort
    Ensure-FirewallRule -Name 'Automatizacion n8n 5678' -Port $n8nPort
    Ensure-FirewallRule -Name 'Automatizacion Mailpit UI 8025' -Port $mailpitUiPort
    Ensure-FirewallRule -Name 'Automatizacion Mailpit SMTP 1025' -Port $mailpitSmtpPort
    if ($config.ContainsKey('CRM_EXTERNAL_TARGET_PORT') -and -not [string]::IsNullOrWhiteSpace($config['CRM_EXTERNAL_TARGET_PORT']) -and $config['CRM_EXTERNAL_TARGET_PORT'].Trim() -eq '80') {
        Ensure-FirewallRule -Name 'Automatizacion Edge Gateway 80' -Port 80
    }

    if ($config.ContainsKey('LOCAL_POSTGRES_MANAGED') -and (Test-TruthyValue -Value $config['LOCAL_POSTGRES_MANAGED'])) {
        powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\start-local-postgres.ps1')
    }

    if ($Build -or -not (Test-Path $frontendIndex) -or -not (Test-Path $backendDll)) {
        Write-Host 'Compilando CRM antes de iniciar...'
        powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\build-crm.ps1')
    }

    if (-not (Test-Path $frontendIndex)) {
        throw "No se encontro $frontendIndex. La compilacion del frontend no quedo disponible."
    }

    if (-not (Test-Path $backendDll)) {
        throw "No se encontro $backendDll. La compilacion del backend no quedo disponible."
    }

    if (-not $SkipDocker) {
        Write-Host 'Iniciando servicios Docker internos: mailpit y n8n...'
        docker compose up -d mailpit n8n
        if ($LASTEXITCODE -ne 0) {
            throw 'No se pudieron iniciar los servicios Docker internos.'
        }

        if (Wait-HttpEndpoint -Uri "http://127.0.0.1:$n8nPort" -TimeoutSeconds 90) {
            Write-Host 'Sincronizando workflows de n8n...'
            powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\import-workflows.ps1') -RestartN8n
        }
        else {
            Write-Warning 'n8n no respondio a tiempo para sincronizar workflows automaticamente.'
        }
    }

    $existingCrm = Get-RunningCrmProcess -PidPath $pidFile
    $hasUnmanagedListener = -not $existingCrm -and (Get-ListeningCrmPids -Port $CrmPort).Count -gt 0
    $crmIsHealthy = $existingCrm -and (Wait-HttpEndpoint -Uri $healthUri -TimeoutSeconds 10) -and (Test-CrmHasNetworkBinding -Port $CrmPort)

    if ($existingCrm -and -not $crmIsHealthy) {
        Write-Host "Se detecto un CRM inconsistente con PID $($existingCrm.Id). Reiniciando..."
        Stop-StaleCrmState -Port $CrmPort
        $existingCrm = $null
        $crmIsHealthy = $false
    }
    elseif ($hasUnmanagedListener) {
        Write-Host 'Se detecto un listener CRM no gestionado por el script. Reiniciando para estabilizar el estado...'
        Stop-StaleCrmState -Port $CrmPort
    }

    if (-not $crmIsHealthy) {
        Remove-Item $stdoutLog -Force -ErrorAction SilentlyContinue
        Remove-Item $stderrLog -Force -ErrorAction SilentlyContinue

        Write-Host "Iniciando CRM en http://0.0.0.0:$CrmPort ..."
        $process = Start-Process $dotnet -ArgumentList 'backend\bin\Debug\net10.0\backend.dll' -WorkingDirectory $root -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru
        Set-Content -Path $pidFile -Value $process.Id -Encoding ASCII

        if (-not (Wait-CrmReady -Port $CrmPort -TimeoutSeconds 90)) {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }

            Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
            $stdoutTail = Get-LogTail -Path $stdoutLog
            $stderrTail = Get-LogTail -Path $stderrLog
            throw "El CRM no quedo listo en $healthUri.`nSTDOUT:`n$stdoutTail`nSTDERR:`n$stderrTail"
        }

        $existingCrm = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
    }

    if (-not $existingCrm) {
        throw 'El CRM no quedo ejecutandose despues del arranque.'
    }

    $crmExternalUrl = $null
    if (-not $SkipDocker) {
        Remove-Item $crmExternalUrlFile -Force -ErrorAction SilentlyContinue
        if (-not $SkipCrmTunnel) {
            $crmExternalUrl = Start-CrmExternalTunnel -Config $config -Port $CrmPort
            Set-Content -Path $crmExternalUrlFile -Value $crmExternalUrl -Encoding ASCII
        }
        else {
            Stop-CrmTunnel
        }
    }

    Write-Host "CRM listo en http://127.0.0.1:$CrmPort"
    if ($crmExternalUrl) {
        Write-Host "CRM externo publicado en $crmExternalUrl"
        Write-Host "URL guardada en $crmExternalUrlFile"
    }

    if (-not $SkipDocker) {
        Write-Host "n8n interno disponible en http://127.0.0.1:$n8nPort"
        Write-Host "Mailpit interno disponible en http://127.0.0.1:$mailpitUiPort"
        Write-Host "SMTP local disponible en 127.0.0.1:$mailpitSmtpPort"
    }

    Write-Host 'Logs:'
    Write-Host "  $stdoutLog"
    Write-Host "  $stderrLog"
}
finally {
    Pop-Location
}
