param(
    [switch]$SkipActivation
    ,[switch]$RestartN8n
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

Write-Host "Importando workflows desde $workflowDir ..."
docker compose exec -T n8n n8n import:workflow --separate --input=/import/workflows | Out-Host

if (-not $SkipActivation) {
    $envPath = Join-Path $root '.env'
    $toolsPath = Join-Path $PSScriptRoot 'PostgresTools.ps1'

    if (-not (Test-Path $envPath)) {
        Write-Warning 'No existe .env para activar workflows automaticamente.'
    }
    elseif (-not (Test-Path $toolsPath)) {
        Write-Warning 'No existe scripts\\PostgresTools.ps1 para activar workflows automaticamente.'
    }
    else {
        . $toolsPath

        $config = Read-EnvConfig -Path $envPath
        $dbHost = if ($config.ContainsKey('CRM_DB_HOST') -and $config['CRM_DB_HOST']) { $config['CRM_DB_HOST'] } else { 'localhost' }
        $dbPort = if ($config.ContainsKey('CRM_DB_PORT') -and $config['CRM_DB_PORT']) { [int]$config['CRM_DB_PORT'] } else { 5432 }
        $dbName = $config['POSTGRES_DB']
        $dbUser = $config['POSTGRES_USER']
        $dbPassword = $config['POSTGRES_PASSWORD']

        if (-not $dbName -or -not $dbUser -or -not $dbPassword) {
            Write-Warning 'POSTGRES_DB, POSTGRES_USER o POSTGRES_PASSWORD no estan definidos. Se omite la activacion automatica.'
        }
        else {
            $psql = Find-PostgresExecutable -Name 'psql.exe'
            $env:PGPASSWORD = $dbPassword
            try {
                $activateNames = @('01 SERCOP Master Poller')
                $deactivateNames = @('01 SERCOP OCDS Poller', '02 SERCOP NCO Poller', '07 SERCOP PC Public Poller')
                $activateSql = ($activateNames | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ', '
                $deactivateSql = ($deactivateNames | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ', '
                Write-Host "Activando workflows requeridos: $($activateNames -join ', ')"
                $sql = @"
UPDATE workflow_entity
SET active = TRUE,
    "activeVersionId" = "versionId",
    "updatedAt" = NOW()
WHERE name IN ($activateSql);

UPDATE workflow_entity
SET active = FALSE,
    "updatedAt" = NOW()
WHERE name IN ($deactivateSql);
"@
                $tempSqlFile = Join-Path $env:TEMP ("activate-workflows-{0}.sql" -f ([Guid]::NewGuid().ToString('N')))
                Set-Content -Path $tempSqlFile -Value $sql -Encoding UTF8
                try {
                    & $psql -h $dbHost -p $dbPort -U $dbUser -d $dbName -v ON_ERROR_STOP=1 -f $tempSqlFile
                    if ($LASTEXITCODE -ne 0) {
                        throw "psql devolvio codigo $LASTEXITCODE activando workflows."
                    }
                }
                finally {
                    Remove-Item $tempSqlFile -Force -ErrorAction SilentlyContinue
                }
            }
            finally {
                $env:PGPASSWORD = ''
            }
        }
    }
}

if ($RestartN8n) {
    Write-Host 'Reiniciando n8n para aplicar activacion de workflows...'
    docker compose restart n8n | Out-Host
}

Write-Host "Importacion completada."
