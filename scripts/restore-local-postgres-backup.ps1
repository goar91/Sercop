param(
    [Parameter(Mandatory)]
    [string]$BackupFile,
    [Parameter(Mandatory)]
    [string]$AdminPassword,
    [string]$AdminUser = 'postgres',
    [string]$DbHost = 'localhost',
    [int]$Port = 5432
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'PostgresTools.ps1')

$envPath = Join-Path $root '.env'
$config = Read-EnvConfig -Path $envPath
$psql = Find-PostgresExecutable -Name 'psql.exe'
$pgRestore = Find-PostgresExecutable -Name 'pg_restore.exe'

if (-not (Test-Path $BackupFile)) {
    throw "No existe el respaldo indicado: $BackupFile"
}

$appDb = $config['POSTGRES_DB']
$appUser = $config['POSTGRES_USER']
$appPassword = $config['POSTGRES_PASSWORD']

if (-not $appDb -or -not $appUser -or -not $appPassword) {
    throw 'POSTGRES_DB, POSTGRES_USER o POSTGRES_PASSWORD no estan definidos en .env'
}

function Invoke-Psql {
    param(
        [Parameter(Mandatory)]
        [string]$Database,
        [Parameter(Mandatory)]
        [string]$Sql
    )

    & $psql -h $DbHost -p $Port -U $AdminUser -d $Database -v ON_ERROR_STOP=1 -c $Sql
    if ($LASTEXITCODE -ne 0) {
        throw "psql devolvio codigo $LASTEXITCODE ejecutando SQL sobre $Database."
    }
}

$env:PGPASSWORD = $AdminPassword

try {
    $roleExists = & $psql -h $DbHost -p $Port -U $AdminUser -d postgres -tAc "SELECT 1 FROM pg_roles WHERE rolname = '$appUser';"
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo verificar el rol $appUser."
    }

    if (-not $roleExists.Trim()) {
        Invoke-Psql -Database 'postgres' -Sql "CREATE ROLE $appUser WITH LOGIN PASSWORD '$appPassword';"
    }
    else {
        Invoke-Psql -Database 'postgres' -Sql "ALTER ROLE $appUser WITH LOGIN PASSWORD '$appPassword';"
    }

    $dbExists = & $psql -h $DbHost -p $Port -U $AdminUser -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$appDb';"
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo verificar la base $appDb."
    }

    if (-not $dbExists.Trim()) {
        Invoke-Psql -Database 'postgres' -Sql "CREATE DATABASE $appDb OWNER $appUser;"
    }

    Invoke-Psql -Database 'postgres' -Sql "GRANT ALL PRIVILEGES ON DATABASE $appDb TO $appUser;"
    Invoke-Psql -Database 'postgres' -Sql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$appDb' AND pid <> pg_backend_pid();"

    & $pgRestore -h $DbHost -p $Port -U $AdminUser -d $appDb --clean --if-exists --no-owner --no-privileges --verbose $BackupFile
    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore devolvio codigo $LASTEXITCODE restaurando $BackupFile."
    }
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

Write-Host "Respaldo restaurado correctamente en $appDb" -ForegroundColor Green
