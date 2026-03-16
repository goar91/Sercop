param(
    [string]$AdminUser = 'postgres',
    [string]$AdminPassword = 'ChangeDB3001',
    [string]$DbHost = 'localhost',
    [int]$Port = 5432
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $root '.env'
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'

if (-not (Test-Path $envPath)) {
    throw 'No existe .env. Ejecuta scripts\bootstrap.ps1 primero.'
}

if (-not (Test-Path $psql)) {
    throw "No se encontro psql en $psql"
}

$config = @{}
Get-Content $envPath | ForEach-Object {
    if ($_ -match '^[A-Za-z0-9_]+=' -and -not $_.StartsWith('#')) {
        $parts = $_.Split('=', 2)
        $config[$parts[0]] = $parts[1]
    }
}

$appDb = $config['POSTGRES_DB']
$appUser = $config['POSTGRES_USER']
$appPassword = $config['POSTGRES_PASSWORD']

if (-not $appDb -or -not $appUser -or -not $appPassword) {
    throw 'POSTGRES_DB, POSTGRES_USER o POSTGRES_PASSWORD no estan definidos en .env'
}

function Invoke-Psql([string]$Database, [string]$Sql) {
    & $psql -h $DbHost -p $Port -U $AdminUser -d $Database -v ON_ERROR_STOP=1 -c $Sql
}

$env:PGPASSWORD = $AdminPassword
try {
    $roleExists = & $psql -h $DbHost -p $Port -U $AdminUser -d postgres -tAc "SELECT 1 FROM pg_roles WHERE rolname = '$appUser';"
    if (-not ($roleExists -match '1')) {
        Invoke-Psql 'postgres' "CREATE ROLE $appUser WITH LOGIN PASSWORD '$appPassword';"
    }

    $dbExists = & $psql -h $DbHost -p $Port -U $AdminUser -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$appDb';"
    if (-not ($dbExists -match '1')) {
        Invoke-Psql 'postgres' "CREATE DATABASE $appDb OWNER $appUser;"
    }

    Invoke-Psql 'postgres' "GRANT ALL PRIVILEGES ON DATABASE $appDb TO $appUser;"

    foreach ($scriptName in @('001_schema.sql', '002_invitation_filter.sql', '004_url_fix.sql', '003_crm.sql', '005_permissions.sql', '006_performance_indexes.sql', '007_keyword_rules_management.sql', '008_invitation_tracking.sql')) {
        $scriptPath = Join-Path $root "database\init\$scriptName"
        if (-not (Test-Path $scriptPath)) {
            throw "No se encontro el script $scriptPath"
        }
        & $psql -h $DbHost -p $Port -U $AdminUser -d $appDb -v ON_ERROR_STOP=1 -f $scriptPath
    }

    if ($config.ContainsKey('RESPONSIBLE_EMAIL') -and -not [string]::IsNullOrWhiteSpace($config['RESPONSIBLE_EMAIL'])) {
        $responsibleEmail = $config['RESPONSIBLE_EMAIL'].Trim().ToLower()
        $escapedEmail = $responsibleEmail.Replace("'", "''")
        $managerSql = @"
INSERT INTO crm_users (full_name, email, role, active)
VALUES ('Responsable Comercial', '$escapedEmail', 'manager', TRUE)
ON CONFLICT (email) DO UPDATE
SET full_name = EXCLUDED.full_name,
    role = EXCLUDED.role,
    active = EXCLUDED.active,
    updated_at = NOW();

DELETE FROM crm_users
WHERE email = 'licitaciones@example.com';
"@
        Invoke-Psql $appDb $managerSql
    }
}
finally {
    $env:PGPASSWORD = ''
}

Write-Host "Base local lista: $appDb"
Write-Host "Usuario de aplicacion: $appUser"




