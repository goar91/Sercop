param(
    [int]$Port = 0
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
$logDir = Join-Path $root 'logs'
$runDir = Join-Path $root 'run'
$logFile = Join-Path $logDir 'postgres-local.log'

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

if (-not (Test-Path $envFile)) {
    throw 'No existe .env. Ejecuta scripts\bootstrap.ps1 primero.'
}

$config = Read-EnvConfig -Path $envFile

if ($Port -le 0) {
    $Port = if ($config.ContainsKey('CRM_DB_PORT') -and $config['CRM_DB_PORT']) {
        [int]$config['CRM_DB_PORT']
    }
    else {
        5432
    }
}

$dataDir = if ($config.ContainsKey('LOCAL_POSTGRES_DATA_DIR') -and $config['LOCAL_POSTGRES_DATA_DIR']) {
    Resolve-ProjectPath -Path $config['LOCAL_POSTGRES_DATA_DIR']
}
else {
    Join-Path $runDir 'postgres-data'
}

if (-not $config.ContainsKey('POSTGRES_PASSWORD') -or [string]::IsNullOrWhiteSpace($config['POSTGRES_PASSWORD'])) {
    throw 'POSTGRES_PASSWORD no esta definido en .env.'
}

New-Item -ItemType Directory -Force -Path $logDir | Out-Null
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

$pgCtl = Find-PostgresExecutable -Name 'pg_ctl.exe'
$initDb = Find-PostgresExecutable -Name 'initdb.exe'
$pgIsReady = Find-PostgresExecutable -Name 'pg_isready.exe'

if (-not (Test-Path (Join-Path $dataDir 'PG_VERSION'))) {
    Write-Host "Inicializando PostgreSQL local en $dataDir ..."
    $passwordFile = Join-Path $runDir 'postgres-local.pw.tmp'
    try {
        Set-Content -Path $passwordFile -Value $config['POSTGRES_PASSWORD'] -NoNewline -Encoding ASCII
        & $initDb -D $dataDir -U postgres "--pwfile=$passwordFile" --auth-host=scram-sha-256 --auth-local=scram-sha-256 --encoding=UTF8
        if ($LASTEXITCODE -ne 0) {
            throw "initdb devolvio codigo $LASTEXITCODE."
        }
    }
    finally {
        Remove-Item $passwordFile -Force -ErrorAction SilentlyContinue
    }

    Add-Content -Path (Join-Path $dataDir 'postgresql.conf') -Value @"

# Automatizacion local PostgreSQL
port = $Port
listen_addresses = '*'
"@

    Add-Content -Path (Join-Path $dataDir 'pg_hba.conf') -Value @"

# Automatizacion local and Docker Desktop access
host    all             all             127.0.0.1/32            scram-sha-256
host    all             all             ::1/128                 scram-sha-256
host    all             all             172.16.0.0/12           scram-sha-256
host    all             all             192.168.0.0/16          scram-sha-256
"@
}

$status = & $pgCtl status -D $dataDir 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "PostgreSQL local ya esta iniciado en puerto $Port."
}
else {
    Write-Host "Iniciando PostgreSQL local en puerto $Port ..."
    & $pgCtl start -D $dataDir -l $logFile -w
    if ($LASTEXITCODE -ne 0) {
        throw "pg_ctl start devolvio codigo $LASTEXITCODE."
    }
}

& $pgIsReady -h localhost -p $Port -d postgres | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "PostgreSQL local no respondio en localhost:$Port."
}

