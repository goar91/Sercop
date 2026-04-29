param(
    [switch]$Build,
    [int]$Port = 5050
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$startSystemScript = Join-Path $root 'scripts\start-system.ps1'

if (-not (Test-Path $startSystemScript)) {
    throw "No se encontro $startSystemScript."
}

$arguments = @(
    '-NoProfile'
    '-ExecutionPolicy'
    'Bypass'
    '-File'
    $startSystemScript
    '-SkipDocker'
    '-SkipCrmTunnel'
    '-CrmPort'
    "$Port"
)

if ($Build) {
    $arguments += '-Build'
}

powershell @arguments
