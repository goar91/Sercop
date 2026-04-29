param(
    [string]$BaseUrl = 'http://127.0.0.1:5050'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'

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

function Find-Executable {
    param(
        [string]$Name,
        [string[]]$Candidates = @()
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    foreach ($candidate in $Candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    return $null
}

function Test-Http {
    param(
        [string]$Name,
        [string]$Uri
    )

    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 10
        [pscustomobject]@{ Servicio = $Name; Url = $Uri; Estado = $response.StatusCode; Ok = $true }
    }
    catch {
        [pscustomobject]@{ Servicio = $Name; Url = $Uri; Estado = 'sin conexion'; Ok = $false }
    }
}

if (-not (Test-Path $envFile)) {
    throw 'No existe .env. Ejecuta scripts\bootstrap.ps1 primero.'
}

$config = Read-EnvFile -Path $envFile
$n8nPort = if ($config.ContainsKey('N8N_PORT') -and $config['N8N_PORT']) { $config['N8N_PORT'] } else { '5678' }
$crmDbHost = if ($config.ContainsKey('CRM_DB_HOST') -and $config['CRM_DB_HOST']) { $config['CRM_DB_HOST'] } else { 'localhost' }
$crmDbPort = if ($config.ContainsKey('CRM_DB_PORT') -and $config['CRM_DB_PORT']) { $config['CRM_DB_PORT'] } else { '5432' }
$psql = Find-Executable -Name 'psql.exe' -Candidates @(
    'C:\Program Files\PostgreSQL\18\bin\psql.exe',
    'C:\Program Files\PostgreSQL\17\bin\psql.exe',
    'C:\Program Files\PostgreSQL\16\bin\psql.exe'
)

Write-Host '== Servicios HTTP =='
@(
    Test-Http -Name 'CRM' -Uri "$BaseUrl/api/health"
    Test-Http -Name 'n8n' -Uri "http://127.0.0.1:$n8nPort"
    Test-Http -Name 'Mailpit' -Uri 'http://127.0.0.1:8025/'
) | Format-Table -AutoSize

Write-Host ''
Write-Host '== Docker =='
$docker = Find-Executable -Name 'docker.exe'
if ($docker) {
    try {
        $dockerOutput = & $docker ps --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}' 2>&1
        if ($LASTEXITCODE -eq 0) {
            $dockerOutput | Out-Host
        }
        else {
            Write-Warning 'Docker no responde. Verifica que Docker Desktop este iniciado.'
            $dockerOutput | ForEach-Object { Write-Warning $_ }
        }
    }
    catch {
        Write-Warning 'Docker no responde. Verifica que Docker Desktop este iniciado.'
    }
}
else {
    Write-Warning 'docker.exe no esta disponible.'
}

Write-Host ''
Write-Host '== PostgreSQL =='
if (-not $psql) {
    Write-Warning 'psql.exe no esta disponible.'
}
else {
    $sql = @'
SELECT 'opportunities' AS table_name, COUNT(*)::int AS total FROM opportunities
UNION ALL SELECT 'analysis_runs', COUNT(*)::int FROM analysis_runs
UNION ALL SELECT 'feedback_events', COUNT(*)::int FROM feedback_events
UNION ALL SELECT 'documents', COUNT(*)::int FROM documents
UNION ALL SELECT 'crm_activities', COUNT(*)::int FROM crm_opportunity_activities
UNION ALL SELECT 'workflow_entity', COUNT(*)::int FROM workflow_entity
ORDER BY table_name;

SELECT
  COALESCE(process_category, 'sin_categoria') AS process_category,
  COUNT(*)::int AS total
FROM opportunities
GROUP BY COALESCE(process_category, 'sin_categoria')
ORDER BY total DESC, process_category ASC;

SELECT id, name, active, "updatedAt" FROM workflow_entity ORDER BY id;
'@
    $tempSqlFile = Join-Path $env:TEMP ("verify-process-pipeline-{0}.sql" -f ([Guid]::NewGuid().ToString('N')))
    Set-Content -Path $tempSqlFile -Value $sql -Encoding UTF8
    $env:PGPASSWORD = $config['POSTGRES_PASSWORD']
    try {
        & $psql -h $crmDbHost -p $crmDbPort -U $config['POSTGRES_USER'] -d $config['POSTGRES_DB'] -v ON_ERROR_STOP=1 -f $tempSqlFile
    }
    finally {
        $env:PGPASSWORD = ''
        Remove-Item $tempSqlFile -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host '== API CRM autenticada =='
if ($config.ContainsKey('CRM_ADMIN_LOGIN') -and $config.ContainsKey('CRM_AUTH_BOOTSTRAP_PASSWORD')) {
    try {
        $loginBody = @{
            identifier = $config['CRM_ADMIN_LOGIN']
            password = $config['CRM_AUTH_BOOTSTRAP_PASSWORD']
            rememberMe = $false
        } | ConvertTo-Json -Compress

        $login = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/auth/login" -ContentType 'application/json' -Body $loginBody -SessionVariable session -TimeoutSec 30
        $dashboard = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/dashboard" -WebSession $session -TimeoutSec 30
        $opportunities = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/opportunities?page=1&pageSize=10&chemistryOnly=false" -WebSession $session -TimeoutSec 30
        $workflows = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/workflows?page=1&pageSize=10" -WebSession $session -TimeoutSec 30

        [pscustomobject]@{
            UsuarioRol = $login.user.role
            DashboardProcesos = $dashboard.totalOpportunities
            DashboardWorkflows = $dashboard.workflowCount
            ProcesosApiTotal = $opportunities.totalCount
            ProcesosApiPagina = @($opportunities.items).Count
            WorkflowsApiTotal = $workflows.totalCount
            WorkflowsApiActivos = @($workflows.items | Where-Object { $_.active -eq $true }).Count
        } | Format-List
    }
    catch {
        Write-Warning "No se pudo consultar la API autenticada: $($_.Exception.Message)"
    }
}
else {
    Write-Warning 'No hay credenciales admin configuradas en .env para verificar la API autenticada.'
}
