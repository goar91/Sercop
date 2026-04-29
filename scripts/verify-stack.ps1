param(
    [switch]$Live
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
$externalUrlFile = Join-Path $root 'run\crm-external-url.txt'
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$env:DOCKER_CONFIG = Join-Path $root '.docker'

function Read-EnvFile {
    param([string]$Path)

    $map = @{}
    if (-not (Test-Path $Path)) {
        return $map
    }

    Get-Content $Path | ForEach-Object {
        if ($_ -match '^[A-Za-z0-9_]+=' -and -not $_.StartsWith('#')) {
            $parts = $_.Split('=', 2)
            $map[$parts[0]] = $parts[1]
        }
    }

    return $map
}

New-Item -ItemType Directory -Force -Path $env:DOCKER_CONFIG | Out-Null

if (-not (Test-Path $envFile)) {
    throw 'No existe .env. Ejecuta scripts\bootstrap.ps1 primero.'
}

$config = Read-EnvFile -Path $envFile
$n8nPort = if ($config.ContainsKey('N8N_PORT') -and $config['N8N_PORT']) { [int]$config['N8N_PORT'] } else { 5678 }

Write-Host 'Validando docker compose config...'
$composeConfig = cmd /c "docker compose config 2>nul" | Out-String
if ($LASTEXITCODE -eq 0) {
    Write-Host 'docker compose config: OK'
}
else {
    Write-Warning 'No se pudo validar docker compose desde esta sesion. Revisa permisos de Docker Desktop.'
}

if (-not (Test-Path (Join-Path $root 'frontend\dist\frontend\browser\index.html'))) {
    Write-Warning 'El frontend compilado no existe todavia. Ejecuta scripts\build-crm.ps1.'
}
else {
    Write-Host 'Frontend build: OK'
}

if (-not (Test-Path (Join-Path $root 'backend\bin\Debug\net10.0\backend.dll'))) {
    Write-Warning 'El backend compilado no existe todavia. Ejecuta scripts\build-crm.ps1.'
}
else {
    Write-Host 'Backend build: OK'
}

if (-not $Live) {
    return
}

Write-Host 'Contenedores activos:'
$composePs = cmd /c "docker compose ps 2>nul" | Out-String
if ($LASTEXITCODE -eq 0) {
    $composePs.TrimEnd()
}
else {
    Write-Warning 'No se pudo listar contenedores desde esta sesion. Revisa permisos de Docker Desktop.'
}

Write-Host 'Probing n8n...'
try {
    $n8n = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$n8nPort" -TimeoutSec 10
    Write-Host "n8n HTTP status: $($n8n.StatusCode)"
}
catch {
    Write-Warning $_.Exception.Message
}

Write-Host 'Probing Mailpit...'
try {
    $mailpit = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:8025/' -TimeoutSec 10
    Write-Host "Mailpit HTTP status: $($mailpit.StatusCode)"
}
catch {
    Write-Warning $_.Exception.Message
}

Write-Host 'Probing CRM...'
try {
    $crm = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5050/api/health' -TimeoutSec 10
    Write-Host "CRM HTTP status: $($crm.StatusCode)"
}
catch {
    Write-Warning 'El CRM no esta iniciado. Usa scripts\start-system.ps1 o scripts\start-crm.ps1.'
}

if (Test-Path $externalUrlFile) {
    $externalUrl = (Get-Content $externalUrlFile | Select-Object -First 1).Trim()
    if ($externalUrl) {
        Write-Host "Probing CRM externo: $externalUrl"
        try {
            $externalResponse = Invoke-WebRequest -UseBasicParsing -Uri $externalUrl -TimeoutSec 15 -MaximumRedirection 5
            Write-Host "CRM externo HTTP status: $($externalResponse.StatusCode)"
        }
        catch {
            $statusCode = $_.Exception.Response.StatusCode.value__
            if ($statusCode) {
                Write-Host "CRM externo HTTP status: $statusCode"
            }
            else {
                Write-Warning $_.Exception.Message
            }
        }
    }
}
else {
    Write-Host 'CRM externo: sin URL publicada en run\crm-external-url.txt'
}

if (Test-Path $psql) {
    $dbHost = if ($config.ContainsKey('CRM_DB_HOST') -and $config['CRM_DB_HOST']) { $config['CRM_DB_HOST'] } else { 'localhost' }
    $dbPort = if ($config.ContainsKey('CRM_DB_PORT') -and $config['CRM_DB_PORT']) { $config['CRM_DB_PORT'] } else { '5432' }

    Write-Host 'Probing PostgreSQL local...'
    $env:PGPASSWORD = $config['POSTGRES_PASSWORD']
    try {
        & $psql -h $dbHost -p $dbPort -U $config['POSTGRES_USER'] -d $config['POSTGRES_DB'] -c 'SELECT COUNT(*) AS opportunities FROM opportunities; SELECT SUM(CASE WHEN COALESCE(process_code, ocid_or_nic) ILIKE ''NIC-%'' THEN 1 ELSE 0 END) AS nic, SUM(CASE WHEN COALESCE(process_code, ocid_or_nic) ILIKE ''NC-%'' THEN 1 ELSE 0 END) AS nc, SUM(CASE WHEN COALESCE(process_code, ocid_or_nic) ILIKE ''SIE-%'' THEN 1 ELSE 0 END) AS sie, SUM(CASE WHEN COALESCE(process_code, ocid_or_nic) ILIKE ''RE-%'' THEN 1 ELSE 0 END) AS re FROM opportunities; SELECT COUNT(*) AS zones FROM crm_zones; SELECT COUNT(*) AS users FROM crm_users; SELECT COUNT(*) AS keyword_rules FROM keyword_rules; SELECT id, name, active FROM workflow_entity ORDER BY id;'
    }
    finally {
        $env:PGPASSWORD = ''
    }
}
else {
    Write-Warning 'psql no esta disponible para validar PostgreSQL local.'
}
