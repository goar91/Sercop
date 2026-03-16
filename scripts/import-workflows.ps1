param(
    [switch]$SkipActivation
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$workflowDir = Join-Path $root "workflows"

if (-not (Test-Path $workflowDir)) {
    throw "No existe la carpeta workflows"
}

$files = Get-ChildItem $workflowDir -Filter *.json | Sort-Object Name
if (-not $files) {
    throw "No se encontraron workflows JSON para importar."
}

Write-Host 'Provisionando credenciales de n8n...'
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\provision-n8n-credentials.ps1')
if ($LASTEXITCODE -ne 0) {
    throw "La provision de credenciales n8n fallo con codigo $LASTEXITCODE."
}

Write-Host "Importando workflows desde $workflowDir ..."
docker compose exec -T n8n n8n import:workflow --separate --input=/import/workflows | Out-Host

if (-not $SkipActivation) {
    $desiredStates = @{
        '1001' = $true
        '1002' = $true
        '1003' = $true
        '1004' = $true
        '1005' = $true
        '1006' = $false
    }

    foreach ($workflowId in $desiredStates.Keys) {
        $isActive = $desiredStates[$workflowId].ToString().ToLowerInvariant()
        Write-Host "Actualizando estado del workflow $workflowId -> active=$isActive ..."
        if ($desiredStates[$workflowId]) {
            docker compose exec -T n8n n8n publish:workflow --id=$workflowId | Out-Host
        }
        else {
            docker compose exec -T n8n n8n unpublish:workflow --id=$workflowId | Out-Host
        }
    }
}

Write-Host "Importacion completada."
