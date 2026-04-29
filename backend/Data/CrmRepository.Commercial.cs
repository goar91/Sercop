using System.Globalization;
using System.Text.Json;
using backend.Auth;
using Npgsql;

namespace backend;

public sealed partial class CrmRepository
{
    internal async Task<PagedResultDto<OpportunityListItemDto>> SearchOpportunitiesAsync(
        string? search,
        string? entity,
        string? processCode,
        string? keyword,
        string? estado,
        long? zoneId,
        long? assignedUserId,
        string? processCategory,
        bool invitedOnly,
        bool todayOnly,
        bool chemistryOnly,
        int page,
        int pageSize,
        AuthenticatedCrmUser? actor,
        CancellationToken cancellationToken)
    {
        page = NormalizePage(page);
        pageSize = NormalizePageSize(pageSize);
        if (CrmRoleRules.IsSeller(actor))
        {
            assignedUserId = actor!.Id;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var keywordRules = await LoadKeywordRuleSnapshotAsync(connection, cancellationToken);
        var rows = await LoadOpportunityRowsAsync(connection, search, entity, processCode, keyword, estado, zoneId, assignedUserId, invitedOnly, cancellationToken);
        var normalizedProcessCategory = OpportunityProcessCategory.NormalizeFilter(processCategory);
        var visibleRows = FilterVisibleRows(rows, keywordRules, actor, normalizedProcessCategory, todayOnly, chemistryOnly);
        var totalCount = visibleRows.Count;
        var items = visibleRows
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(MapOpportunityListItem)
            .ToArray();

        return new PagedResultDto<OpportunityListItemDto>(items, totalCount, page, pageSize);
    }

    internal async Task<OpportunityVisibilityDto> GetOpportunityVisibilityAsync(
        string processCode,
        bool todayOnly,
        AuthenticatedCrmUser? actor,
        CancellationToken cancellationToken)
    {
        var normalizedCode = NormalizeNullableText(processCode);
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return new OpportunityVisibilityDto(string.Empty, false, false, null, null, null, null, false, Array.Empty<string>(), ["Debes indicar un codigo de proceso."]);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var keywordRules = await LoadKeywordRuleSnapshotAsync(connection, cancellationToken);
        var row = await LoadOpportunityRowByCodeAsync(connection, normalizedCode, cancellationToken);
        if (row is null)
        {
            return new OpportunityVisibilityDto(normalizedCode, false, false, null, null, null, null, false, Array.Empty<string>(), ["No existe en PostgreSQL. Ejecuta o revisa los pollers de SERCOP."]);
        }

        if (actor is not null && !CanActorAccessOpportunity(actor, row))
        {
            return new OpportunityVisibilityDto(
                row.ProcessCode,
                true,
                false,
                row.Id,
                ResolveStoredProcessCategory(row.ProcessCategory, row.Source, row.Tipo, row.ProcessCode),
                row.CaptureScope,
                row.IsChemistryCandidate,
                row.IsInvitedMatch,
                ParseClassificationReasons(row.ClassificationPayloadText),
                ["No se muestra porque el proceso no esta asignado a tu usuario."]);
        }

        var evaluation = EvaluateVisibility(row, keywordRules, todayOnly, chemistryOnly: true);
        return new OpportunityVisibilityDto(
            row.ProcessCode,
            true,
            evaluation.IsVisible,
            row.Id,
            ResolveStoredProcessCategory(row.ProcessCategory, row.Source, row.Tipo, row.ProcessCode),
            row.CaptureScope,
            row.IsChemistryCandidate,
            row.IsInvitedMatch,
            ParseClassificationReasons(row.ClassificationPayloadText),
            evaluation.Reasons.Count == 0 ? ["Visible con el filtro actual."] : evaluation.Reasons);
    }

    internal async Task<CommercialAlertSummaryDto> GetCommercialAlertsAsync(
        AuthenticatedCrmUser actor,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var showForCurrentUser = !CrmRoleRules.IsSeller(actor)
            || await SellerHasAssignedOpportunitiesAsync(connection, actor.Id, cancellationToken);
        if (!showForCurrentUser)
        {
            return new CommercialAlertSummaryDto(0, 0, 0, false, []);
        }

        var keywordRules = await LoadKeywordRuleSnapshotAsync(connection, cancellationToken);
        var rows = await LoadOpportunityRowsAsync(
            connection,
            null,
            null,
            null,
            null,
            null,
            null,
            CrmRoleRules.IsSeller(actor) ? actor.Id : null,
            false,
            cancellationToken);
        var visibleRows = FilterVisibleRows(rows, keywordRules, actor, OpportunityProcessCategory.All, false, true);
        var now = EcuadorTime.Now();
        var freshSince = now.AddDays(-2);
        var followUpWatchSince = now.AddDays(-2);
        var recentOverdueWindow = now.AddHours(-12);
        var dedupe = new HashSet<string>(StringComparer.Ordinal);
        var alerts = new List<CommercialAlertItemDto>();

        foreach (var item in visibleRows)
        {
            var publishedAt = item.Row.FechaPublicacion ?? item.Row.CreatedAt;
            var isFresh = publishedAt >= freshSince;
            var hasRecentInvitation = item.Row.IsInvitedMatch
                && item.Row.InvitationVerifiedAt is DateTimeOffset invitationVerifiedAtCandidate
                && invitationVerifiedAtCandidate >= freshSince;
            var isRecentOpportunity = isFresh || hasRecentInvitation;

            if (hasRecentInvitation && item.Row.InvitationVerifiedAt is DateTimeOffset invitationVerifiedAt)
            {
                AddAlert("critical", "Invitacion HDM confirmada recientemente.", invitationVerifiedAt);
            }

            if (!isRecentOpportunity)
            {
                continue;
            }

            if (isFresh)
            {
                if (!item.Row.AssignedUserId.HasValue && !CrmRoleRules.IsSeller(actor))
                {
                    AddAlert("critical", "Proceso nuevo sin vendedor asignado.", publishedAt);
                }
                else
                {
                    AddAlert("warning", "Proceso nuevo pendiente de gestion comercial.", publishedAt);
                }
            }

            if (item.Row.FechaLimite is DateTimeOffset deadline)
            {
                if (deadline >= now && deadline <= now.AddHours(24))
                {
                    AddAlert("critical", "Proceso por vencer en menos de 24 horas.", deadline);
                }
            }

            if (item.Row.Reminder?.RemindAt is DateTimeOffset remindAt)
            {
                if (remindAt < now && remindAt >= recentOverdueWindow)
                {
                    AddAlert("critical", "Recordatorio vencido.", remindAt);
                }
                else if (remindAt <= now.AddHours(12))
                {
                    AddAlert("warning", "Recordatorio programado para las proximas 12 horas.", remindAt);
                }
            }

            if (publishedAt >= followUpWatchSince
                && item.Row.AssignedUserId.HasValue
                && string.Equals(item.Metrics.SlaStatus, "sin_seguimiento", StringComparison.Ordinal))
            {
                AddAlert("warning", "Proceso sin seguimiento reciente.", item.Metrics.LastActivityAt ?? item.Row.FechaLimite);
            }

            void AddAlert(string severity, string message, DateTimeOffset? referenceAt)
            {
                var key = $"{item.Row.Id}|{severity}|{message}";
                if (!dedupe.Add(key))
                {
                    return;
                }

                alerts.Add(new CommercialAlertItemDto(
                    item.Row.Id,
                    item.Row.ProcessCode,
                    item.Row.Titulo,
                    severity,
                    message,
                    referenceAt));
            }
        }

        var orderedAlerts = alerts
            .GroupBy(alert => new { alert.OpportunityId, alert.ProcessCode, alert.Titulo })
            .Select(group =>
            {
                var orderedGroup = group
                    .OrderBy(alert => GetAlertPriority(alert.Message))
                    .ThenByDescending(alert => alert.ReferenceAt ?? DateTimeOffset.MinValue)
                    .ToArray();
                var primary = orderedGroup[0];
                var extraAlerts = orderedGroup.Length - 1;
                var message = extraAlerts > 0
                    ? $"{primary.Message} (+{extraAlerts} alerta{(extraAlerts == 1 ? string.Empty : "s")})"
                    : primary.Message;
                var severity = orderedGroup.Any(alert => string.Equals(alert.Severity, "critical", StringComparison.OrdinalIgnoreCase))
                    ? "critical"
                    : "warning";
                var referenceAt = orderedGroup.Max(alert => alert.ReferenceAt);
                return new CommercialAlertItemDto(
                    primary.OpportunityId,
                    primary.ProcessCode,
                    primary.Titulo,
                    severity,
                    message,
                    referenceAt);
            })
            .OrderBy(alert => GetAlertPriority(alert.Message))
            .ThenByDescending(alert => alert.ReferenceAt ?? DateTimeOffset.MinValue)
            .ThenBy(alert => alert.ProcessCode, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        var criticalAlerts = orderedAlerts.Count(alert => string.Equals(alert.Severity, "critical", StringComparison.OrdinalIgnoreCase));
        var warningAlerts = orderedAlerts.Count(alert => string.Equals(alert.Severity, "warning", StringComparison.OrdinalIgnoreCase));
        return new CommercialAlertSummaryDto(orderedAlerts.Length, criticalAlerts, warningAlerts, true, orderedAlerts);

        static int GetAlertPriority(string message)
        {
            if (message.StartsWith("Invitacion HDM confirmada recientemente.", StringComparison.Ordinal))
            {
                return 0;
            }

            if (message.StartsWith("Proceso por vencer en menos de 24 horas.", StringComparison.Ordinal))
            {
                return 1;
            }

            if (message.StartsWith("Recordatorio vencido.", StringComparison.Ordinal))
            {
                return 2;
            }

            if (message.StartsWith("Proceso nuevo sin vendedor asignado.", StringComparison.Ordinal))
            {
                return 3;
            }

            if (message.StartsWith("Proceso nuevo pendiente de gestion comercial.", StringComparison.Ordinal))
            {
                return 4;
            }

            if (message.StartsWith("Recordatorio programado para las proximas 12 horas.", StringComparison.Ordinal))
            {
                return 5;
            }

            if (message.StartsWith("Proceso sin seguimiento reciente.", StringComparison.Ordinal))
            {
                return 6;
            }

            return 99;
        }
    }

    private static async Task<bool> SellerHasAssignedOpportunitiesAsync(
        NpgsqlConnection connection,
        long sellerId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM opportunities
                WHERE assigned_user_id = @assigned_user_id
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("assigned_user_id", sellerId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }

    public async Task<PagedResultDto<OpportunityActivityDto>> GetOpportunityActivitiesAsync(
        long opportunityId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = NormalizePage(page);
        pageSize = NormalizePageSize(pageSize);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string countSql = "SELECT COUNT(*)::int FROM crm_opportunity_activities WHERE opportunity_id = @opportunity_id;";
        await using var countCommand = new NpgsqlCommand(countSql, connection);
        countCommand.Parameters.AddWithValue("opportunity_id", opportunityId);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT a.id, a.activity_type, a.body, a.metadata_json::text, a.created_by_user_id, u.full_name, a.created_at
            FROM crm_opportunity_activities a
            LEFT JOIN crm_users u ON u.id = a.created_by_user_id
            WHERE a.opportunity_id = @opportunity_id
            ORDER BY a.created_at DESC, a.id DESC
            OFFSET @offset ROWS FETCH NEXT @page_size ROWS ONLY;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunity_id", opportunityId);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        command.Parameters.AddWithValue("page_size", pageSize);

        var items = new List<OpportunityActivityDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new OpportunityActivityDto(
                reader.GetInt64(0),
                reader.GetString(1),
                GetNullableString(reader, 2),
                reader.GetString(3),
                GetNullableInt64(reader, 4),
                GetNullableString(reader, 5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return new PagedResultDto<OpportunityActivityDto>(items, totalCount, page, pageSize);
    }

    public async Task<OpportunityActivityDto?> AddOpportunityActivityAsync(
        long opportunityId,
        long? createdByUserId,
        string? createdByLoginName,
        OpportunityActivityCreateRequest request,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var body = NormalizeNullableText(request.Body);
        var activityType = NormalizeNullableText(request.ActivityType)?.ToLowerInvariant() ?? "note";
        var metadataJson = NormalizeNullableText(request.MetadataJson) ?? "{}";

        const string sql = """
            INSERT INTO crm_opportunity_activities (
              opportunity_id,
              activity_type,
              body,
              metadata_json,
              created_by_user_id
            )
            VALUES (
              @opportunity_id,
              @activity_type,
              @body,
              @metadata_json::jsonb,
              @created_by_user_id
            )
            RETURNING id, activity_type, body, metadata_json::text, created_by_user_id, created_at;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunity_id", opportunityId);
        command.Parameters.AddWithValue("activity_type", activityType);
        AddNullableText(command.Parameters, "body", body);
        command.Parameters.AddWithValue("metadata_json", metadataJson);
        AddNullableInt64(command.Parameters, "created_by_user_id", createdByUserId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var activity = new OpportunityActivityDto(
            reader.GetInt64(0),
            reader.GetString(1),
            GetNullableString(reader, 2),
            reader.GetString(3),
            GetNullableInt64(reader, 4),
            null,
            reader.GetFieldValue<DateTimeOffset>(5));

        await reader.CloseAsync();
        var authorName = createdByUserId.HasValue ? await ResolveUserNameAsync(connection, createdByUserId.Value, cancellationToken) : null;
        await WriteAuditLogAsync(
            connection,
            createdByUserId,
            createdByLoginName,
            "activity_add",
            "opportunity",
            opportunityId.ToString(CultureInfo.InvariantCulture),
            ipAddress,
            userAgent,
            new { activityType, body },
            cancellationToken);

        return activity with { CreatedByUserName = authorName };
    }

    public async Task<OpportunityReminderDto?> UpsertReminderAsync(
        long opportunityId,
        long? createdByUserId,
        string? createdByLoginName,
        OpportunityReminderUpsertRequest request,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string closePreviousSql = """
            UPDATE crm_reminders
            SET completed_at = NOW(),
                updated_at = NOW()
            WHERE opportunity_id = @opportunity_id
              AND completed_at IS NULL;
            """;

        await using (var closeCommand = new NpgsqlCommand(closePreviousSql, connection, transaction))
        {
            closeCommand.Parameters.AddWithValue("opportunity_id", opportunityId);
            await closeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!request.RemindAt.HasValue)
        {
            await CreateSystemActivityAsync(connection, transaction, opportunityId, createdByUserId, "reminder", "Recordatorio eliminado", new { }, cancellationToken);
            await WriteAuditLogAsync(connection, createdByUserId, createdByLoginName, "reminder_clear", "opportunity", opportunityId.ToString(CultureInfo.InvariantCulture), ipAddress, userAgent, new { }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        const string insertSql = """
            INSERT INTO crm_reminders (
              opportunity_id,
              remind_at,
              notes,
              created_by_user_id
            )
            VALUES (@opportunity_id, @remind_at, @notes, @created_by_user_id)
            RETURNING id, remind_at, notes, created_by_user_id, created_at, completed_at;
            """;

        OpportunityReminderDto? reminder;
        await using (var insertCommand = new NpgsqlCommand(insertSql, connection, transaction))
        {
            insertCommand.Parameters.AddWithValue("opportunity_id", opportunityId);
            AddNullableTimestamp(insertCommand.Parameters, "remind_at", request.RemindAt);
            AddNullableText(insertCommand.Parameters, "notes", NormalizeNullableText(request.Notes));
            AddNullableInt64(insertCommand.Parameters, "created_by_user_id", createdByUserId);

            await using var reader = await insertCommand.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            reminder = new OpportunityReminderDto(
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                GetNullableString(reader, 2),
                GetNullableInt64(reader, 3),
                null,
                reader.GetFieldValue<DateTimeOffset>(4),
                GetNullableDateTimeOffset(reader, 5));
        }

        await CreateSystemActivityAsync(connection, transaction, opportunityId, createdByUserId, "reminder", "Recordatorio programado", new { request.RemindAt, request.Notes }, cancellationToken);
        await WriteAuditLogAsync(connection, createdByUserId, createdByLoginName, "reminder_upsert", "opportunity", opportunityId.ToString(CultureInfo.InvariantCulture), ipAddress, userAgent, new { request.RemindAt, request.Notes }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var authorName = createdByUserId.HasValue ? await ResolveUserNameAsync(connection, createdByUserId.Value, cancellationToken) : null;
        return reminder with { CreatedByUserName = authorName };
    }

    public async Task<PagedResultDto<SavedViewDto>> GetSavedViewsAsync(long userId, string? viewType, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = NormalizePage(page);
        pageSize = NormalizePageSize(pageSize);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string countSql = """
            SELECT COUNT(*)::int
            FROM crm_saved_views
            WHERE user_id = @user_id
              AND (@view_type = '' OR view_type = @view_type);
            """;

        await using var countCommand = new NpgsqlCommand(countSql, connection);
        countCommand.Parameters.AddWithValue("user_id", userId);
        countCommand.Parameters.AddWithValue("view_type", NormalizeNullableText(viewType) ?? string.Empty);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT id, user_id, view_type, name, filters_json::text, shared, created_at, updated_at
            FROM crm_saved_views
            WHERE user_id = @user_id
              AND (@view_type = '' OR view_type = @view_type)
            ORDER BY updated_at DESC, id DESC
            OFFSET @offset ROWS FETCH NEXT @page_size ROWS ONLY;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("view_type", NormalizeNullableText(viewType) ?? string.Empty);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        command.Parameters.AddWithValue("page_size", pageSize);

        var items = new List<SavedViewDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SavedViewDto(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return new PagedResultDto<SavedViewDto>(items, totalCount, page, pageSize);
    }

    public async Task<SavedViewDto?> UpsertSavedViewAsync(
        long? id,
        long userId,
        SavedViewUpsertRequest request,
        long? actorUserId,
        string? actorLoginName,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var normalizedFiltersJson = request.FiltersJson.Trim();
        var sql = id.HasValue
            ? """
              UPDATE crm_saved_views
              SET view_type = @view_type,
                  name = @name,
                  filters_json = @filters_json::jsonb,
                  shared = @shared,
                  updated_at = NOW()
              WHERE id = @id
                AND user_id = @user_id
              RETURNING id, user_id, view_type, name, filters_json::text, shared, created_at, updated_at;
              """
            : """
              INSERT INTO crm_saved_views (user_id, view_type, name, filters_json, shared)
              VALUES (@user_id, @view_type, @name, @filters_json::jsonb, @shared)
              RETURNING id, user_id, view_type, name, filters_json::text, shared, created_at, updated_at;
              """;

        await using var command = new NpgsqlCommand(sql, connection);
        if (id.HasValue)
        {
            command.Parameters.AddWithValue("id", id.Value);
        }

        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("view_type", request.ViewType.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("filters_json", normalizedFiltersJson);
        command.Parameters.AddWithValue("shared", request.Shared);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var dto = new SavedViewDto(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetBoolean(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
        await reader.CloseAsync();

        await WriteAuditLogAsync(connection, actorUserId, actorLoginName, id.HasValue ? "saved_view_update" : "saved_view_create", "saved_view", dto.Id.ToString(CultureInfo.InvariantCulture), ipAddress, userAgent, new { dto.ViewType, dto.Name }, cancellationToken);
        return dto;
    }

    public async Task<bool> DeleteSavedViewAsync(
        long id,
        long userId,
        long? actorUserId,
        string? actorLoginName,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            DELETE FROM crm_saved_views
            WHERE id = @id
              AND user_id = @user_id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("user_id", userId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected > 0)
        {
            await WriteAuditLogAsync(connection, actorUserId, actorLoginName, "saved_view_delete", "saved_view", id.ToString(CultureInfo.InvariantCulture), ipAddress, userAgent, new { }, cancellationToken);
        }

        return affected > 0;
    }
}
