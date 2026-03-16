param(
    [switch]$Live
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$env:DOCKER_CONFIG = Join-Path $root '.docker'

New-Item -ItemType Directory -Force -Path $env:DOCKER_CONFIG | Out-Null

if (-not (Test-Path $envFile)) {
    throw 'No existe .env. Ejecuta scripts\bootstrap.ps1 primero.'
}

$config = @{}
Get-Content $envFile | ForEach-Object {
    if ($_ -match '^[A-Za-z0-9_]+=' -and -not $_.StartsWith('#')) {
        $parts = $_.Split('=', 2)
        $config[$parts[0]] = $parts[1]
    }
}

Write-Host 'Validando docker compose config...'
$composeConfig = cmd /c "docker compose config 2>nul" | Out-String
if ($LASTEXITCODE -eq 0) {
    Write-Host 'docker compose config: OK'
} else {
    Write-Warning 'No se pudo validar docker compose desde esta sesion. Revisa permisos de Docker Desktop.'
}

if (-not (Test-Path (Join-Path $root 'frontend\dist\frontend\browser\index.html'))) {
    Write-Warning 'El frontend compilado no existe todavia. Ejecuta scripts\build-crm.ps1.'
} else {
    Write-Host 'Frontend build: OK'
}

if ($Live) {
    Write-Host 'Contenedores activos:'
    $composePs = cmd /c "docker compose ps 2>nul" | Out-String
    if ($LASTEXITCODE -eq 0) {
        $composePs.TrimEnd()
    } else {
        Write-Warning 'No se pudo listar contenedores desde esta sesion. Revisa permisos de Docker Desktop.'
    }

    Write-Host 'Probing n8n...'
    try {
        $n8n = Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost:5678' -TimeoutSec 10
        Write-Host "n8n HTTP status: $($n8n.StatusCode)"
    } catch {
        Write-Warning $_.Exception.Message
    }

    Write-Host 'Probing Ollama...'
    try {
        $ollama = Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost:11434/api/tags' -TimeoutSec 10
        Write-Host "Ollama HTTP status: $($ollama.StatusCode)"
    } catch {
        Write-Warning $_.Exception.Message
    }

    Write-Host 'Probing Mailpit...'
    try {
        $mailpit = Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost:8025/' -TimeoutSec 10
        Write-Host "Mailpit HTTP status: $($mailpit.StatusCode)"
    } catch {
        Write-Warning $_.Exception.Message
    }

    Write-Host 'Probing Qdrant...'
    try {
        $qdrant = Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost:6333/readyz' -TimeoutSec 10
        Write-Host "Qdrant HTTP status: $($qdrant.StatusCode)"
    } catch {
        Write-Warning $_.Exception.Message
    }

    if (Test-Path $psql) {
        $dbHost = if ($config.ContainsKey('CRM_DB_HOST') -and $config['CRM_DB_HOST']) { $config['CRM_DB_HOST'] } else { 'localhost' }
        $dbPort = if ($config.ContainsKey('CRM_DB_PORT') -and $config['CRM_DB_PORT']) { $config['CRM_DB_PORT'] } else { '5432' }

        Write-Host 'Probing PostgreSQL local...'
        $env:PGPASSWORD = $config['POSTGRES_PASSWORD']
        try {
            & $psql -h $dbHost -p $dbPort -U $config['POSTGRES_USER'] -d $config['POSTGRES_DB'] -c 'SELECT COUNT(*) AS workflows FROM workflow_entity; SELECT COUNT(*) AS zones FROM crm_zones; SELECT COUNT(*) AS users FROM crm_users;'
        } finally {
            $env:PGPASSWORD = ''
        }
    } else {
        Write-Warning 'psql no esta disponible para validar PostgreSQL local.'
    }

    Write-Host 'Probing CRM...'
    try {
        $crm = Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost:5050/api/health' -TimeoutSec 10
        Write-Host "CRM HTTP status: $($crm.StatusCode)"
    } catch {
        Write-Warning 'El CRM no esta iniciado. Usa scripts\start-crm.ps1.'
    }
}
