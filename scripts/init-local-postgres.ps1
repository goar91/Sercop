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
    if ($LASTEXITCODE -ne 0) {
        throw "psql devolvio codigo $LASTEXITCODE ejecutando SQL sobre $Database."
    }
}

function Invoke-PsqlFile([string]$Database, [string]$Path) {
    & $psql -h $DbHost -p $Port -U $AdminUser -d $Database -v ON_ERROR_STOP=1 -f $Path
    if ($LASTEXITCODE -ne 0) {
        throw "psql devolvio codigo $LASTEXITCODE ejecutando el script $Path sobre $Database."
    }
}

$env:PGPASSWORD = $AdminPassword
try {
    $roleExists = & $psql -h $DbHost -p $Port -U $AdminUser -d postgres -tAc "SELECT 1 FROM pg_roles WHERE rolname = '$appUser';"
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo consultar la existencia del rol de aplicacion.'
    }
    if (-not ($roleExists -match '1')) {
        Invoke-Psql 'postgres' "CREATE ROLE $appUser WITH LOGIN PASSWORD '$appPassword';"
    }

    $dbExists = & $psql -h $DbHost -p $Port -U $AdminUser -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$appDb';"
    if ($LASTEXITCODE -ne 0) {
        throw 'No se pudo consultar la existencia de la base local.'
    }
    if (-not ($dbExists -match '1')) {
        Invoke-Psql 'postgres' "CREATE DATABASE $appDb OWNER $appUser;"
    }

    Invoke-Psql 'postgres' "GRANT ALL PRIVILEGES ON DATABASE $appDb TO $appUser;"

    foreach ($scriptName in @('001_schema.sql', '002_invitation_filter.sql', '004_url_fix.sql', '003_crm.sql', '005_permissions.sql', '006_performance_indexes.sql', '007_keyword_rules_management.sql', '008_invitation_tracking.sql', '009_supply_filter_rules.sql', '010_exclude_medical_rules.sql', '011_data_integrity.sql', '012_modernization_foundation.sql', '013_keyword_refresh_and_search.sql', '014_keyword_only_and_sercop_credentials.sql', '015_sercop_credential_hardening.sql', '016_opportunity_classification.sql', '017_chemistry_classification_refinement.sql', '018_chemistry_classification_refinement_food_petro.sql')) {
        $scriptPath = Join-Path $root "database\init\$scriptName"
        if (-not (Test-Path $scriptPath)) {
            throw "No se encontro el script $scriptPath"
        }
        Invoke-PsqlFile $appDb $scriptPath
    }

    if ($config.ContainsKey('RESPONSIBLE_EMAIL') -and -not [string]::IsNullOrWhiteSpace($config['RESPONSIBLE_EMAIL'])) {
        $responsibleEmail = $config['RESPONSIBLE_EMAIL'].Trim().ToLower()
        $escapedEmail = $responsibleEmail.Replace("'", "''")
        $managerSql = @"
INSERT INTO crm_users (full_name, email, role, active, login_name)
VALUES ('Responsable Comercial', '$escapedEmail', 'gerencia', TRUE, split_part('$escapedEmail', '@', 1))
ON CONFLICT (email) DO UPDATE
SET full_name = EXCLUDED.full_name,
    role = EXCLUDED.role,
    active = EXCLUDED.active,
    login_name = COALESCE(crm_users.login_name, split_part(EXCLUDED.email, '@', 1)),
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




