$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
$runDir = Join-Path $root 'run'

. (Join-Path $PSScriptRoot 'PostgresTools.ps1')

function Resolve-ProjectPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $root $Path
}

$dataDir = Join-Path $runDir 'postgres-data'
if (Test-Path $envFile) {
    $config = Read-EnvConfig -Path $envFile
    if ($config.ContainsKey('LOCAL_POSTGRES_DATA_DIR') -and $config['LOCAL_POSTGRES_DATA_DIR']) {
        $dataDir = Resolve-ProjectPath -Path $config['LOCAL_POSTGRES_DATA_DIR']
    }
}

if (-not (Test-Path (Join-Path $dataDir 'PG_VERSION'))) {
    return
}

$pgCtl = Find-PostgresExecutable -Name 'pg_ctl.exe'
$status = & $pgCtl status -D $dataDir 2>&1
if ($LASTEXITCODE -ne 0) {
    return
}

Write-Host 'Deteniendo PostgreSQL local...'
& $pgCtl stop -D $dataDir -m fast -w
if ($LASTEXITCODE -ne 0) {
    throw "pg_ctl stop devolvio codigo $LASTEXITCODE."
}

