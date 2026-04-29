using System.Text.Json;
using backend.Auth;
using Npgsql;
using NpgsqlTypes;

namespace backend;

public sealed partial class CrmRepository(NpgsqlDataSource dataSource, IConfiguration configuration)
{
    internal async Task<DashboardSummaryDto> GetDashboardAsync(AuthenticatedCrmUser? actor, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string summarySql = """
            SELECT
              (SELECT COUNT(*)::int FROM crm_zones WHERE active = TRUE) AS active_zones,
              (SELECT COUNT(*)::int FROM crm_users WHERE active = TRUE) AS active_users,
              (SELECT COUNT(*)::int FROM workflow_entity) AS workflow_count;
            """;

        await using var summaryCommand = new NpgsqlCommand(summarySql, connection);
        await using var summaryReader = await summaryCommand.ExecuteReaderAsync(cancellationToken);
        await summaryReader.ReadAsync(cancellationToken);

        var activeZones = summaryReader.GetInt32(0);
        var activeUsers = summaryReader.GetInt32(1);
        var workflowCount = summaryReader.GetInt32(2);
        await summaryReader.CloseAsync();
        var keywordRules = await LoadKeywordRuleSnapshotAsync(connection, cancellationToken);
        var rows = await LoadOpportunityRowsAsync(
            connection,
            null,
            null,
            null,
            null,
            null,
            null,
            CrmRoleRules.IsSeller(actor) ? actor!.Id : null,
            false,
            cancellationToken);
        var visibleOpportunities = FilterVisibleRows(rows, keywordRules, actor, OpportunityProcessCategory.All, false, false)
            .Select(MapOpportunityListItem)
            .ToList();

        var total = visibleOpportunities.Count;
        var invited = visibleOpportunities.Count(item => item.IsInvitedMatch);
        var assigned = visibleOpportunities.Count(item => !string.IsNullOrWhiteSpace(item.AssignedUserName));
        var unassigned = total - assigned;

        var statuses = visibleOpportunities
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Estado) ? "sin_estado" : item.Estado!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DashboardMetricDto(group.Key, group.Count()))
            .OrderByDescending(metric => metric.Count)
            .ThenBy(metric => metric.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var zoneCounts = visibleOpportunities
            .Where(item => !string.IsNullOrWhiteSpace(item.ZoneName))
            .GroupBy(item => item.ZoneName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        const string zoneSql = """
            SELECT id, name
            FROM crm_zones
            ORDER BY name ASC;
            """;

        var zoneLoads = new List<ZoneLoadDto>();
        await using (var zoneCommand = new NpgsqlCommand(zoneSql, connection))
        await using (var zoneReader = await zoneCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await zoneReader.ReadAsync(cancellationToken))
            {
                var zoneName = zoneReader.GetString(1);
                zoneLoads.Add(new ZoneLoadDto(
                    zoneReader.GetInt64(0),
                    zoneName,
                    zoneCounts.TryGetValue(zoneName, out var totalByZone) ? totalByZone : 0));
            }
        }

        zoneLoads = zoneLoads
            .OrderByDescending(zone => zone.Count)
            .ThenBy(zone => zone.ZoneName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (CrmRoleRules.IsSeller(actor))
        {
            activeUsers = 1;
            activeZones = zoneLoads.Count(zone => zone.Count > 0);
            zoneLoads = zoneLoads.Where(zone => zone.Count > 0).ToList();
        }

        return new DashboardSummaryDto(total, invited, assigned, unassigned, activeZones, activeUsers, workflowCount, statuses, zoneLoads);
    }

    public async Task<ManagementReportDto> GetManagementReportAsync(CancellationToken cancellationToken)
        => await GetManagementReportAsync("90d", null, null, cancellationToken);

    internal async Task<IReadOnlyList<OpportunityListItemDto>> GetOpportunitiesAsync(
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
        AuthenticatedCrmUser? actor,
        CancellationToken cancellationToken)
        => (await SearchOpportunitiesAsync(search, entity, processCode, keyword, estado, zoneId, assignedUserId, processCategory, invitedOnly, todayOnly, false, 1, 300, actor, cancellationToken)).Items;

    public async Task<OpportunityDetailDto?> GetOpportunityAsync(long id, CancellationToken cancellationToken)
        => await GetOpportunityCoreAsync(id, cancellationToken);

    internal async Task<OpportunityDetailDto?> GetOpportunityAsync(long id, AuthenticatedCrmUser? actor, CancellationToken cancellationToken)
        => await GetOpportunityCoreAsync(id, actor, cancellationToken);

    internal async Task<OpportunityDetailDto?> UpdateAssignmentAsync(long id, OpportunityAssignmentRequest request, long? changedByUserId, AuthenticatedCrmUser? actorScope, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string? previousStatus = null;
        long? previousAssignedUserId = null;
        long? previousZoneId = null;

        await using (var selectCommand = new NpgsqlCommand("SELECT estado, assigned_user_id, zone_id FROM opportunities WHERE id = @id;", connection, transaction))
        {
            selectCommand.Parameters.AddWithValue("id", id);
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            previousStatus = GetNullableString(reader, 0);
            previousAssignedUserId = GetNullableInt64(reader, 1);
            previousZoneId = GetNullableInt64(reader, 2);
        }

        var resolvedStatus = string.IsNullOrWhiteSpace(request.Estado)
            ? (request.AssignedUserId.HasValue ? "asignado" : previousStatus ?? "nuevo")
            : request.Estado.Trim();

        var resolvedPriority = string.IsNullOrWhiteSpace(request.Priority)
            ? "normal"
            : request.Priority.Trim().ToLowerInvariant();

        const string updateSql = """
            UPDATE opportunities
            SET
              assigned_user_id = @assigned_user_id,
              zone_id = @zone_id,
              estado = @estado,
              priority = @priority,
              crm_notes = @crm_notes,
              assignment_updated_at = NOW(),
              vendedor = CASE
                WHEN @assigned_user_id IS NULL THEN NULL
                ELSE (SELECT full_name FROM crm_users WHERE id = @assigned_user_id)
              END
            WHERE id = @id;
            """;

        await using (var updateCommand = new NpgsqlCommand(updateSql, connection, transaction))
        {
            updateCommand.Parameters.AddWithValue("id", id);
            AddNullableInt64(updateCommand.Parameters, "assigned_user_id", request.AssignedUserId);
            AddNullableInt64(updateCommand.Parameters, "zone_id", request.ZoneId);
            updateCommand.Parameters.AddWithValue("estado", resolvedStatus);
            updateCommand.Parameters.AddWithValue("priority", resolvedPriority);
            AddNullableText(updateCommand.Parameters, "crm_notes", NormalizeNullableText(request.Notes));
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (previousAssignedUserId != request.AssignedUserId || previousZoneId != request.ZoneId || !string.Equals(previousStatus, resolvedStatus, StringComparison.Ordinal))
        {
            const string historySql = """
                INSERT INTO crm_assignment_history (
                  opportunity_id,
                  assigned_user_id,
                  zone_id,
                  previous_status,
                  new_status,
                  notes
                )
                VALUES (@opportunity_id, @assigned_user_id, @zone_id, @previous_status, @new_status, @notes);
                """;

            await using var historyCommand = new NpgsqlCommand(historySql, connection, transaction);
            historyCommand.Parameters.AddWithValue("opportunity_id", id);
            AddNullableInt64(historyCommand.Parameters, "assigned_user_id", request.AssignedUserId);
            AddNullableInt64(historyCommand.Parameters, "zone_id", request.ZoneId);
            AddNullableText(historyCommand.Parameters, "previous_status", previousStatus);
            historyCommand.Parameters.AddWithValue("new_status", resolvedStatus);
            AddNullableText(historyCommand.Parameters, "notes", NormalizeNullableText(request.Notes));
            await historyCommand.ExecuteNonQueryAsync(cancellationToken);

            await CreateSystemActivityAsync(
                connection,
                transaction,
                id,
                changedByUserId,
                previousStatus != resolvedStatus ? "status_change" : "assignment",
                "Asignacion o estado actualizado",
                new
                {
                    previousAssignedUserId,
                    request.AssignedUserId,
                    previousZoneId,
                    request.ZoneId,
                    previousStatus,
                    resolvedStatus,
                    request.Notes
                },
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetOpportunityAsync(id, actorScope, cancellationToken);
    }

    internal async Task<OpportunityDetailDto?> UpdateInvitationAsync(
        long id,
        OpportunityInvitationUpdateRequest request,
        string invitedCompanyName,
        long? changedByUserId,
        AuthenticatedCrmUser? actorScope,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """
            UPDATE opportunities
            SET
              is_invited_match = @is_invited_match,
              invited_company_name = CASE WHEN @is_invited_match THEN @invited_company_name ELSE NULL END,
              invitation_source = CASE WHEN @is_invited_match THEN @invitation_source ELSE NULL END,
              invitation_notes = CASE WHEN @is_invited_match THEN @invitation_notes ELSE NULL END,
              invitation_evidence_url = CASE WHEN @is_invited_match THEN @invitation_evidence_url ELSE NULL END,
              invitation_verified_at = CASE WHEN @is_invited_match THEN NOW() ELSE NULL END,
              updated_at = NOW()
            WHERE id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("is_invited_match", request.IsInvitedMatch);
        command.Parameters.AddWithValue("invited_company_name", invitedCompanyName);
        AddNullableText(command.Parameters, "invitation_source", NormalizeNullableText(request.InvitationSource));
        AddNullableText(command.Parameters, "invitation_notes", NormalizeNullableText(request.InvitationNotes));
        AddNullableText(command.Parameters, "invitation_evidence_url", NormalizeNullableText(request.InvitationEvidenceUrl));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            return null;
        }

        await CreateSystemActivityAsync(
            connection,
            null,
            id,
            changedByUserId,
            "invitation_confirmation",
            request.IsInvitedMatch ? "Invitacion HDM confirmada o actualizada" : "Invitacion HDM retirada",
            new
            {
                request.IsInvitedMatch,
                request.InvitationSource,
                request.InvitationEvidenceUrl
            },
            cancellationToken);

        return await GetOpportunityAsync(id, actorScope, cancellationToken);
    }

    internal async Task<OpportunityDetailDto?> ImportOpportunityByCodeAsync(
        string code,
        SercopPublicClient sercopPublicClient,
        int fallbackYear,
        CancellationToken cancellationToken)
    {
        var normalizedCode = NormalizeNullableText(code);
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return null;
        }

        var imported = await sercopPublicClient.ResolveByCodeAsync(normalizedCode, fallbackYear, cancellationToken);
        if (imported is null)
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var keywordRules = await LoadKeywordRuleSnapshotAsync(connection, cancellationToken);

        var opportunityId = await UpsertImportedOpportunityAsync(connection, transaction, imported, keywordRules, "manual_import", cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetOpportunityAsync(opportunityId, cancellationToken);
    }

    public async Task<BulkInvitationImportResultDto> BulkImportInvitationsAsync(
        BulkInvitationImportRequest request,
        SercopPublicClient sercopPublicClient,
        string invitedCompanyName,
        int fallbackYear,
        CancellationToken cancellationToken)
    {
        var codes = ParseCodes(request.CodesText);
        if (codes.Count == 0)
        {
            return new BulkInvitationImportResultDto(0, 0, Array.Empty<string>(), Array.Empty<string>());
        }

        var updatedCodes = new List<string>();
        var unmatchedCodes = new List<string>();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var keywordRules = await LoadKeywordRuleSnapshotAsync(connection, cancellationToken);

        foreach (var code in codes)
        {
            var opportunityId = await FindOpportunityIdByCodeAsync(connection, transaction, code, cancellationToken);
            if (!opportunityId.HasValue)
            {
                var imported = await sercopPublicClient.ResolveByCodeAsync(code, fallbackYear, cancellationToken);
                if (imported is null)
                {
                    unmatchedCodes.Add(code);
                    continue;
                }

                opportunityId = await UpsertImportedOpportunityAsync(connection, transaction, imported, keywordRules, "manual_invitation_import", cancellationToken);
            }

            await ApplyInvitationConfirmationAsync(
                connection,
                transaction,
                opportunityId.Value,
                invitedCompanyName,
                request.InvitationSource,
                request.InvitationEvidenceUrl,
                request.InvitationNotes,
                cancellationToken);

            updatedCodes.Add(code);
        }

        await transaction.CommitAsync(cancellationToken);
        return new BulkInvitationImportResultDto(codes.Count, updatedCodes.Count, updatedCodes, unmatchedCodes);
    }

    public async Task<InvitationSyncResultDto> SyncInvitationsFromPublicReportsAsync(
        SercopInvitationPublicClient invitationClient,
        string invitedCompanySearch,
        string? invitedCompanyRuc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invitedCompanySearch) && string.IsNullOrWhiteSpace(invitedCompanyRuc))
        {
            return new InvitationSyncResultDto(0, 0, 0, Array.Empty<string>(), ["Configura INVITED_COMPANY_NAME o INVITED_COMPANY_RUC."]);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """
            SELECT id, COALESCE(process_code, ocid_or_nic) AS process_code, titulo, entidad, tipo
            FROM opportunities
            WHERE source = 'ocds'
              AND (is_invited_match = FALSE OR COALESCE(invitation_source, '') <> 'reporte_sercop')
            ORDER BY COALESCE(fecha_limite, fecha_publicacion, created_at) DESC NULLS LAST, id DESC
            LIMIT 200;
            """;

        var candidates = new List<OpportunityInvitationSyncCandidate>();
        await using (var command = new NpgsqlCommand(sql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new OpportunityInvitationSyncCandidate(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    GetNullableString(reader, 3),
                    GetNullableString(reader, 4)));
            }
        }

        var confirmedCodes = new List<string>();
        var errors = new List<string>();
        var updatedIds = new HashSet<long>();
        var confirmedCount = 0;

        foreach (var candidate in candidates)
        {
            try
            {
                var verification = await invitationClient.VerifyInvitationAsync(
                    candidate.ProcessCode,
                    candidate.Titulo,
                    candidate.Entidad,
                    candidate.Tipo,
                    invitedCompanySearch,
                    invitedCompanyRuc,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(verification.PublicProcessCode)
                    && !string.Equals(candidate.ProcessCode, verification.PublicProcessCode, StringComparison.OrdinalIgnoreCase))
                {
                    if (await UpdatePublicProcessCodeAsync(connection, candidate.Id, verification.PublicProcessCode!, cancellationToken) > 0)
                    {
                        updatedIds.Add(candidate.Id);
                    }
                }

                if (!verification.IsInvited)
                {
                    continue;
                }

                confirmedCount++;
                confirmedCodes.Add(verification.PublicProcessCode ?? candidate.ProcessCode);

                if (await ApplyPublicInvitationVerificationAsync(
                        connection,
                        candidate.Id,
                        verification.MatchedSupplierName ?? invitedCompanySearch,
                        verification.PublicProcessCode,
                        verification.EvidenceUrl,
                        verification.Notes,
                        cancellationToken) > 0)
                {
                    updatedIds.Add(candidate.Id);
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{candidate.ProcessCode}: {exception.Message}");
            }
        }

        return new InvitationSyncResultDto(candidates.Count, confirmedCount, updatedIds.Count, confirmedCodes, errors);
    }

    public async Task<IReadOnlyList<ZoneDto>> GetZonesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, name, code, description, active
            FROM crm_zones
            ORDER BY active DESC, name ASC;
            """;

        var zones = new List<ZoneDto>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            zones.Add(new ZoneDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                GetNullableString(reader, 3),
                reader.GetBoolean(4)));
        }

        return zones;
    }

    public async Task<ZoneDto?> UpsertZoneAsync(long? id, ZoneUpsertRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var sql = id.HasValue
            ? "UPDATE crm_zones SET name = @name, code = @code, description = @description, active = @active, updated_at = NOW() WHERE id = @id RETURNING id, name, code, description, active;"
            : "INSERT INTO crm_zones (name, code, description, active) VALUES (@name, @code, @description, @active) RETURNING id, name, code, description, active;";

        await using var command = new NpgsqlCommand(sql, connection);
        if (id.HasValue)
        {
            command.Parameters.AddWithValue("id", id.Value);
        }

        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("code", request.Code.Trim().ToUpperInvariant());
        AddNullableText(command.Parameters, "description", NormalizeNullableText(request.Description));
        command.Parameters.AddWithValue("active", request.Active);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ZoneDto(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            GetNullableString(reader, 3),
            reader.GetBoolean(4));
    }

    public async Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken cancellationToken)
        => (await SearchUsersAsync(1, 500, cancellationToken)).Items;

    public async Task<UserDto?> UpsertUserAsync(long? id, UserUpsertRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var sql = id.HasValue
            ? """
              UPDATE crm_users
              SET login_name = @login_name,
                  full_name = @full_name,
                  email = @email,
                  role = @role,
                  phone = @phone,
                  active = @active,
                  zone_id = @zone_id,
                  must_change_password = @must_change_password,
                  password_hash = COALESCE(@password_hash, password_hash),
                  updated_at = NOW()
              WHERE id = @id
              RETURNING id, login_name, full_name, email, role, phone, active, zone_id, must_change_password, last_login_at;
              """
            : """
              INSERT INTO crm_users (login_name, full_name, email, role, phone, active, zone_id, password_hash, must_change_password)
              VALUES (@login_name, @full_name, @email, @role, @phone, @active, @zone_id, @password_hash, @must_change_password)
              RETURNING id, login_name, full_name, email, role, phone, active, zone_id, must_change_password, last_login_at;
              """;

        await using var command = new NpgsqlCommand(sql, connection);
        if (id.HasValue)
        {
            command.Parameters.AddWithValue("id", id.Value);
        }

        command.Parameters.AddWithValue("login_name", request.LoginName.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("full_name", request.FullName.Trim());
        command.Parameters.AddWithValue("email", request.Email.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("role", request.Role.Trim().ToLowerInvariant());
        AddNullableText(command.Parameters, "phone", NormalizeNullableText(request.Phone));
        command.Parameters.AddWithValue("active", request.Active);
        AddNullableInt64(command.Parameters, "zone_id", request.ZoneId);
        AddNullableText(command.Parameters, "password_hash", string.IsNullOrWhiteSpace(request.Password) ? null : backend.Auth.CrmPasswordHasher.HashPassword(request.Password));
        command.Parameters.AddWithValue("must_change_password", request.MustChangePassword);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var userId = reader.GetInt64(0);
        var loginName = reader.GetString(1);
        var fullName = reader.GetString(2);
        var email = reader.GetString(3);
        var role = reader.GetString(4);
        var phone = GetNullableString(reader, 5);
        var active = reader.GetBoolean(6);
        var zoneId = GetNullableInt64(reader, 7);
        var mustChangePassword = reader.GetBoolean(8);
        var lastLoginAt = GetNullableDateTimeOffset(reader, 9);
        await reader.CloseAsync();

        var zoneName = await GetZoneNameAsync(connection, zoneId, cancellationToken);
        return new UserDto(
            userId,
            loginName,
            fullName,
            email,
            role,
            phone,
            active,
            zoneId,
            zoneName,
            mustChangePassword,
            lastLoginAt);
    }

    public async Task<IReadOnlyList<KeywordRuleDto>> GetKeywordRulesAsync(string? ruleType, string? scope, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, rule_type, scope, keyword, family, weight, notes, active, created_at, updated_at
            FROM keyword_rules
            WHERE (@rule_type = '' OR rule_type = @rule_type)
              AND (@scope = '' OR scope = @scope)
            ORDER BY rule_type ASC, active DESC, scope ASC, weight DESC, keyword ASC;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("rule_type", NormalizeOptionalKeywordRuleType(ruleType));
        command.Parameters.AddWithValue("scope", NormalizeOptionalKeywordRuleScope(scope));

        var items = new List<KeywordRuleDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new KeywordRuleDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                GetNullableString(reader, 4),
                reader.GetDecimal(5),
                GetNullableString(reader, 6),
                reader.GetBoolean(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return items;
    }

    public async Task<KeywordRuleDto?> UpsertKeywordRuleAsync(long? id, KeywordRuleUpsertRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var normalizedRuleType = NormalizeKeywordRuleType(request.RuleType);
        var normalizedScope = NormalizeKeywordRuleScope(request.Scope);
        var normalizedKeyword = NormalizeKeyword(request.Keyword);
        var normalizedFamily = NormalizeNullableText(request.Family);
        var normalizedNotes = NormalizeNullableText(request.Notes);
        var normalizedWeight = request.Weight <= 0 ? 1.00m : Math.Round(request.Weight, 2);

        var sql = id.HasValue
            ? """
            UPDATE keyword_rules
            SET
              rule_type = @rule_type,
              scope = @scope,
              keyword = @keyword,
              family = @family,
              weight = @weight,
              notes = @notes,
              active = @active,
              updated_at = NOW()
            WHERE id = @id
            RETURNING id, rule_type, scope, keyword, family, weight, notes, active, created_at, updated_at;
            """
            : """
            INSERT INTO keyword_rules (rule_type, scope, keyword, family, weight, notes, active)
            VALUES (@rule_type, @scope, @keyword, @family, @weight, @notes, @active)
            ON CONFLICT (rule_type, scope, keyword_normalized)
            DO UPDATE SET
              keyword = EXCLUDED.keyword,
              family = EXCLUDED.family,
              weight = EXCLUDED.weight,
              notes = EXCLUDED.notes,
              active = EXCLUDED.active,
              updated_at = NOW()
            RETURNING id, rule_type, scope, keyword, family, weight, notes, active, created_at, updated_at;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        if (id.HasValue)
        {
            command.Parameters.AddWithValue("id", id.Value);
        }

        command.Parameters.AddWithValue("rule_type", normalizedRuleType);
        command.Parameters.AddWithValue("scope", normalizedScope);
        command.Parameters.AddWithValue("keyword", normalizedKeyword);
        AddNullableText(command.Parameters, "family", normalizedFamily);
        command.Parameters.AddWithValue("weight", normalizedWeight);
        AddNullableText(command.Parameters, "notes", normalizedNotes);
        command.Parameters.AddWithValue("active", request.Active);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new KeywordRuleDto(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            GetNullableString(reader, 4),
            reader.GetDecimal(5),
            GetNullableString(reader, 6),
            reader.GetBoolean(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetFieldValue<DateTimeOffset>(9));
    }

    public async Task<bool> DeleteKeywordRuleAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            DELETE FROM keyword_rules
            WHERE id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);

        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        return affectedRows > 0;
    }

    public async Task<IReadOnlyList<WorkflowSummaryDto>> GetWorkflowsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, name, active, description, "updatedAt", json_array_length(nodes)::int AS node_count
            FROM workflow_entity
            ORDER BY name ASC;
            """;

        var workflows = new List<WorkflowSummaryDto>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            workflows.Add(new WorkflowSummaryDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                GetNullableString(reader, 3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetInt32(5)));
        }

        return workflows;
    }

    public async Task<WorkflowDetailDto?> GetWorkflowAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, name, active, description, "updatedAt", nodes::text, connections::text
            FROM workflow_entity
            WHERE id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var nodes = ParseNodes(reader.IsDBNull(5) ? "[]" : reader.GetString(5));
        return new WorkflowDetailDto(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetBoolean(2),
            GetNullableString(reader, 3),
            reader.GetFieldValue<DateTimeOffset>(4),
            nodes.Count,
            nodes,
            reader.IsDBNull(6) ? "{}" : reader.GetString(6));
    }

    public async Task<bool> ZoneExistsAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM crm_zones WHERE id = @id);", connection);
        command.Parameters.AddWithValue("id", id);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<bool> UserExistsAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM crm_users WHERE id = @id);", connection);
        command.Parameters.AddWithValue("id", id);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<IReadOnlyList<AssignmentHistoryItemDto>> GetAssignmentHistoryAsync(NpgsqlConnection connection, long opportunityId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT h.id, h.assigned_user_id, u.full_name, h.zone_id, z.name, h.previous_status, h.new_status, h.notes, h.changed_at
            FROM crm_assignment_history h
            LEFT JOIN crm_users u ON u.id = h.assigned_user_id
            LEFT JOIN crm_zones z ON z.id = h.zone_id
            WHERE h.opportunity_id = @opportunity_id
            ORDER BY h.changed_at DESC;
            """;

        var items = new List<AssignmentHistoryItemDto>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("opportunity_id", opportunityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new AssignmentHistoryItemDto(
                reader.GetInt64(0),
                GetNullableInt64(reader, 1),
                GetNullableString(reader, 2),
                GetNullableInt64(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return items;
    }

    private static async Task<string?> GetZoneNameAsync(NpgsqlConnection connection, long? zoneId, CancellationToken cancellationToken)
    {
        if (!zoneId.HasValue)
        {
            return null;
        }

        await using var command = new NpgsqlCommand("SELECT name FROM crm_zones WHERE id = @id;", connection);
        command.Parameters.AddWithValue("id", zoneId.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result?.ToString();
    }

    private static async Task<long?> FindOpportunityIdByCodeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string code,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id
            FROM opportunities
            WHERE external_id = @code
               OR ocid_or_nic = @code
               OR process_code = @code
               OR (source = 'ocds' AND ocid_or_nic ILIKE @ocid_pattern)
            ORDER BY id DESC
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("ocid_pattern", $"%{code}");
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long id ? id : result is int intId ? intId : null;
    }

    private async Task<long> UpsertImportedOpportunityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ImportedOpportunityCandidate candidate,
        KeywordRuleSnapshot keywordRules,
        string captureScope,
        CancellationToken cancellationToken)
    {
        var classification = BuildClassificationSnapshot(candidate.Source, candidate.ProcessCode, candidate.Tipo, candidate.Titulo, candidate.Entidad, candidate.RawPayloadJson, captureScope, keywordRules);

        const string sql = """
            INSERT INTO opportunities (
              source,
              external_id,
              ocid_or_nic,
              process_code,
              titulo,
              entidad,
              tipo,
              fecha_publicacion,
              fecha_limite,
              monto_ref,
              moneda,
              url,
              process_category,
              capture_scope,
              is_chemistry_candidate,
              classification_payload,
              keywords_hit,
              match_score,
              ai_score,
              recomendacion,
              estado,
              raw_payload
            )
            VALUES (
              @source,
              @external_id,
              @ocid_or_nic,
              @process_code,
              @titulo,
              @entidad,
              @tipo,
              @fecha_publicacion,
              @fecha_limite,
              @monto_ref,
              @moneda,
              @url,
              @process_category,
              @capture_scope,
              @is_chemistry_candidate,
              @classification_payload::jsonb,
              @keywords_hit,
              @match_score,
              0,
              @recomendacion,
              'nuevo',
              @raw_payload::jsonb
            )
            ON CONFLICT (source, external_id)
            DO UPDATE SET
              ocid_or_nic = EXCLUDED.ocid_or_nic,
              process_code = EXCLUDED.process_code,
              titulo = EXCLUDED.titulo,
              entidad = EXCLUDED.entidad,
              tipo = EXCLUDED.tipo,
              fecha_publicacion = COALESCE(EXCLUDED.fecha_publicacion, opportunities.fecha_publicacion),
              fecha_limite = COALESCE(EXCLUDED.fecha_limite, opportunities.fecha_limite),
              monto_ref = COALESCE(EXCLUDED.monto_ref, opportunities.monto_ref),
              moneda = COALESCE(NULLIF(EXCLUDED.moneda, ''), opportunities.moneda),
              url = COALESCE(NULLIF(EXCLUDED.url, ''), opportunities.url),
              process_category = EXCLUDED.process_category,
              capture_scope = EXCLUDED.capture_scope,
              is_chemistry_candidate = EXCLUDED.is_chemistry_candidate,
              classification_payload = EXCLUDED.classification_payload,
              keywords_hit = EXCLUDED.keywords_hit,
              match_score = EXCLUDED.match_score,
              recomendacion = EXCLUDED.recomendacion,
              raw_payload = EXCLUDED.raw_payload,
              updated_at = NOW()
            RETURNING id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("source", candidate.Source);
        command.Parameters.AddWithValue("external_id", candidate.ExternalId);
        command.Parameters.AddWithValue("ocid_or_nic", candidate.OcidOrNic);
        command.Parameters.AddWithValue("process_code", candidate.ProcessCode);
        command.Parameters.AddWithValue("titulo", candidate.Titulo);
        AddNullableText(command.Parameters, "entidad", candidate.Entidad);
        AddNullableText(command.Parameters, "tipo", candidate.Tipo);
        AddNullableTimestamp(command.Parameters, "fecha_publicacion", candidate.FechaPublicacion);
        AddNullableTimestamp(command.Parameters, "fecha_limite", candidate.FechaLimite);
        AddNullableDecimal(command.Parameters, "monto_ref", candidate.MontoRef);
        command.Parameters.AddWithValue("moneda", candidate.Moneda);
        command.Parameters.AddWithValue("url", candidate.Url);
        command.Parameters.AddWithValue("process_category", classification.ProcessCategory);
        command.Parameters.AddWithValue("capture_scope", classification.CaptureScope);
        command.Parameters.AddWithValue("is_chemistry_candidate", classification.IsChemistryCandidate);
        command.Parameters.AddWithValue("classification_payload", classification.PayloadJson);
        command.Parameters.AddWithValue("keywords_hit", classification.KeywordsHit.ToArray());
        command.Parameters.AddWithValue("match_score", classification.MatchScore);
        command.Parameters.AddWithValue("recomendacion", classification.Recommendation);
        command.Parameters.AddWithValue("raw_payload", candidate.RawPayloadJson);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long id ? id : Convert.ToInt64(result);
    }

    private static async Task ApplyInvitationConfirmationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long opportunityId,
        string invitedCompanyName,
        string? invitationSource,
        string? invitationEvidenceUrl,
        string? invitationNotes,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE opportunities
            SET
              is_invited_match = TRUE,
              invited_company_name = @invited_company_name,
              invitation_source = @invitation_source,
              invitation_evidence_url = @invitation_evidence_url,
              invitation_notes = @invitation_notes,
              invitation_verified_at = NOW(),
              updated_at = NOW()
            WHERE id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", opportunityId);
        command.Parameters.AddWithValue("invited_company_name", invitedCompanyName);
        AddNullableText(command.Parameters, "invitation_source", NormalizeNullableText(invitationSource));
        AddNullableText(command.Parameters, "invitation_evidence_url", NormalizeNullableText(invitationEvidenceUrl));
        AddNullableText(command.Parameters, "invitation_notes", NormalizeNullableText(invitationNotes));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> UpdatePublicProcessCodeAsync(
        NpgsqlConnection connection,
        long opportunityId,
        string processCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE opportunities
            SET process_code = @process_code,
                updated_at = NOW()
            WHERE id = @id
              AND COALESCE(process_code, '') <> @process_code;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", opportunityId);
        command.Parameters.AddWithValue("process_code", processCode.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ApplyPublicInvitationVerificationAsync(
        NpgsqlConnection connection,
        long opportunityId,
        string invitedCompanyName,
        string? processCode,
        string? invitationEvidenceUrl,
        string? invitationNotes,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE opportunities
            SET
              process_code = COALESCE(NULLIF(@process_code, ''), process_code),
              is_invited_match = TRUE,
              invited_company_name = @invited_company_name,
              invitation_source = 'reporte_sercop',
              invitation_evidence_url = @invitation_evidence_url,
              invitation_notes = @invitation_notes,
              invitation_verified_at = NOW(),
              updated_at = NOW()
            WHERE id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", opportunityId);
        command.Parameters.AddWithValue("process_code", NormalizeNullableText(processCode) ?? string.Empty);
        command.Parameters.AddWithValue("invited_company_name", invitedCompanyName.Trim());
        AddNullableText(command.Parameters, "invitation_evidence_url", NormalizeNullableText(invitationEvidenceUrl));
        AddNullableText(command.Parameters, "invitation_notes", NormalizeNullableText(invitationNotes));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<KeywordRuleSnapshot> LoadKeywordRuleSnapshotAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT rule_type, keyword_normalized, family, active, weight
            FROM keyword_rules
            """;

        var includeKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludeKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var familyByKeyword = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var includeWeights = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var excludeWeights = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var ruleType = reader.GetString(0);
            var keyword = TextNormalization.NormalizeForComparison(reader.GetString(1));
            var family = GetNullableString(reader, 2);
            var active = reader.GetBoolean(3);
            var weight = reader.GetDecimal(4);

            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(family))
            {
                familyByKeyword[keyword] = family.Trim();
            }

            if (!active)
            {
                continue;
            }

            if (string.Equals(ruleType, "include", StringComparison.OrdinalIgnoreCase))
            {
                includeKeywords.Add(keyword);
                includeWeights[keyword] = weight <= 0 ? 1m : weight;
            }
            else if (string.Equals(ruleType, "exclude", StringComparison.OrdinalIgnoreCase))
            {
                excludeKeywords.Add(keyword);
                excludeWeights[keyword] = weight <= 0 ? 1m : weight;
            }
        }

        return new KeywordRuleSnapshot(
            includeKeywords.ToArray(),
            excludeKeywords.ToArray(),
            familyByKeyword,
            includeWeights,
            excludeWeights);
    }

    private static IReadOnlyList<string> ParseCodes(string? rawValue)
        => string.IsNullOrWhiteSpace(rawValue)
            ? Array.Empty<string>()
            : rawValue
                .Split(new[] { '\r', '\n', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string NormalizeOptionalKeywordRuleType(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : NormalizeKeywordRuleType(value);

    private static string NormalizeOptionalKeywordRuleScope(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : NormalizeKeywordRuleScope(value);

    private static string NormalizeKeywordRuleType(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "include" or "exclude"
            ? normalized
            : throw new ArgumentException("ruleType debe ser include o exclude.");
    }

    private static string NormalizeKeywordRuleScope(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "all" or "ocds" or "nco"
            ? normalized
            : throw new ArgumentException("scope debe ser all, ocds o nco.");
    }

    private static string NormalizeKeyword(string value)
    {
        var normalized = TextNormalization.NormalizeKeywordDisplay(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("keyword es obligatorio.");
        }

        return normalized;
    }

    private static string? NormalizeNullableText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsParticipating(ManagementOpportunityRow row)
        => !IsDecided(row);

    private static bool IsWon(ManagementOpportunityRow row)
        => NormalizeLifecycleValue(row.Resultado, row.Estado) == "ganado";

    private static bool IsLost(ManagementOpportunityRow row)
        => NormalizeLifecycleValue(row.Resultado, row.Estado) == "perdido";

    private static bool IsNotPresented(ManagementOpportunityRow row)
        => NormalizeLifecycleValue(row.Resultado, row.Estado) == "no_presentado";

    private static bool IsDecided(ManagementOpportunityRow row)
        => IsWon(row) || IsLost(row) || IsNotPresented(row);

    private static string NormalizeLifecycleValue(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = NormalizeNullableText(value)?.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized.Replace(' ', '_');
            }
        }

        return string.Empty;
    }

    private static List<WorkflowNodeDto> ParseNodes(string json)
    {
        var nodes = new List<WorkflowNodeDto>();

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return nodes;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var id = element.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? string.Empty : string.Empty;
            var name = element.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
            var type = element.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? string.Empty : string.Empty;
            var disabled = element.TryGetProperty("disabled", out var disabledProp) && disabledProp.ValueKind == JsonValueKind.True;
            var x = 0d;
            var y = 0d;

            if (element.TryGetProperty("position", out var positionProp) && positionProp.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var positionValue in positionProp.EnumerateArray())
                {
                    if (index == 0)
                    {
                        x = positionValue.GetDouble();
                    }
                    else if (index == 1)
                    {
                        y = positionValue.GetDouble();
                    }

                    index++;
                }
            }

            nodes.Add(new WorkflowNodeDto(id, name, type, x, y, disabled));
        }

        return nodes;
    }

    private static void AddNullableInt64(NpgsqlParameterCollection parameters, string name, long? value)
    {
        var parameter = parameters.Add(name, NpgsqlDbType.Bigint);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private static void AddNullableDecimal(NpgsqlParameterCollection parameters, string name, decimal? value)
    {
        var parameter = parameters.Add(name, NpgsqlDbType.Numeric);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private static void AddNullableTimestamp(NpgsqlParameterCollection parameters, string name, DateTimeOffset? value)
    {
        var parameter = parameters.Add(name, NpgsqlDbType.TimestampTz);
        parameter.Value = value.HasValue ? value.Value.ToUniversalTime() : DBNull.Value;
    }

    private static void AddNullableText(NpgsqlParameterCollection parameters, string name, string? value)
    {
        var parameter = parameters.Add(name, NpgsqlDbType.Text);
        parameter.Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    }

    private static string? GetNullableString(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetNullableInt64(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static decimal? GetNullableDecimal(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static DateTimeOffset? GetNullableDateTimeOffset(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private sealed record OpportunityInvitationSyncCandidate(
        long Id,
        string ProcessCode,
        string Titulo,
        string? Entidad,
        string? Tipo);

    private sealed record ManagementOpportunityRow(
        long Id,
        string Titulo,
        string? Entidad,
        string? Tipo,
        string? Estado,
        string? Resultado,
        decimal? MontoRef,
        long? AssignedUserId,
        string? AssignedUserName,
        IReadOnlyList<string> KeywordsHit);
}
