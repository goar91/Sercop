param(
    [switch]$SkipDocker,
    [switch]$KeepNgrok,
    [switch]$KeepCrmTunnel
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pidFile = Join-Path $root 'run\crm.pid'
$crmExternalUrlFile = Join-Path $root 'run\crm-external-url.txt'
$crmPort = 5050

function Stop-CrmFromPidFile {
    param([string]$PidPath)

    if (-not (Test-Path $PidPath)) {
        Write-Host 'CRM no tiene PID registrado. Nada que detener.'
        return
    }

    $rawPid = (Get-Content $PidPath -ErrorAction SilentlyContinue | Select-Object -First 1).Trim()
    if (-not $rawPid) {
        Remove-Item $PidPath -Force -ErrorAction SilentlyContinue
        Write-Host 'PID vacio. Archivo limpiado.'
        return
    }

    $process = Get-Process -Id ([int]$rawPid) -ErrorAction SilentlyContinue
    if (-not $process) {
        Remove-Item $PidPath -Force -ErrorAction SilentlyContinue
        Write-Host 'El proceso del CRM ya no estaba activo. Archivo PID limpiado.'
        return
    }

    Stop-Process -Id $process.Id -Force
    Remove-Item $PidPath -Force -ErrorAction SilentlyContinue
    Write-Host "CRM detenido. PID $($process.Id)."
}

function Stop-CrmListenersByPort {
    param([int]$Port)

    try {
        $pids = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop |
            Select-Object -ExpandProperty OwningProcess -Unique)
    }
    catch {
        $pids = @()
    }

    foreach ($owningProcessId in $pids) {
        $process = Get-Process -Id $owningProcessId -ErrorAction SilentlyContinue
        if (-not $process) {
            continue
        }

        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Write-Host "CRM detenido por puerto $Port. PID $($process.Id)."
    }
}

Push-Location $root
try {
    Stop-CrmFromPidFile -PidPath $pidFile
    Stop-CrmListenersByPort -Port $crmPort
    Remove-Item $crmExternalUrlFile -Force -ErrorAction SilentlyContinue

    if (-not $SkipDocker) {
        Write-Host 'Deteniendo servicios Docker: n8n, mailpit, ollama y qdrant...'
        docker compose stop n8n mailpit ollama qdrant

        if (-not $KeepNgrok) {
            Write-Host 'Deteniendo tunel ngrok...'
            docker compose --profile tunnel stop ngrok
        }

        if (-not $KeepCrmTunnel) {
            Write-Host 'Deteniendo tunel externo del CRM...'
            docker compose --profile crm-tunnel stop crm-cloudflared
            docker compose --profile crm-ngrok stop crm-ngrok
        }
    }
}
finally {
    Pop-Location
}

