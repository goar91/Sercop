param(
    [switch]$Live,
    [switch]$ImportWorkflows,
    [switch]$PortalAudit,
    [switch]$FailOnMissing,
    [switch]$RunTests,
    [switch]$RunSmokeTest
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
$toolsPath = Join-Path $PSScriptRoot 'PostgresTools.ps1'

if (-not (Test-Path $envFile)) {
    throw 'No existe .env. Ejecuta scripts\bootstrap.ps1 primero.'
}

if (-not (Test-Path $toolsPath)) {
    throw 'No existe scripts\PostgresTools.ps1.'
}

. $toolsPath

$config = Read-EnvConfig -Path $envFile
$dbHost = if ($config.ContainsKey('CRM_DB_HOST') -and $config['CRM_DB_HOST']) { $config['CRM_DB_HOST'] } else { 'localhost' }
$dbPort = if ($config.ContainsKey('CRM_DB_PORT') -and $config['CRM_DB_PORT']) { [int]$config['CRM_DB_PORT'] } else { 5432 }
$dbName = $config['POSTGRES_DB']
$dbUser = $config['POSTGRES_USER']
$dbPassword = $config['POSTGRES_PASSWORD']

if (-not $dbName -or -not $dbUser -or -not $dbPassword) {
    throw 'POSTGRES_DB, POSTGRES_USER o POSTGRES_PASSWORD no estan definidos en .env.'
}

$pollMinutesRaw = if ($config.ContainsKey('POLL_MINUTES')) { $config['POLL_MINUTES'] } else { '' }
$pollMinutes = 30
if ($pollMinutesRaw) {
    try { $pollMinutes = [int]$pollMinutesRaw } catch { $pollMinutes = 30 }
}
$pollMinutes = [Math]::Max(5, [Math]::Min(240, $pollMinutes))

$retentionDaysRaw = if ($config.ContainsKey('CRM_RETENTION_DAYS')) { $config['CRM_RETENTION_DAYS'] } else { '' }
$retentionDays = 5
if ($retentionDaysRaw) {
    try { $retentionDays = [int]$retentionDaysRaw } catch { $retentionDays = 5 }
}
$retentionDays = [Math]::Max(1, [Math]::Min(14, $retentionDays))

$visibilitySloMinutesRaw = if ($config.ContainsKey('CRM_VISIBILITY_SLO_MINUTES')) { $config['CRM_VISIBILITY_SLO_MINUTES'] } else { '' }
$visibilitySloMinutes = 35
if ($visibilitySloMinutesRaw) {
    try { $visibilitySloMinutes = [int]$visibilitySloMinutesRaw } catch { $visibilitySloMinutes = 35 }
}
$visibilitySloMinutes = [Math]::Max(5, [Math]::Min(240, $visibilitySloMinutes))

$visibilityGraceMinutesRaw = if ($config.ContainsKey('CRM_VISIBILITY_AUDIT_GRACE_MINUTES')) { $config['CRM_VISIBILITY_AUDIT_GRACE_MINUTES'] } else { '' }
$visibilityGraceMinutes = ($visibilitySloMinutes + 5)
if ($visibilityGraceMinutesRaw) {
    try { $visibilityGraceMinutes = [int]$visibilityGraceMinutesRaw } catch { $visibilityGraceMinutes = ($visibilitySloMinutes + 5) }
}
$visibilityGraceMinutes = [Math]::Max($visibilitySloMinutes, [Math]::Min(240, $visibilityGraceMinutes))

$psql = Find-PostgresExecutable -Name 'psql.exe'

function Resolve-DotnetExecutable {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles} 'dotnet\dotnet.exe'),
        (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw 'No se encontro dotnet en PATH, C:\Program Files\dotnet\dotnet.exe ni en el perfil del usuario.'
}

function Invoke-Sql {
    param([Parameter(Mandatory)][string]$Query)

    $env:PGPASSWORD = $dbPassword
    $tempSqlFile = Join-Path $env:TEMP ("audit-{0}.sql" -f ([Guid]::NewGuid().ToString('N')))
    try {
        Set-Content -Path $tempSqlFile -Value $Query -Encoding UTF8
        & $psql -h $dbHost -p $dbPort -U $dbUser -d $dbName -v ON_ERROR_STOP=1 -f $tempSqlFile
        if ($LASTEXITCODE -ne 0) {
            throw "psql devolvio codigo $LASTEXITCODE ejecutando la auditoria."
        }
    }
    finally {
        $env:PGPASSWORD = ''
        Remove-Item $tempSqlFile -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-SqlLines {
    param([Parameter(Mandatory)][string]$Query)

    $env:PGPASSWORD = $dbPassword
    $tempSqlFile = Join-Path $env:TEMP ("audit-lines-{0}.sql" -f ([Guid]::NewGuid().ToString('N')))
    try {
        Set-Content -Path $tempSqlFile -Value $Query -Encoding UTF8
        $output = & $psql -h $dbHost -p $dbPort -U $dbUser -d $dbName -v ON_ERROR_STOP=1 -t -A -f $tempSqlFile 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "psql devolvio codigo $LASTEXITCODE ejecutando la auditoria."
        }

        return @($output | ForEach-Object { $_.ToString() })
    }
    finally {
        $env:PGPASSWORD = ''
        Remove-Item $tempSqlFile -Force -ErrorAction SilentlyContinue
    }
}

Write-Host '== Auditoria del stack HDM CRM =='
Write-Host "Fecha local: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

$exitCode = 0

if ($Live) {
    Write-Host ''
    Write-Host '== Infra (verify-stack -Live) =='
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\verify-stack.ps1') -Live
}
else {
    Write-Host ''
    Write-Host '== Infra (verify-stack) =='
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\verify-stack.ps1')
}

if ($ImportWorkflows) {
    Write-Host ''
    Write-Host '== n8n (import-workflows) =='
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\import-workflows.ps1') -RestartN8n
}

Write-Host ''
Write-Host '== Base de datos (sercop_crm) =='
Invoke-Sql -Query "SELECT NOW() AS now_utc, (NOW() AT TIME ZONE 'America/Guayaquil') AS now_ec;"
Invoke-Sql -Query "SELECT COUNT(*) AS opportunities_total FROM opportunities;"
Invoke-Sql -Query "SELECT $retentionDays AS retention_days;"
Invoke-Sql -Query "SELECT COUNT(*) AS opportunities_recent FROM opportunities WHERE fecha_publicacion >= (NOW() - INTERVAL '$retentionDays days');"
Invoke-Sql -Query "SELECT COUNT(*) AS opportunities_older_than_retention FROM opportunities WHERE fecha_publicacion < (NOW() - INTERVAL '$retentionDays days');"
Invoke-Sql -Query "SELECT COUNT(*) AS sie_recent FROM opportunities WHERE COALESCE(process_code, ocid_or_nic) ILIKE 'SIE-%' AND fecha_publicacion >= (NOW() - INTERVAL '$retentionDays days');"
Invoke-Sql -Query "SELECT COUNT(*) AS re_recent FROM opportunities WHERE COALESCE(process_code, ocid_or_nic) ILIKE 'RE-%' AND fecha_publicacion >= (NOW() - INTERVAL '$retentionDays days');"
Invoke-Sql -Query "SELECT COUNT(*) AS nic_recent FROM opportunities WHERE COALESCE(process_code, ocid_or_nic) ILIKE 'NIC-%' AND fecha_publicacion >= (NOW() - INTERVAL '$retentionDays days');"
Invoke-Sql -Query "SELECT process_category, COUNT(*) AS count FROM opportunities WHERE fecha_publicacion >= (NOW() - INTERVAL '$retentionDays days') GROUP BY process_category ORDER BY process_category;"
Invoke-Sql -Query "SELECT COUNT(*) AS keyword_rules_active FROM keyword_rules WHERE active = TRUE;"
Invoke-Sql -Query "SELECT raw_payload->>'source' AS payload_source, COUNT(*) AS count FROM opportunities WHERE fecha_publicacion >= (NOW() - INTERVAL '$retentionDays days') GROUP BY payload_source ORDER BY count DESC;"

Write-Host ''
Write-Host '== n8n workflows (estado en workflow_entity) =='
Invoke-Sql -Query "SELECT id, name, active, `"activeVersionId`" FROM workflow_entity WHERE name IN ('01 SERCOP Master Poller','01 SERCOP OCDS Poller','02 SERCOP NCO Poller','07 SERCOP PC Public Poller') ORDER BY id;"
Write-Host '== n8n workflows legacy activos =='
Invoke-Sql -Query "SELECT COUNT(*) AS legacy_active FROM workflow_entity WHERE active = TRUE AND name IN ('01 SERCOP OCDS Poller','02 SERCOP NCO Poller','07 SERCOP PC Public Poller');"

Write-Host ''
Write-Host '== n8n executions (SLO / stuck runs) =='
Write-Host "POLL_MINUTES=$pollMinutes | alerta si running > $($pollMinutes + 5) min"
$runningExecutions = @(
    Invoke-SqlLines -Query @"
SELECT
  w.name,
  e.id,
  e.status,
  to_char(e."startedAt" AT TIME ZONE 'America/Guayaquil', 'YYYY-MM-DD HH24:MI:SS') AS started_ec,
  ROUND(EXTRACT(EPOCH FROM (NOW() - e."startedAt")) / 60) AS age_min
FROM execution_entity e
JOIN workflow_entity w ON w.id = e."workflowId"
WHERE e.status = 'running'
  AND w.name IN ('01 SERCOP Master Poller','01 SERCOP OCDS Poller','02 SERCOP NCO Poller','07 SERCOP PC Public Poller')
ORDER BY e."startedAt" ASC;
"@ |
        Where-Object { $_ } |
        ForEach-Object { $_.Trim() }
)

if (-not $runningExecutions -or $runningExecutions.Count -eq 0) {
    Write-Host 'Sin ejecuciones running.'
}
else {
    $stuck = @()
    foreach ($line in $runningExecutions) {
        $parts = $line -split '\|'
        if ($parts.Count -lt 5) { continue }
        $ageMin = 0
        [void][int]::TryParse($parts[4], [ref]$ageMin)
        if ($ageMin -gt ($pollMinutes + 5)) {
            $stuck += $line
        }
    }

    Write-Host 'Running:'
    $runningExecutions | ForEach-Object { Write-Host "  - $_" }
    if ($stuck.Count -gt 0) {
        Write-Warning "Ejecuciones running demasiado tiempo: $($stuck.Count)"
        $stuck | ForEach-Object { Write-Host "  - $_" }
    }
}

Write-Host ''
Write-Host '== Credenciales SERCOP (sercop_credentials) =='
Invoke-Sql -Query "SELECT credential_key, masked_ruc, masked_username, validation_status, last_validated_at, validation_error FROM sercop_credentials ORDER BY credential_key;"

if ($PortalAudit) {
    Write-Host ''
    Write-Host '== Auditoria portal SERCOP (publico) vs PostgreSQL (ventana 5 dias) =='

    $now = Get-Date
    Write-Host "SLO visibilidad: <= $visibilitySloMinutes min | gracia auditoria: < $visibilityGraceMinutes min"
    $cutoff = (Get-Date).AddDays(-$retentionDays)
    Write-Host "Cutoff local: $($cutoff.ToString('yyyy-MM-dd HH:mm:ss')) (America/Guayaquil)"

    function Get-HeaderValue {
        param(
            [Parameter(Mandatory)]
            [hashtable]$Headers,
            [Parameter(Mandatory)]
            [string]$Name
        )

        foreach ($key in $Headers.Keys) {
            if ($key -and $key.ToString().ToLowerInvariant() -eq $Name.ToLowerInvariant()) {
                $value = $Headers[$key]
                if ($value -is [array]) {
                    return [string]($value | Select-Object -First 1)
                }

                return [string]$value
            }
        }

        return ''
    }

    function Parse-XJson {
        param([hashtable]$Headers)

        $raw = (Get-HeaderValue -Headers $Headers -Name 'x-json').Trim()
        if (-not $raw) {
            return $null
        }

        if ($raw.StartsWith('(') -and $raw.EndsWith(')')) {
            $raw = $raw.Substring(1, $raw.Length - 2)
        }

        try {
            return $raw | ConvertFrom-Json
        }
        catch {
            return $null
        }
    }

    function Parse-PortalTimestamp {
        param([string]$Value)

        $raw = if ($null -ne $Value) { [string]$Value } else { '' }
        $raw = $raw.Trim()
        if (-not $raw) {
            return $null
        }

        try {
            return [DateTime]::ParseExact($raw, 'yyyy-MM-dd HH:mm:ss', [System.Globalization.CultureInfo]::InvariantCulture)
        }
        catch {
            return $null
        }
    }

    function Print-MissingSample {
        param(
            [string]$Label,
            [string[]]$MissingCodes,
            [int]$TotalPortal = 0,
            [int]$TotalDb = 0,
            [int]$TotalIgnored = 0
        )

        if (-not $MissingCodes -or $MissingCodes.Count -eq 0) {
            Write-Host ("{0}: OK (0 faltantes fuera del SLO) | Portal={1} | DB={2} | Ignorados(<{3}min)={4}" -f $Label, $TotalPortal, $TotalDb, $visibilityGraceMinutes, $TotalIgnored)
            return
        }

        Write-Warning ("{0}: faltan {1} en PostgreSQL (fuera del SLO) | Portal={2} | DB={3} | Ignorados(<{4}min)={5}" -f $Label, $MissingCodes.Count, $TotalPortal, $TotalDb, $visibilityGraceMinutes, $TotalIgnored)
        $sample = $MissingCodes | Select-Object -First 25
        $sample | ForEach-Object { Write-Host "  - $_" }
        if ($MissingCodes.Count -gt $sample.Count) {
            Write-Host "  ... y $($MissingCodes.Count - $sample.Count) mas"
        }
    }

    # NCO public list
    Write-Host ''
    Write-Host '--- NCO (NIC/NC) ---'
    $ncoPortal = Invoke-RestMethod -Uri 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/NCO/NCORetornaRegistros.cpe?lot=1&draw=1&start=0&length=500' -TimeoutSec 60
    $ncoPortalPublishedByCode = @{}
    $ncPortal = Invoke-RestMethod -Method Post -Uri 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/NCO/NCORetornaRegistros.cpe?lot=1' -ContentType 'application/x-www-form-urlencoded; charset=UTF-8' -Body @{
        sEcho = '1'
        iDisplayStart = '0'
        iDisplayLength = '2000'
        sSearch_0 = '54089'
        sSearch_5 = '384'
        iSortCol_0 = '1'
        sSortDir_0 = 'desc'
    } -TimeoutSec 60
    foreach ($row in @($ncoPortal.data) + @($ncPortal.data)) {
        $code = [string]$row.codigo_contratacion
        $published = Parse-PortalTimestamp ([string]$row.fecha_publicacion)
        if (-not $code -or -not $published -or $published -lt $cutoff) {
            continue
        }

        $key = $code.Trim()
        if (-not $ncoPortalPublishedByCode.ContainsKey($key)) {
            $ncoPortalPublishedByCode[$key] = $published
        }
    }

    $ncoPortalRecent = @(
        $ncoPortalPublishedByCode.GetEnumerator() |
            ForEach-Object { [PSCustomObject]@{ Code = $_.Key; PublishedAt = $_.Value } } |
            Sort-Object Code
    )

    $ncoDbCodes = @(
        Invoke-SqlLines -Query "SELECT COALESCE(process_code, ocid_or_nic) FROM opportunities WHERE source='nco' AND fecha_publicacion >= (NOW() - INTERVAL '$retentionDays days');" |
            Where-Object { $_ -match '^(NIC|NC)-' } |
            ForEach-Object { $_.Trim() } |
            Sort-Object -Unique
    )

    $ncoMissingItems = $ncoPortalRecent | Where-Object { $_.Code -notin $ncoDbCodes }
    $ncoMissingIgnored = @($ncoMissingItems | Where-Object { ($now - $_.PublishedAt).TotalMinutes -lt $visibilityGraceMinutes })
    $ncoMissingOutside = @($ncoMissingItems | Where-Object { ($now - $_.PublishedAt).TotalMinutes -ge $visibilityGraceMinutes })
    Write-Host "Portal NCO (reciente): $($ncoPortalRecent.Count) | DB NCO (reciente): $($ncoDbCodes.Count) | faltantes total: $($ncoMissingItems.Count) | ignorados: $($ncoMissingIgnored.Count)"
    Print-MissingSample -Label 'NCO' -MissingCodes ($ncoMissingOutside | ForEach-Object { $_.Code }) -TotalPortal $ncoPortalRecent.Count -TotalDb $ncoDbCodes.Count -TotalIgnored $ncoMissingIgnored.Count
    if ($FailOnMissing -and $ncoMissingOutside.Count -gt 0) {
        $exitCode = 2
    }

    # PC public list
    Write-Host ''
    Write-Host '--- PC (portal interfazWeb.php) ---'
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $todayKey = (Get-Date).ToString('yyyy-MM-dd')
    $startKey = (Get-Date).AddDays(-($retentionDays + 1)).ToString('yyyy-MM-dd')

    $countResponse = Invoke-WebRequest -UseBasicParsing -WebSession $session -Method Post -Uri 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/servicio/interfazWeb.php' -ContentType 'application/x-www-form-urlencoded' -Body @{
        '__class' = 'SolicitudCompra'
        '__action' = 'buscarProcesoxEntidadCount'
        'txtPalabrasClaves' = ''
        'txtEntidadContratante' = ''
        'cmbEntidad' = ''
        'txtCodigoTipoCompra' = ''
        'txtCodigoProceso' = ''
        'f_inicio' = $startKey
        'f_fin' = $todayKey
        'captccc2' = '1'
        'paginaActual' = '0'
    } -TimeoutSec 60

    $countPayload = Parse-XJson -Headers $countResponse.Headers
    $count = if ($countPayload -and $countPayload.count) { [int]$countPayload.count } else { 0 }
    Write-Host "Portal PC count (window $startKey..$todayKey): $count"

    $pageSize = 20
    $offsets = if ($count -gt 0) { 0..([Math]::Ceiling($count / $pageSize) - 1) | ForEach-Object { $_ * $pageSize } } else { @() }
    $pcPortalPublishedByCode = @{}

    foreach ($offset in $offsets) {
        $capt = if ($offset -eq 0) { '1' } else { '2' }
        $pageResponse = Invoke-WebRequest -UseBasicParsing -WebSession $session -Method Post -Uri 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/servicio/interfazWeb.php' -ContentType 'application/x-www-form-urlencoded' -Body @{
            '__class' = 'SolicitudCompra'
            '__action' = 'buscarProcesoxEntidad'
            'txtPalabrasClaves' = ''
            'txtEntidadContratante' = ''
            'cmbEntidad' = ''
            'txtCodigoTipoCompra' = ''
            'txtCodigoProceso' = ''
            'f_inicio' = $startKey
            'f_fin' = $todayKey
            'captccc2' = $capt
            'paginaActual' = "$offset"
            'count' = "$count"
        } -TimeoutSec 60

        $rows = Parse-XJson -Headers $pageResponse.Headers
        if (-not ($rows -is [System.Collections.IEnumerable])) {
            continue
        }

        foreach ($row in $rows) {
            $code = [string]$row.c
            $published = Parse-PortalTimestamp ([string]$row.f)
            if (-not $code -or -not $published -or $published -lt $cutoff) {
                continue
            }

            $key = $code.Trim()
            if (-not $pcPortalPublishedByCode.ContainsKey($key)) {
                $pcPortalPublishedByCode[$key] = $published
            }
        }
    }

    $pcPortalRecent = @(
        $pcPortalPublishedByCode.GetEnumerator() |
            ForEach-Object { [PSCustomObject]@{ Code = $_.Key; PublishedAt = $_.Value } } |
            Sort-Object Code
    )

    $pcDbCodes = @(
        Invoke-SqlLines -Query "SELECT COALESCE(process_code, ocid_or_nic) FROM opportunities WHERE source='ocds' AND fecha_publicacion >= (NOW() - INTERVAL '$retentionDays days');" |
            Where-Object { $_ -match '^[A-Z]{2,5}-' } |
            ForEach-Object { $_.Trim() } |
            Sort-Object -Unique
    )

    $pcMissingItems = $pcPortalRecent | Where-Object { $_.Code -notin $pcDbCodes }
    $pcMissingIgnored = @($pcMissingItems | Where-Object { ($now - $_.PublishedAt).TotalMinutes -lt $visibilityGraceMinutes })
    $pcMissingOutside = @($pcMissingItems | Where-Object { ($now - $_.PublishedAt).TotalMinutes -ge $visibilityGraceMinutes })
    Write-Host "Portal PC (reciente): $($pcPortalRecent.Count) | DB PC (reciente): $($pcDbCodes.Count) | faltantes total: $($pcMissingItems.Count) | ignorados: $($pcMissingIgnored.Count)"
    Print-MissingSample -Label 'PC' -MissingCodes ($pcMissingOutside | ForEach-Object { $_.Code }) -TotalPortal $pcPortalRecent.Count -TotalDb $pcDbCodes.Count -TotalIgnored $pcMissingIgnored.Count
    if ($FailOnMissing -and $pcMissingOutside.Count -gt 0) {
        $exitCode = 2
    }
}

if ($RunTests) {
    Write-Host ''
    Write-Host '== dotnet test (backend.tests) =='
    $dotnet = Resolve-DotnetExecutable
    & $dotnet test (Join-Path $root 'backend.tests\backend.tests.csproj') --no-build
}

if ($RunSmokeTest) {
    Write-Host ''
    Write-Host '== smoke-test =='
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\smoke-test.ps1')
}

Write-Host ''
Write-Host 'Auditoria finalizada.'

if ($exitCode -ne 0) {
    exit $exitCode
}
