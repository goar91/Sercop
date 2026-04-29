using System.Globalization;
using System.Text;
using System.Text.Json;
using backend.Auth;
using Npgsql;

namespace backend;

public sealed partial class CrmRepository
{
    private async Task<IReadOnlyList<OpportunityProjectionRow>> LoadOpportunityRowsAsync(
        NpgsqlConnection connection,
        string? search,
        string? entity,
        string? processCode,
        string? keyword,
        string? estado,
        long? zoneId,
        long? assignedUserId,
        bool invitedOnly,
        CancellationToken cancellationToken)
    {
        var retentionCutoff = EcuadorTime.Now().AddDays(-GetRetentionDays()).ToUniversalTime();
        var searchTokens = TextNormalization.TokenizeSearch(search);
        var entityTokens = TextNormalization.TokenizeSearch(entity);
        var processCodeTokens = TextNormalization.TokenizeSearch(processCode);
        var normalizedKeyword = TextNormalization.NormalizeForComparison(keyword);
        var sqlBuilder = new StringBuilder("""
            SELECT
              o.id,
              o.source,
              o.external_id,
              o.ocid_or_nic,
              COALESCE(o.process_code, o.ocid_or_nic) AS process_code,
              o.titulo,
              o.entidad,
              o.tipo,
              o.fecha_publicacion,
              o.fecha_limite,
              o.monto_ref,
              o.moneda,
              o.url,
              o.invited_company_name,
              o.is_invited_match,
              o.invitation_source,
              o.invitation_notes,
              o.invitation_evidence_url,
              o.invitation_verified_at,
              o.process_category,
              o.capture_scope,
              o.is_chemistry_candidate,
              o.classification_payload::text,
              o.match_score,
              o.recomendacion,
              o.estado,
              o.vendedor,
              o.resultado,
              o.priority,
              o.crm_notes,
              o.assignment_updated_at,
              o.created_at,
              o.keywords_hit,
              z.id AS zone_id,
              z.name AS zone_name,
              z.code AS zone_code,
              u.id AS assigned_user_id,
              u.full_name AS assigned_user_name,
              u.email AS assigned_user_email,
              activity.last_activity_at,
              activity.activity_count,
              reminder.id AS reminder_id,
              reminder.remind_at,
              reminder.notes,
              reminder.created_by_user_id,
              reminder.created_by_user_name,
              reminder.created_at,
              reminder.completed_at
            FROM opportunities o
            LEFT JOIN crm_zones z ON z.id = o.zone_id
            LEFT JOIN crm_users u ON u.id = o.assigned_user_id
            LEFT JOIN LATERAL (
              SELECT MAX(created_at) AS last_activity_at,
                     COUNT(*)::int AS activity_count
              FROM crm_opportunity_activities a
              WHERE a.opportunity_id = o.id
            ) activity ON TRUE
            LEFT JOIN LATERAL (
              SELECT r.id,
                     r.remind_at,
                     r.notes,
                     r.created_by_user_id,
                     ru.full_name AS created_by_user_name,
                     r.created_at,
                     r.completed_at
              FROM crm_reminders r
              LEFT JOIN crm_users ru ON ru.id = r.created_by_user_id
              WHERE r.opportunity_id = o.id
                AND r.completed_at IS NULL
              ORDER BY r.remind_at ASC, r.created_at DESC
              LIMIT 1
            ) reminder ON TRUE
            WHERE (@estado = '' OR COALESCE(o.estado, '') = @estado)
              AND (@has_zone_filter = FALSE OR o.zone_id = @zone_id)
              AND (@has_assigned_user_filter = FALSE OR o.assigned_user_id = @assigned_user_id)
              AND (@invited_only = FALSE OR o.is_invited_match = TRUE)
              AND (@keyword = '' OR @keyword = ANY(COALESCE(o.keywords_hit, ARRAY[]::text[])))
              AND o.fecha_publicacion >= @retention_cutoff
              /*__DYNAMIC_FILTERS__*/
            ORDER BY o.is_invited_match DESC, COALESCE(o.fecha_publicacion, o.created_at) DESC NULLS LAST, o.id DESC;
            """);

        if (searchTokens.Length > 0 || entityTokens.Length > 0 || processCodeTokens.Length > 0)
        {
            var clause = new StringBuilder();

            foreach (var (_, index) in searchTokens.Select((value, idx) => (value, idx)))
            {
                clause.Append($"  AND o.search_document_normalized LIKE @search_token_{index} ESCAPE '\\'\n");
            }

            foreach (var (_, index) in entityTokens.Select((value, idx) => (value, idx)))
            {
                clause.Append($"  AND crm_normalize_text(COALESCE(o.entidad, '')) LIKE @entity_token_{index} ESCAPE '\\'\n");
            }

            foreach (var (_, index) in processCodeTokens.Select((value, idx) => (value, idx)))
            {
                clause.Append($"  AND o.search_document_normalized LIKE @process_code_token_{index} ESCAPE '\\'\n");
            }

            sqlBuilder.Replace("/*__DYNAMIC_FILTERS__*/", clause.ToString());
        }
        else
        {
            sqlBuilder.Replace("/*__DYNAMIC_FILTERS__*/", string.Empty);
        }

        await using var command = new NpgsqlCommand(sqlBuilder.ToString(), connection);
        command.Parameters.AddWithValue("estado", estado?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("has_zone_filter", zoneId.HasValue);
        command.Parameters.AddWithValue("zone_id", zoneId ?? -1L);
        command.Parameters.AddWithValue("has_assigned_user_filter", assignedUserId.HasValue);
        command.Parameters.AddWithValue("assigned_user_id", assignedUserId ?? -1L);
        command.Parameters.AddWithValue("invited_only", invitedOnly);
        command.Parameters.AddWithValue("keyword", normalizedKeyword);
        command.Parameters.AddWithValue("retention_cutoff", retentionCutoff);
        for (var index = 0; index < searchTokens.Length; index++)
        {
            command.Parameters.AddWithValue($"search_token_{index}", $"%{TextNormalization.EscapeLikeToken(searchTokens[index])}%");
        }

        for (var index = 0; index < entityTokens.Length; index++)
        {
            command.Parameters.AddWithValue($"entity_token_{index}", $"%{TextNormalization.EscapeLikeToken(entityTokens[index])}%");
        }

        for (var index = 0; index < processCodeTokens.Length; index++)
        {
            command.Parameters.AddWithValue($"process_code_token_{index}", $"%{TextNormalization.EscapeLikeToken(processCodeTokens[index])}%");
        }

        var rows = new List<OpportunityProjectionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var reminder = GetNullableInt64(reader, 41) is long reminderId
                ? new ReminderProjection(
                    reminderId,
                    reader.GetFieldValue<DateTimeOffset>(42),
                    GetNullableString(reader, 43),
                    GetNullableInt64(reader, 44),
                    GetNullableString(reader, 45),
                    reader.GetFieldValue<DateTimeOffset>(46),
                    GetNullableDateTimeOffset(reader, 47))
                : null;

            rows.Add(new OpportunityProjectionRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                GetNullableDateTimeOffset(reader, 8),
                GetNullableDateTimeOffset(reader, 9),
                GetNullableDecimal(reader, 10),
                reader.GetString(11),
                reader.GetString(12),
                GetNullableString(reader, 13),
                reader.GetBoolean(14),
                GetNullableString(reader, 15),
                GetNullableString(reader, 16),
                GetNullableString(reader, 17),
                GetNullableDateTimeOffset(reader, 18),
                GetNullableString(reader, 19),
                GetNullableString(reader, 20),
                reader.IsDBNull(21) ? false : reader.GetBoolean(21),
                GetNullableString(reader, 22),
                reader.GetDecimal(23),
                GetNullableString(reader, 24),
                GetNullableString(reader, 25),
                GetNullableString(reader, 26),
                GetNullableString(reader, 27),
                reader.GetString(28),
                GetNullableString(reader, 29),
                GetNullableDateTimeOffset(reader, 30),
                reader.GetFieldValue<DateTimeOffset>(31),
                reader.IsDBNull(32) ? Array.Empty<string>() : reader.GetFieldValue<string[]>(32),
                GetNullableInt64(reader, 33),
                GetNullableString(reader, 34),
                GetNullableString(reader, 35),
                GetNullableInt64(reader, 36),
                GetNullableString(reader, 37),
                GetNullableString(reader, 38),
                GetNullableDateTimeOffset(reader, 39),
                reader.IsDBNull(40) ? 0 : reader.GetInt32(40),
                reminder));
        }

        return rows;
    }

    private async Task<OpportunityProjectionRow?> LoadOpportunityRowByCodeAsync(
        NpgsqlConnection connection,
        string code,
        CancellationToken cancellationToken)
    {
        var rows = await LoadOpportunityRowsAsync(connection, code, null, null, null, null, null, null, false, cancellationToken);
        return rows.FirstOrDefault(row =>
            string.Equals(row.ProcessCode, code, StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.OcidOrNic, code, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<OpportunityProjectionRow?> LoadOpportunityRowByIdAsync(
        NpgsqlConnection connection,
        long id,
        CancellationToken cancellationToken)
    {
        var rows = await LoadOpportunityRowsAsync(connection, null, null, null, null, null, null, null, false, cancellationToken);
        return rows.FirstOrDefault(row => row.Id == id);
    }

    private List<VisibleOpportunityRow> FilterVisibleRows(
        IReadOnlyList<OpportunityProjectionRow> rows,
        KeywordRuleSnapshot keywordRules,
        AuthenticatedCrmUser? actor,
        string processCategory,
        bool todayOnly,
        bool chemistryOnly)
    {
        var visible = new List<VisibleOpportunityRow>(rows.Count);
        foreach (var row in rows)
        {
            if (actor is not null && !CanActorAccessOpportunity(actor, row))
            {
                continue;
            }

            var evaluation = EvaluateVisibility(row, keywordRules, todayOnly, chemistryOnly);
            if (!evaluation.IsVisible)
            {
                continue;
            }

            var category = ResolveStoredProcessCategory(row.ProcessCategory, row.Source, row.Tipo, row.ProcessCode);
            if (!OpportunityProcessCategory.MatchesFilter(category, processCategory))
            {
                continue;
            }

            visible.Add(new VisibleOpportunityRow(row, BuildDerivedMetrics(row), category));
        }

        return visible
            .OrderByDescending(item => item.Row.IsInvitedMatch)
            .ThenByDescending(item => item.Metrics.SortDate)
            .ThenByDescending(item => item.Row.Id)
            .ToList();
    }

    private static bool CanActorAccessOpportunity(AuthenticatedCrmUser actor, OpportunityProjectionRow row)
    {
        if (!CrmRoleRules.IsSeller(actor))
        {
            return true;
        }

        // Los vendedores pueden ver procesos sin asignar y procesos asignados a ellos
        return !row.AssignedUserId.HasValue || row.AssignedUserId == actor.Id;
    }

    private OpportunityVisibilityEvaluation EvaluateVisibility(
        OpportunityProjectionRow row,
        KeywordRuleSnapshot keywordRules,
        bool todayOnly,
        bool chemistryOnly)
    {
        var reasons = new List<string>();
        if (chemistryOnly)
        {
            if (!row.IsChemistryCandidate)
            {
                reasons.Add("No se muestra porque la clasificación persistida lo marcó fuera del módulo químico.");
            }
        }

        if (todayOnly && !IsCurrentDay(row))
        {
            reasons.Add("No se muestra porque el filtro activo solo permite procesos del dia actual en Ecuador.");
        }

        return new OpportunityVisibilityEvaluation(reasons.Count == 0, reasons);
    }

    private static bool IsCurrentDay(OpportunityProjectionRow row)
    {
        var today = EcuadorTime.Now().Date;
        return row.FechaPublicacion?.Date == today || row.FechaLimite?.Date == today;
    }

    private DerivedOpportunityMetrics BuildDerivedMetrics(OpportunityProjectionRow row)
    {
        var now = EcuadorTime.Now();
        var origin = row.FechaPublicacion ?? row.CreatedAt;
        var daysOpen = Math.Max(0, (int)Math.Floor((now - origin).TotalDays));
        var agingBucket = daysOpen <= 1 ? "0-1 dia" : daysOpen <= 7 ? "2-7 dias" : ">7 dias";
        var nextActionAt = row.Reminder?.RemindAt;
        var hasPendingAction = !row.AssignedUserId.HasValue || (nextActionAt.HasValue && nextActionAt.Value <= now.AddDays(1));
        var slaStatus = ComputeSlaStatus(row, now);
        var sortDate = row.FechaPublicacion ?? row.CreatedAt;
        return new DerivedOpportunityMetrics(daysOpen, agingBucket, row.LastActivityAt, nextActionAt, hasPendingAction, slaStatus, sortDate);
    }

    private static string ComputeSlaStatus(OpportunityProjectionRow row, DateTimeOffset now)
    {
        if (!row.AssignedUserId.HasValue)
        {
            return "sin_asignar";
        }

        if (row.Reminder?.RemindAt is DateTimeOffset remindAt && remindAt < now)
        {
            return "recordatorio_vencido";
        }

        if (row.FechaLimite is DateTimeOffset deadline && deadline < now)
        {
            return "vencido";
        }

        if (row.LastActivityAt.HasValue && row.LastActivityAt.Value < now.AddDays(-2))
        {
            return "sin_seguimiento";
        }

        return "al_dia";
    }

    private static bool IsExpiringSoon(VisibleOpportunityRow row)
    {
        if (!row.Row.FechaLimite.HasValue)
        {
            return false;
        }

        var now = EcuadorTime.Now();
        return row.Row.FechaLimite.Value >= now && row.Row.FechaLimite.Value <= now.AddHours(24);
    }

    private static int AgingSortOrder(string bucket)
        => bucket switch
        {
            "0-1 dia" => 1,
            "2-7 dias" => 2,
            ">7 dias" => 3,
            _ => 99,
        };

    private static OpportunityListItemDto MapOpportunityListItem(VisibleOpportunityRow item)
        => new(
            item.Row.Id,
            item.Row.Source,
            item.Row.ExternalId,
            item.Row.OcidOrNic,
            item.Row.ProcessCode,
            item.Row.Titulo,
            item.Row.Entidad,
            item.Row.Tipo,
            item.ProcessCategory,
            item.Row.FechaPublicacion,
            item.Row.FechaLimite,
            item.Row.MontoRef,
            item.Row.Url,
            item.Row.InvitedCompanyName,
            item.Row.IsInvitedMatch,
            item.Row.InvitationSource,
            item.Row.InvitationVerifiedAt,
            ResolveCaptureScope(item.Row.CaptureScope, item.ProcessCategory),
            item.Row.IsChemistryCandidate,
            item.Row.MatchScore,
            item.Row.Recomendacion,
            item.Row.Estado,
            item.Row.Resultado,
            item.Row.Priority,
            item.Row.ZoneName,
            item.Row.AssignedUserName,
            item.Metrics.DaysOpen,
            item.Metrics.AgingBucket,
            item.Metrics.LastActivityAt,
            item.Metrics.NextActionAt,
            item.Metrics.HasPendingAction,
            item.Metrics.SlaStatus);

    private async Task<OpportunityDetailDto?> GetOpportunityCoreAsync(long id, CancellationToken cancellationToken)
        => await GetOpportunityCoreAsync(id, null, cancellationToken);

    private async Task<OpportunityDetailDto?> GetOpportunityCoreAsync(long id, AuthenticatedCrmUser? actor, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await LoadOpportunityRowByIdAsync(connection, id, cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (actor is not null && !CanActorAccessOpportunity(actor, row))
        {
            return null;
        }

        var history = await GetAssignmentHistoryAsync(connection, id, cancellationToken);
        var metrics = BuildDerivedMetrics(row);
        return new OpportunityDetailDto(
            row.Id,
            row.Source,
            row.ExternalId,
            row.OcidOrNic,
            row.ProcessCode,
            row.Titulo,
            row.Entidad,
            row.Tipo,
            ResolveStoredProcessCategory(row.ProcessCategory, row.Source, row.Tipo, row.ProcessCode),
            row.FechaPublicacion,
            row.FechaLimite,
            row.MontoRef,
            row.Moneda,
            row.Url,
            row.InvitedCompanyName,
            row.IsInvitedMatch,
            row.InvitationSource,
            row.InvitationNotes,
            row.InvitationEvidenceUrl,
            row.InvitationVerifiedAt,
            ResolveCaptureScope(row.CaptureScope, ResolveStoredProcessCategory(row.ProcessCategory, row.Source, row.Tipo, row.ProcessCode)),
            row.IsChemistryCandidate,
            ParseClassificationReasons(row.ClassificationPayloadText),
            row.MatchScore,
            row.Recomendacion,
            row.Estado,
            row.Vendedor,
            row.Resultado,
            row.Priority,
            row.CrmNotes,
            row.AssignmentUpdatedAt,
            row.ZoneId,
            row.ZoneName,
            row.ZoneCode,
            row.AssignedUserId,
            row.AssignedUserName,
            row.AssignedUserEmail,
            metrics.DaysOpen,
            metrics.AgingBucket,
            metrics.LastActivityAt,
            metrics.NextActionAt,
            metrics.HasPendingAction,
            metrics.SlaStatus,
            row.Reminder is null ? null : new OpportunityReminderDto(
                row.Reminder.Id,
                row.Reminder.RemindAt,
                row.Reminder.Notes,
                row.Reminder.CreatedByUserId,
                row.Reminder.CreatedByUserName,
                row.Reminder.CreatedAt,
                row.Reminder.CompletedAt),
            history);
    }

    private static int NormalizePage(int page)
        => page < 1 ? 1 : page;

    private static int NormalizePageSize(int pageSize)
        => pageSize switch
        {
            <= 0 => 25,
            > 100 => 100,
            _ => pageSize,
        };

    private int GetRetentionDays()
        => int.TryParse(configuration["CRM_RETENTION_DAYS"], out var days) && days >= 1
            ? Math.Clamp(days, 1, 30)
            : 5;

    private decimal GetMatchThreshold()
        => decimal.TryParse(configuration["MATCH_THRESHOLD"], NumberStyles.Number, CultureInfo.InvariantCulture, out var threshold)
            ? Math.Clamp(threshold, 0m, 100m)
            : 60m;

    private static async Task CreateSystemActivityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long opportunityId,
        long? createdByUserId,
        string activityType,
        string? body,
        object details,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO crm_opportunity_activities (
              opportunity_id,
              activity_type,
              body,
              metadata_json,
              created_by_user_id
            )
            VALUES (@opportunity_id, @activity_type, @body, @metadata_json::jsonb, @created_by_user_id);
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("opportunity_id", opportunityId);
        command.Parameters.AddWithValue("activity_type", activityType);
        AddNullableText(command.Parameters, "body", NormalizeNullableText(body));
        command.Parameters.AddWithValue("metadata_json", JsonSerializer.Serialize(details));
        AddNullableInt64(command.Parameters, "created_by_user_id", createdByUserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ResolveUserNameAsync(NpgsqlConnection connection, long userId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT full_name FROM crm_users WHERE id = @id;";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", userId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private sealed record OpportunityProjectionRow(
        long Id,
        string Source,
        string ExternalId,
        string OcidOrNic,
        string ProcessCode,
        string Titulo,
        string? Entidad,
        string? Tipo,
        DateTimeOffset? FechaPublicacion,
        DateTimeOffset? FechaLimite,
        decimal? MontoRef,
        string Moneda,
        string Url,
        string? InvitedCompanyName,
        bool IsInvitedMatch,
        string? InvitationSource,
        string? InvitationNotes,
        string? InvitationEvidenceUrl,
        DateTimeOffset? InvitationVerifiedAt,
        string? ProcessCategory,
        string? CaptureScope,
        bool IsChemistryCandidate,
        string? ClassificationPayloadText,
        decimal MatchScore,
        string? Recomendacion,
        string? Estado,
        string? Vendedor,
        string? Resultado,
        string Priority,
        string? CrmNotes,
        DateTimeOffset? AssignmentUpdatedAt,
        DateTimeOffset CreatedAt,
        IReadOnlyList<string> KeywordsHit,
        long? ZoneId,
        string? ZoneName,
        string? ZoneCode,
        long? AssignedUserId,
        string? AssignedUserName,
        string? AssignedUserEmail,
        DateTimeOffset? LastActivityAt,
        int ActivityCount,
        ReminderProjection? Reminder);

    private sealed record ReminderProjection(
        long Id,
        DateTimeOffset RemindAt,
        string? Notes,
        long? CreatedByUserId,
        string? CreatedByUserName,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CompletedAt);

    private sealed record DerivedOpportunityMetrics(
        int DaysOpen,
        string AgingBucket,
        DateTimeOffset? LastActivityAt,
        DateTimeOffset? NextActionAt,
        bool HasPendingAction,
        string SlaStatus,
        DateTimeOffset SortDate);

    private sealed record VisibleOpportunityRow(
        OpportunityProjectionRow Row,
        DerivedOpportunityMetrics Metrics,
        string ProcessCategory);

    private sealed record OpportunityVisibilityEvaluation(
        bool IsVisible,
        IReadOnlyList<string> Reasons);
}
