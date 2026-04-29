param(
    [string]$OutputRoot,
    [switch]$SkipDatabaseBackup,
    [int]$DatabaseBackupJobs = 4,
    [switch]$IncludeN8nExecutionPayloads
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

. (Join-Path $PSScriptRoot 'PostgresTools.ps1')

if (-not $OutputRoot) {
    $OutputRoot = Join-Path $root 'backups\deployment-bundles'
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$bundleRoot = Join-Path $OutputRoot "HDM-CRM-migration-$timestamp"
$packageRoot = Join-Path $bundleRoot 'package'
$databaseBackupRoot = Join-Path $bundleRoot 'database-backup'

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
New-Item -ItemType Directory -Path $databaseBackupRoot -Force | Out-Null

$envPath = Join-Path $root '.env'
$config = Read-EnvConfig -Path $envPath

function Copy-DirectoryFiltered {
    param(
        [Parameter(Mandatory)]
        [string]$Source,
        [Parameter(Mandatory)]
        [string]$Destination
    )

    $robocopyArgs = @(
        $Source,
        $Destination,
        '/E',
        '/R:2',
        '/W:2',
        '/NFL',
        '/NDL',
        '/NJH',
        '/NJS',
        '/NP',
        '/XD',
        'bin',
        'obj',
        'build-check',
        'dist',
        'node_modules',
        '.angular',
        'run',
        'logs',
        'tmp',
        'backups',
        '.git',
        '.dotnet'
    )

    & robocopy @robocopyArgs | Out-Null

    if ($LASTEXITCODE -gt 7) {
        throw "robocopy devolvio codigo $LASTEXITCODE copiando $Source."
    }
}

$rootFiles = @(
    'Automatización.sln',
    'docker-compose.yml',
    'iniciar-automatizacion.cmd',
    'detener-automatizacion.cmd',
    '.env.example',
    'README.md',
    'PROPUESTA_MODERNIZACION.md'
)

foreach ($file in $rootFiles) {
    $source = Join-Path $root $file
    if (Test-Path $source) {
        Copy-Item $source -Destination (Join-Path $packageRoot $file) -Force
    }
}

Copy-Item $envPath -Destination (Join-Path $packageRoot '.env.current') -Force

$directoriesToCopy = @(
    'backend',
    'frontend',
    'database',
    'docs',
    'scripts',
    'workflows',
    'config',
    'knowledge'
)

foreach ($directory in $directoriesToCopy) {
    $source = Join-Path $root $directory
    $destination = Join-Path $packageRoot $directory
    if (-not (Test-Path $source)) {
        continue
    }

    Copy-DirectoryFiltered -Source $source -Destination $destination
}

$tempCredentials = Join-Path $packageRoot 'config\temp-credentials.json'
if (Test-Path $tempCredentials) {
    Remove-Item $tempCredentials -Force
}

$deploymentGuideSource = Join-Path $root 'docs\deploy-on-new-pc.md'
if (Test-Path $deploymentGuideSource) {
    Copy-Item $deploymentGuideSource -Destination (Join-Path $bundleRoot 'DEPLOYMENT_GUIDE.md') -Force
}

$commit = git rev-parse HEAD
if ($LASTEXITCODE -ne 0) {
    throw 'No se pudo obtener el commit actual.'
}

$databaseBackupFile = $null
if (-not $SkipDatabaseBackup) {
    $pgDump = Find-PostgresExecutable -Name 'pg_dump.exe'
    $dbHost = $config['CRM_DB_HOST']
    if (-not $dbHost) {
        $dbHost = 'localhost'
    }

    $dbPort = $config['CRM_DB_PORT']
    if (-not $dbPort) {
        $dbPort = '5432'
    }

    $databaseBackupFile = Join-Path $databaseBackupRoot "$($config['POSTGRES_DB'])-$timestamp.dir"
    $env:PGPASSWORD = $config['POSTGRES_PASSWORD']

    try {
        $pgDumpArgs = @(
            '-h', $dbHost,
            '-p', $dbPort,
            '-U', $config['POSTGRES_USER'],
            '-d', $config['POSTGRES_DB'],
            '-F', 'd',
            '-f', $databaseBackupFile
        )

        if ($DatabaseBackupJobs -gt 1) {
            $pgDumpArgs += @('-j', $DatabaseBackupJobs)
        }

        if (-not $IncludeN8nExecutionPayloads) {
            $pgDumpArgs += '--exclude-table=public.execution_data'
        }

        & $pgDump @pgDumpArgs
        if ($LASTEXITCODE -ne 0) {
            throw "pg_dump devolvio codigo $LASTEXITCODE."
        }
    }
    finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    }
}

$manifest = @(
    "bundle_created_at=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
    "source_commit=$commit",
    "source_root=$root",
    "package_root=$packageRoot",
    "database_backup=$databaseBackupFile",
    "database_backup_jobs=$DatabaseBackupJobs",
    "includes_n8n_execution_payloads=$IncludeN8nExecutionPayloads",
    "database_name=$($config['POSTGRES_DB'])",
    "database_user=$($config['POSTGRES_USER'])",
    "crm_port=5050",
    "n8n_port=5678",
    "mailpit_port=8025"
)

Set-Content -Path (Join-Path $bundleRoot 'BUNDLE_MANIFEST.txt') -Value $manifest

Write-Host "Bundle creado en: $bundleRoot" -ForegroundColor Green
