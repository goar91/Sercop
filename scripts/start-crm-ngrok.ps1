param(
    [int]$Port = 5050
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
$runDir = Join-Path $root 'run'
$urlFile = Join-Path $runDir 'crm-external-url.txt'

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

function Wait-HttpEndpoint {
    param(
        [string]$Uri,
        [int]$TimeoutSeconds = 30
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

function Wait-NgrokUrl {
    param([int]$TimeoutSeconds = 60)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $payload = Invoke-RestMethod -Uri 'http://127.0.0.1:4041/api/tunnels' -TimeoutSec 5
            $url = $payload.tunnels |
                ForEach-Object { $_.public_url } |
                Where-Object { $_ -match '^https://' } |
                Select-Object -First 1
            if ($url) {
                return $url
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
        if ($joined -match 'ERR_NGROK_4018') {
            return 'ERR_NGROK_4018: ngrok requiere una cuenta verificada y un authtoken valido.'
        }

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

New-Item -ItemType Directory -Force -Path $runDir | Out-Null

$config = Read-EnvFile -Path $envFile
$token = Get-NgrokAuthToken -Config $config

if (-not (Wait-HttpEndpoint -Uri "http://localhost:$Port" -TimeoutSeconds 30)) {
    throw "El CRM no responde en http://localhost:$Port. Inicia primero el CRM antes de abrir el tunel."
}

if ([string]::IsNullOrWhiteSpace($token)) {
    throw 'No se encontro NGROK_AUTHTOKEN en .env ni en la configuracion local de ngrok.'
}

Push-Location $root
try {
    $env:NGROK_AUTHTOKEN = $token
    $env:CRM_EXTERNAL_TARGET_PORT = "$Port"

    docker compose --profile crm-ngrok up -d crm-ngrok | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo iniciar el contenedor crm-ngrok.'
    }

    $url = Wait-NgrokUrl -TimeoutSeconds 60
    if (-not $url) {
        $failure = Get-NgrokFailureMessage
        if ($failure) {
            throw $failure
        }

        throw 'ngrok no devolvio una URL publica valida para el CRM.'
    }

    Set-Content -Path $urlFile -Value $url -Encoding ASCII
    Write-Host "CRM publicado en $url"
    Write-Host "Archivo: $urlFile"
}
finally {
    Pop-Location
}
