$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root '.env'
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$baseUrl = 'http://127.0.0.1:5050'
$session = $null
$smokeTag = "smoke_{0}" -f (Get-Date -Format 'yyyyMMddHHmmss')

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

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-Api {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Body = $null,
        [int]$TimeoutSec = 30
    )

    $invokeParams = @{
        Method = $Method
        Uri = "$baseUrl$Path"
        ContentType = 'application/json'
        TimeoutSec = $TimeoutSec
    }

    if ($null -ne $script:session) {
        $invokeParams.WebSession = $script:session
    }

    if ($null -ne $Body) {
        $invokeParams.Body = ($Body | ConvertTo-Json -Depth 10 -Compress)
    }

    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            return Invoke-RestMethod @invokeParams
        }
        catch {
            $statusCode = $_.Exception.Response.StatusCode.value__
            if ($statusCode -ne 429 -or $attempt -eq 4) {
                throw
            }

            $retryAfter = 3
            if ($_.Exception.Response.Headers['Retry-After']) {
                $retryAfter = [Math]::Max(1, [int]$_.Exception.Response.Headers['Retry-After'])
            }

            Start-Sleep -Seconds $retryAfter
        }
    }
}

function Invoke-ApiWithSession {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Session,
        [object]$Body = $null
    )

    $invokeParams = @{
        Method = $Method
        Uri = "$baseUrl$Path"
        ContentType = 'application/json'
        TimeoutSec = 30
        WebSession = $Session
    }

    if ($null -ne $Body) {
        $invokeParams.Body = ($Body | ConvertTo-Json -Depth 10 -Compress)
    }

    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            return Invoke-RestMethod @invokeParams
        }
        catch {
            $statusCode = $_.Exception.Response.StatusCode.value__
            if ($statusCode -ne 429 -or $attempt -eq 4) {
                throw
            }

            $retryAfter = 3
            if ($_.Exception.Response.Headers['Retry-After']) {
                $retryAfter = [Math]::Max(1, [int]$_.Exception.Response.Headers['Retry-After'])
            }

            Start-Sleep -Seconds $retryAfter
        }
    }
}

function Invoke-ApiExpectStatusWithSession {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Session,
        [int]$ExpectedStatusCode
    )

    try {
        Invoke-ApiWithSession -Method $Method -Path $Path -Session $Session | Out-Null
        return $false
    }
    catch {
        return $_.Exception.Response.StatusCode.value__ -eq $ExpectedStatusCode
    }
}

function Invoke-ApiExpectUnauthorized {
    param([string]$Path)

    try {
        $invokeParams = @{
            Method = 'Get'
            Uri = "$baseUrl$Path"
            TimeoutSec = 15
        }

        if ($null -ne $script:session) {
            $invokeParams.WebSession = $script:session
        }

        Invoke-RestMethod @invokeParams | Out-Null
        return $false
    }
    catch {
        return $_.Exception.Response.StatusCode.value__ -eq 401
    }
}

function Invoke-Sql {
    param([string]$Query)

    $env:PGPASSWORD = $script:config['POSTGRES_PASSWORD']
    try {
        return & $script:psql -At -h $script:config['CRM_DB_HOST'] -p $script:config['CRM_DB_PORT'] -U $script:config['POSTGRES_USER'] -d $script:config['POSTGRES_DB'] -F '|' -c $Query
    }
    finally {
        $env:PGPASSWORD = ''
    }
}

function Get-SqlScalar {
    param([string]$Query)

    $result = Invoke-Sql -Query $Query
    return ($result | Select-Object -First 1)
}

function Remove-Diacritics {
    param([string]$Value)

    $normalized = $Value.Normalize([Text.NormalizationForm]::FormD)
    $builder = [System.Text.StringBuilder]::new()
    foreach ($character in $normalized.ToCharArray()) {
        if ([Globalization.CharUnicodeInfo]::GetUnicodeCategory($character) -ne [Globalization.UnicodeCategory]::NonSpacingMark) {
            [void]$builder.Append([char]::ToLowerInvariant($character))
        }
    }

    return ($builder.ToString() -replace '\s+', ' ').Trim()
}

function Get-NormalizedTokens {
    param([string]$Value)

    $normalizedValue = Remove-Diacritics -Value $Value
    return @($normalizedValue.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries) |
        Where-Object { $_.Length -ge 4 } |
        Select-Object -Unique)
}

function Wait-ForKeywordRefresh {
    param(
        [long]$KeywordRuleId,
        [string]$ExpectedTriggerType,
        [int]$TimeoutSeconds = 0
    )

    if ($TimeoutSeconds -le 0) {
        $TimeoutSeconds = $script:keywordRefreshTimeoutSeconds
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $status = Invoke-Api -Method Get -Path '/api/keywords/refresh-status'
        if ($null -ne $status -and $status.triggerType -eq $ExpectedTriggerType) {
            if (($status.keywordRuleId -eq $KeywordRuleId) -or ($ExpectedTriggerType -eq 'keyword_delete')) {
                if ($status.status -in @('completed', 'error')) {
                    return $status
                }
            }
        }

        Start-Sleep -Seconds 3
    }

    throw "Timeout esperando el refresh $ExpectedTriggerType para keywordRuleId=$KeywordRuleId."
}

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [string]$TimeoutMessage,
        [int]$TimeoutSeconds = 90
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) {
            return
        }

        Start-Sleep -Seconds 3
    }

    throw $TimeoutMessage
}

if (-not (Test-Path $envFile)) {
    throw 'No existe .env. Ejecuta scripts\bootstrap.ps1 primero.'
}

$config = Read-EnvFile -Path $envFile
Assert-True -Condition (Test-Path $psql) -Message 'psql no esta disponible para la verificacion del smoke test.'
$script:keywordRefreshTimeoutSeconds =
    if ($config.ContainsKey('SMOKE_KEYWORD_REFRESH_TIMEOUT_SECONDS') -and -not [string]::IsNullOrWhiteSpace($config['SMOKE_KEYWORD_REFRESH_TIMEOUT_SECONDS'])) {
        [int]$config['SMOKE_KEYWORD_REFRESH_TIMEOUT_SECONDS']
    }
    else {
        600
    }

Write-Host 'Verificando health del CRM...'
$health = Invoke-RestMethod -Method Get -Uri "$baseUrl/api/health" -TimeoutSec 15
Assert-True -Condition ($health.status -eq 'ok') -Message 'El CRM no respondio con estado ok en /api/health.'

Write-Host 'Autenticando sesion de smoke test...'
$loginBody = @{
    identifier = $config['CRM_ADMIN_LOGIN']
    password = $config['CRM_AUTH_BOOTSTRAP_PASSWORD']
    rememberMe = $false
} | ConvertTo-Json -Depth 10 -Compress
$login = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/auth/login" -ContentType 'application/json' -Body $loginBody -SessionVariable session -TimeoutSec 30
Assert-True -Condition ($login.user.role -eq 'admin') -Message 'La sesion de smoke test no ingreso con rol admin.'

Write-Host 'Validando endpoints base...'
$me = Invoke-Api -Method Get -Path '/api/auth/me'
$meta = Invoke-Api -Method Get -Path '/api/meta'
$dashboard = Invoke-Api -Method Get -Path '/api/dashboard'
$management = Invoke-Api -Method Get -Path '/api/management/report'
$commercialAlerts = Invoke-Api -Method Get -Path '/api/commercial/alerts'
$zones = Invoke-Api -Method Get -Path '/api/zones'
$users = Invoke-Api -Method Get -Path '/api/users?page=1&pageSize=50'
$keywords = Invoke-Api -Method Get -Path '/api/keywords?page=1&pageSize=25'
$keywordRefreshStatus = Invoke-Api -Method Get -Path '/api/keywords/refresh-status'
$sercopCredentialStatus = Invoke-Api -Method Get -Path '/api/sercop/credentials'
$sercopOperationalStatus = Invoke-Api -Method Get -Path '/api/sercop/status'
$workflows = Invoke-Api -Method Get -Path '/api/workflows?page=1&pageSize=10'

Assert-True -Condition ($me.loginName -eq $config['CRM_ADMIN_LOGIN']) -Message 'El endpoint /api/auth/me no devolvio el usuario autenticado esperado.'
Assert-True -Condition ($dashboard.totalOpportunities -ge 0) -Message 'Dashboard no devolvio un total valido.'
Assert-True -Condition ($zones.Count -gt 0) -Message 'No se cargaron zonas.'
Assert-True -Condition (@($users.items).Count -gt 0) -Message 'No se cargaron usuarios.'
Assert-True -Condition (@($keywords.items).Count -gt 0) -Message 'No se cargaron keyword_rules activas.'
Assert-True -Condition ($management.summary.totalVisibleOpportunities -ge 0) -Message 'No se obtuvo reporte de management.'
Assert-True -Condition ($commercialAlerts.showForCurrentUser -eq $true) -Message 'El resumen de alertas comerciales no devolvio la visibilidad esperada para admin.'
Assert-True -Condition ($meta.storageTarget -eq $config['POSTGRES_DB']) -Message 'Meta no refleja la base configurada.'
Assert-True -Condition ($null -ne $sercopCredentialStatus.configured) -Message 'El estado de credenciales SERCOP no devolvio la estructura esperada.'
Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($sercopCredentialStatus.portalSessionStatus)) -Message 'El estado SERCOP no devolvio portalSessionStatus.'
Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($sercopOperationalStatus.portalSessionStatus)) -Message 'El endpoint /api/sercop/status no devolvio portalSessionStatus.'

$masterWorkflowActive = Get-SqlScalar -Query "SELECT COUNT(*) FROM workflow_entity WHERE name = '01 SERCOP Master Poller' AND active = TRUE;"
$legacyWorkflowsActive = Get-SqlScalar -Query "SELECT COUNT(*) FROM workflow_entity WHERE name IN ('01 SERCOP OCDS Poller','02 SERCOP NCO Poller','07 SERCOP PC Public Poller') AND active = TRUE;"
Assert-True -Condition ([int]$masterWorkflowActive -eq 1) -Message 'El workflow maestro no esta activo en n8n.'
Assert-True -Condition ([int]$legacyWorkflowsActive -eq 0) -Message 'Siguen activos workflows legacy de captura.'

if (@($workflows.items).Count -gt 0) {
    $workflowDetail = Invoke-Api -Method Get -Path "/api/workflows/$($workflows.items[0].id)"
    Assert-True -Condition ($workflowDetail.nodeCount -ge 0) -Message 'No se pudo cargar el detalle del workflow.'
}

Write-Host 'Asegurando acceso de importaciones para CRUD de keywords...'
$importUser = $users.items | Where-Object { $_.loginName -eq 'importaciones' } | Select-Object -First 1
$importPayload = @{
    loginName = 'importaciones'
    fullName = 'Importaciones'
    email = 'importaciones@hdm.local'
    role = 'coordinator'
    phone = $null
    active = $true
    zoneId = $null
    password = 'Importaciones#2026!'
    mustChangePassword = $true
}

if ($null -eq $importUser) {
    $importUser = Invoke-Api -Method Post -Path '/api/users' -Body $importPayload
}
else {
    $importUser = Invoke-Api -Method Put -Path "/api/users/$($importUser.id)" -Body $importPayload
}

$importLoginBody = @{
    identifier = 'importaciones'
    password = 'Importaciones#2026!'
    rememberMe = $false
} | ConvertTo-Json -Depth 10 -Compress
$importLogin = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/auth/login" -ContentType 'application/json' -Body $importLoginBody -SessionVariable importSession -TimeoutSec 30
Assert-True -Condition ($importLogin.user.role -eq 'coordinator') -Message 'El usuario importaciones no ingreso con rol coordinator.'
Assert-True -Condition (Invoke-ApiExpectStatusWithSession -Method Get -Path '/api/sercop/credentials' -Session $importSession -ExpectedStatusCode 403) -Message 'El usuario importaciones pudo acceder al estado de credenciales SERCOP.'

Write-Host 'Validando CRUD de keywords con importaciones...'
$keywordCandidate = Get-SqlScalar -Query @"
SELECT o.id || '|' || o.process_code || '|' || token
FROM opportunities o
CROSS JOIN LATERAL regexp_split_to_table(o.search_document_normalized, '\s+') token
WHERE length(token) >= 6
  AND NOT EXISTS (
    SELECT 1
    FROM keyword_rules k
    WHERE k.keyword_normalized = token
  )
ORDER BY o.created_at DESC
LIMIT 1;
"@
Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($keywordCandidate)) -Message 'No se encontro un token candidato para probar keyword refresh.'
$keywordParts = $keywordCandidate.Split('|', 3)
$keywordOpportunityId = [long]$keywordParts[0]
$keywordToken = $keywordParts[2]

$keywordRule = Invoke-ApiWithSession -Method Post -Path '/api/keywords' -Session $importSession -Body @{
    ruleType = 'include'
    scope = 'all'
    keyword = $keywordToken
    family = 'smoke'
    weight = 1.5
    notes = $smokeTag
    active = $true
}
Assert-True -Condition ($keywordRule.keyword -eq $keywordToken) -Message 'No se pudo crear la keyword de smoke test.'

$refreshCreate = Wait-ForKeywordRefresh -KeywordRuleId $keywordRule.id -ExpectedTriggerType 'keyword_create'
Assert-True -Condition ($refreshCreate.status -eq 'completed') -Message 'El refresh posterior a keyword_create no completo correctamente.'

Wait-Until -TimeoutMessage 'La keyword creada no aparecio en keywords_hit de la oportunidad reevaluada.' -Condition {
    $keywordsHit = Get-SqlScalar -Query "SELECT COALESCE(array_to_string(keywords_hit, ','), '') FROM opportunities WHERE id = $keywordOpportunityId;"
    return $keywordsHit -match "(^|,)$keywordToken(,|$)"
}

$updatedKeywordRule = Invoke-ApiWithSession -Method Put -Path "/api/keywords/$($keywordRule.id)" -Session $importSession -Body @{
    ruleType = 'include'
    scope = 'all'
    keyword = $keywordToken
    family = 'smoke'
    weight = 1.8
    notes = "${smokeTag}_updated"
    active = $true
}
Assert-True -Condition ([decimal]$updatedKeywordRule.weight -eq [decimal]1.8) -Message 'La keyword no se actualizo.'

$refreshUpdate = Wait-ForKeywordRefresh -KeywordRuleId $keywordRule.id -ExpectedTriggerType 'keyword_update'
Assert-True -Condition ($refreshUpdate.status -eq 'completed') -Message 'El refresh posterior a keyword_update no completo correctamente.'

$keywordWeight = Get-SqlScalar -Query "SELECT weight::text FROM keyword_rules WHERE id = $($keywordRule.id);"
Assert-True -Condition ([decimal]$keywordWeight -eq [decimal]1.8) -Message 'La actualizacion de la keyword no se reflejo en PostgreSQL.'

Write-Host 'Validando carga, detalle y busqueda de oportunidades...'
$opportunities = Invoke-Api -Method Get -Path '/api/opportunities?page=1&pageSize=10&chemistryOnly=false'
Assert-True -Condition (@($opportunities.items).Count -gt 0) -Message 'No se cargaron oportunidades.'

$opportunity = $opportunities.items[0]
$detail = Invoke-Api -Method Get -Path "/api/opportunities/$($opportunity.id)"
$activities = Invoke-Api -Method Get -Path "/api/opportunities/$($opportunity.id)/activities?page=1&pageSize=20"
$visibility = Invoke-Api -Method Get -Path "/api/opportunities/visibility?code=$([Uri]::EscapeDataString($detail.processCode))&todayOnly=false"

Assert-True -Condition ($detail.id -eq $opportunity.id) -Message 'El detalle de oportunidad no coincide con el listado.'
Assert-True -Condition ($visibility.existsInDatabase) -Message 'La visibilidad no encontro el proceso esperado.'
Assert-True -Condition (@($activities.items).Count -ge 0) -Message 'No se pudieron cargar actividades.'

$searchByCode = Invoke-Api -Method Get -Path "/api/opportunities?search=$([Uri]::EscapeDataString($detail.processCode))&page=1&pageSize=100&chemistryOnly=false"
Assert-True -Condition (@($searchByCode.items | Where-Object { $_.id -eq $detail.id }).Count -ge 1) -Message 'La busqueda por codigo no devolvio el proceso esperado.'

$multiTokenSearchSource = @($detail.titulo, $detail.entidad) -join ' '
$multiTokens = @(Get-NormalizedTokens -Value $multiTokenSearchSource | Select-Object -First 2)
Assert-True -Condition ($multiTokens.Count -ge 1) -Message 'No se pudieron derivar tokens para probar la busqueda multi-token.'
$multiSearch = Invoke-Api -Method Get -Path "/api/opportunities?search=$([Uri]::EscapeDataString(($multiTokens -join ' ')))&page=1&pageSize=100&chemistryOnly=false"
Assert-True -Condition (@($multiSearch.items | Where-Object { $_.id -eq $detail.id }).Count -ge 1) -Message 'La busqueda multi-token no devolvio el proceso esperado.'

$accentCandidates = Invoke-Api -Method Get -Path '/api/opportunities?page=1&pageSize=100&chemistryOnly=false'
$accentOpportunity = $accentCandidates.items |
    Where-Object { ($_.titulo -match '[^\x00-\x7F]') -or ($_.tipo -match '[^\x00-\x7F]') } |
    Select-Object -First 1
if ($accentOpportunity) {
    $accentSearchBase = if ($accentOpportunity.titulo -match '[^\x00-\x7F]') { $accentOpportunity.titulo } else { $accentOpportunity.tipo }
    $accentTokens = @(Get-NormalizedTokens -Value $accentSearchBase | Where-Object { $_.Length -ge 5 } | Select-Object -First 3)
    if ($accentTokens.Count -gt 0) {
        $accentSearch = Invoke-Api -Method Get -Path "/api/opportunities?search=$([Uri]::EscapeDataString(($accentTokens -join ' ')))&page=1&pageSize=100&chemistryOnly=false"
        Assert-True -Condition (@($accentSearch.items | Where-Object { $_.processCode -eq $accentOpportunity.processCode }).Count -ge 1) -Message 'La busqueda sin tildes no devolvio el proceso con acentos.'
    }
}

Write-Host 'Validando CRUD de vistas guardadas...'
$savedViewName = "${smokeTag}_view"
$savedViewPayload = @{
    viewType = 'commercial'
    name = $savedViewName
    shared = $false
    filtersJson = (@{
        search = $detail.processCode
        processCategory = 'all'
        invitedOnly = $false
        todayOnly = $false
        grouping = 'age'
    } | ConvertTo-Json -Compress)
}
$savedView = Invoke-Api -Method Post -Path '/api/commercial/views' -Body $savedViewPayload
Assert-True -Condition ($savedView.name -eq $savedViewName) -Message 'No se creo la vista guardada.'

$savedViews = Invoke-Api -Method Get -Path '/api/commercial/views?viewType=commercial&page=1&pageSize=30'
Assert-True -Condition (@($savedViews.items | Where-Object { $_.id -eq $savedView.id }).Count -eq 1) -Message 'La vista guardada no se recargo desde la API.'

$savedViewDbCount = Get-SqlScalar -Query "SELECT COUNT(*) FROM crm_saved_views WHERE id = $($savedView.id) AND name = '$savedViewName';"
Assert-True -Condition ([int]$savedViewDbCount -eq 1) -Message 'La vista guardada no quedo persistida en PostgreSQL.'

$updatedSavedViewName = "${savedViewName}_updated"
$updatedSavedView = Invoke-Api -Method Put -Path "/api/commercial/views/$($savedView.id)" -Body (@{
    viewType = 'commercial'
    name = $updatedSavedViewName
    shared = $true
    filtersJson = (@{
        search = $detail.processCode
        processCategory = 'other_public'
        invitedOnly = $true
        todayOnly = $false
        grouping = 'status'
    } | ConvertTo-Json -Compress)
})
Assert-True -Condition ($updatedSavedView.name -eq $updatedSavedViewName) -Message 'La vista guardada no se actualizo.'

$savedViewShared = Get-SqlScalar -Query "SELECT CASE WHEN shared THEN 't' ELSE 'f' END FROM crm_saved_views WHERE id = $($savedView.id);"
Assert-True -Condition ($savedViewShared -eq 't') -Message 'La actualizacion de la vista guardada no se persistio.'

Invoke-Api -Method Delete -Path "/api/commercial/views/$($savedView.id)" | Out-Null
$savedViewDeleted = Get-SqlScalar -Query "SELECT COUNT(*) FROM crm_saved_views WHERE id = $($savedView.id);"
Assert-True -Condition ([int]$savedViewDeleted -eq 0) -Message 'La vista guardada no se elimino de PostgreSQL.'

Write-Host 'Validando guardado y recarga de asignacion, notas, recordatorios e invitacion...'
$originalDetail = $detail
$assignmentNotes = "${smokeTag}_assignment"
$assignmentPayload = @{
    assignedUserId = $originalDetail.assignedUserId
    zoneId = $originalDetail.zoneId
    estado = $originalDetail.estado
    priority = $originalDetail.priority
    notes = $assignmentNotes
}
$updatedAssignment = Invoke-Api -Method Put -Path "/api/opportunities/$($originalDetail.id)/assignment" -Body $assignmentPayload
Assert-True -Condition ($updatedAssignment.crmNotes -eq $assignmentNotes) -Message 'La asignacion no actualizo las notas.'
$assignmentDbNotes = Get-SqlScalar -Query "SELECT COALESCE(crm_notes, '') FROM opportunities WHERE id = $($originalDetail.id);"
Assert-True -Condition ($assignmentDbNotes -eq $assignmentNotes) -Message 'La nota de asignacion no se guardo en PostgreSQL.'

Invoke-Api -Method Put -Path "/api/opportunities/$($originalDetail.id)/assignment" -Body @{
    assignedUserId = $originalDetail.assignedUserId
    zoneId = $originalDetail.zoneId
    estado = $originalDetail.estado
    priority = $originalDetail.priority
    notes = $originalDetail.crmNotes
} | Out-Null

$activityBody = "${smokeTag}_note"
$createdActivity = Invoke-Api -Method Post -Path "/api/opportunities/$($originalDetail.id)/activities" -Body @{
    activityType = 'note'
    body = $activityBody
    metadataJson = '{"source":"smoke-test"}'
}
Assert-True -Condition ($createdActivity.body -eq $activityBody) -Message 'No se pudo crear la actividad de nota.'

$activitiesReloaded = Invoke-Api -Method Get -Path "/api/opportunities/$($originalDetail.id)/activities?page=1&pageSize=30"
Assert-True -Condition (@($activitiesReloaded.items | Where-Object { $_.id -eq $createdActivity.id }).Count -eq 1) -Message 'La actividad creada no recargo desde la API.'

$activityDbCount = Get-SqlScalar -Query "SELECT COUNT(*) FROM crm_opportunity_activities WHERE id = $($createdActivity.id) AND body = '$activityBody';"
Assert-True -Condition ([int]$activityDbCount -eq 1) -Message 'La actividad creada no quedo persistida en PostgreSQL.'
Invoke-Sql -Query "DELETE FROM crm_opportunity_activities WHERE id = $($createdActivity.id);" | Out-Null

$remindAt = [DateTimeOffset]::UtcNow.AddHours(2).ToString('o')
$reminderNotes = "${smokeTag}_reminder"
$createdReminder = Invoke-Api -Method Put -Path "/api/opportunities/$($originalDetail.id)/reminder" -Body @{
    remindAt = $remindAt
    notes = $reminderNotes
}
Assert-True -Condition ($createdReminder.notes -eq $reminderNotes) -Message 'No se pudo guardar el recordatorio.'
$reminderDbCount = Get-SqlScalar -Query "SELECT COUNT(*) FROM crm_reminders WHERE opportunity_id = $($originalDetail.id) AND completed_at IS NULL AND notes = '$reminderNotes';"
Assert-True -Condition ([int]$reminderDbCount -eq 1) -Message 'El recordatorio no quedo persistido en PostgreSQL.'

$clearedReminder = Invoke-Api -Method Put -Path "/api/opportunities/$($originalDetail.id)/reminder" -Body @{
    remindAt = $null
    notes = $null
}
Assert-True -Condition ($null -eq $clearedReminder -or ($clearedReminder -is [string] -and [string]::IsNullOrWhiteSpace($clearedReminder))) -Message 'La limpieza del recordatorio no devolvio un cuerpo vacio.'
$detailAfterReminderClear = Invoke-Api -Method Get -Path "/api/opportunities/$($originalDetail.id)"
Assert-True -Condition ($null -eq $detailAfterReminderClear.reminder) -Message 'La API sigue devolviendo un recordatorio activo despues de limpiarlo.'
$openReminderCount = Get-SqlScalar -Query "SELECT COUNT(*) FROM crm_reminders WHERE opportunity_id = $($originalDetail.id) AND completed_at IS NULL;"
Assert-True -Condition ([int]$openReminderCount -eq 0) -Message 'El recordatorio activo no quedo cerrado en PostgreSQL.'

$invitationSource = "${smokeTag}_invitation"
$invitationUrl = "https://example.com/$smokeTag"
$invitationNotes = "${smokeTag}_notes"
$targetInvitationState = if ($originalDetail.isInvitedMatch) { $true } else { $true }

$updatedInvitation = Invoke-Api -Method Put -Path "/api/opportunities/$($originalDetail.id)/invitation" -Body @{
    isInvitedMatch = $targetInvitationState
    invitationSource = $invitationSource
    invitationEvidenceUrl = $invitationUrl
    invitationNotes = $invitationNotes
}
Assert-True -Condition ($updatedInvitation.invitationSource -eq $invitationSource) -Message 'La invitacion no guardo la fuente esperada.'
$invitationDbRow = Get-SqlScalar -Query "SELECT CASE WHEN is_invited_match THEN 't' ELSE 'f' END || '|' || COALESCE(invitation_source, '') || '|' || COALESCE(invitation_evidence_url, '') FROM opportunities WHERE id = $($originalDetail.id);"
$invitationDbParts = $invitationDbRow.Split('|', 3)
Assert-True -Condition ($invitationDbParts[0] -eq 't' -and $invitationDbParts[1] -eq $invitationSource -and $invitationDbParts[2] -eq $invitationUrl) -Message 'La invitacion no se persistio correctamente en PostgreSQL.'

Invoke-Api -Method Put -Path "/api/opportunities/$($originalDetail.id)/invitation" -Body @{
    isInvitedMatch = $originalDetail.isInvitedMatch
    invitationSource = $originalDetail.invitationSource
    invitationEvidenceUrl = $originalDetail.invitationEvidenceUrl
    invitationNotes = $originalDetail.invitationNotes
} | Out-Null

Write-Host 'Eliminando keyword temporal con importaciones...'
Invoke-ApiWithSession -Method Delete -Path "/api/keywords/$($keywordRule.id)" -Session $importSession | Out-Null
Wait-Until -TimeoutMessage 'La keyword eliminada sigue presente en PostgreSQL.' -Condition {
    [int](Get-SqlScalar -Query "SELECT COUNT(*) FROM keyword_rules WHERE id = $($keywordRule.id);") -eq 0
}
Wait-Until -TimeoutMessage 'La keyword eliminada sigue apareciendo en keywords_hit despues del delete.' -Condition {
    $keywordsHit = Get-SqlScalar -Query "SELECT COALESCE(array_to_string(keywords_hit, ','), '') FROM opportunities WHERE id = $keywordOpportunityId;"
    return $keywordsHit -notmatch "(^|,)$keywordToken(,|$)"
}

Write-Host 'Validando sync de invitaciones y logout...'
$syncResult = Invoke-Api -Method Post -Path '/api/invitations/sync' -Body @{} -TimeoutSec 180
Assert-True -Condition ($syncResult.scannedCount -ge 0) -Message 'La sincronizacion de invitaciones no devolvio conteos validos.'

$logout = Invoke-Api -Method Post -Path '/api/auth/logout' -Body @{}
Assert-True -Condition ($logout.message -eq 'Sesion cerrada.') -Message 'Logout no devolvio el mensaje esperado.'
Assert-True -Condition (Invoke-ApiExpectUnauthorized -Path '/api/auth/me') -Message 'La sesion seguia activa despues del logout.'

Write-Host "Smoke test completado correctamente. Marca: $smokeTag"
