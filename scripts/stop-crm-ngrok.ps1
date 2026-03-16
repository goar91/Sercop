param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$urlFile = Join-Path $root 'run\crm-external-url.txt'

Push-Location $root
try {
    docker compose --profile crm-ngrok stop crm-ngrok | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo detener el contenedor crm-ngrok.'
    }

    Remove-Item $urlFile -Force -ErrorAction SilentlyContinue
    Write-Host 'Tunel ngrok del CRM detenido.'
}
finally {
    Pop-Location
}
