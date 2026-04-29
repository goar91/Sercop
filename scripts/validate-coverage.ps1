param(
    [switch]$NoLive
)

$ErrorActionPreference = 'Stop'

$auditScript = Join-Path $PSScriptRoot 'audit-system.ps1'
if (-not (Test-Path $auditScript)) {
    throw "No existe $auditScript"
}

$argsList = @('-PortalAudit', '-FailOnMissing')
if (-not $NoLive) {
    $argsList += '-Live'
}

powershell -NoProfile -ExecutionPolicy Bypass -File $auditScript @argsList
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

