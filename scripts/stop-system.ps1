param(
    [switch]$SkipDocker,
    [switch]$KeepCrmTunnel
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pidFile = Join-Path $root 'run\crm.pid'
$crmExternalUrlFile = Join-Path $root 'run\crm-external-url.txt'
$crmPort = 5050
$localPostgresStopScript = Join-Path $root 'scripts\stop-local-postgres.ps1'

function Stop-CrmFromPidFile {
    param([string]$PidPath)

    if (-not (Test-Path $PidPath)) {
        return
    }

    $rawPid = (Get-Content $PidPath -ErrorAction SilentlyContinue | Select-Object -First 1).Trim()
    if (-not $rawPid) {
        Remove-Item $PidPath -Force -ErrorAction SilentlyContinue
        return
    }

    $process = Get-Process -Id ([int]$rawPid) -ErrorAction SilentlyContinue
    if (-not $process) {
        Remove-Item $PidPath -Force -ErrorAction SilentlyContinue
        return
    }

    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
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

function Stop-LocalNgrokProcesses {
    $localNgrokProcesses = @(Get-Process -Name 'ngrok' -ErrorAction SilentlyContinue)
    foreach ($process in $localNgrokProcesses) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Write-Host "Proceso ngrok residual detenido. PID $($process.Id)."
    }
}

Push-Location $root
try {
    Stop-CrmFromPidFile -PidPath $pidFile
    Stop-CrmListenersByPort -Port $crmPort
    if (-not $KeepCrmTunnel) {
        Remove-Item $crmExternalUrlFile -Force -ErrorAction SilentlyContinue
    }

    if (-not $KeepCrmTunnel) {
        docker compose --profile crm-ngrok stop crm-ngrok | Out-Null
        Stop-LocalNgrokProcesses
    }

    if (-not $SkipDocker) {
        Write-Host 'Deteniendo servicios Docker internos: n8n y mailpit...'
        docker compose stop n8n mailpit
    }

    if (Test-Path $localPostgresStopScript) {
        powershell -NoProfile -ExecutionPolicy Bypass -File $localPostgresStopScript
    }
}
finally {
    Pop-Location
}
