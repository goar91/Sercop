using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace backend;

public sealed partial class CrmRepository(NpgsqlDataSource dataSource, ILogger<CrmRepository> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<DashboardSummaryDto> GetDashboardAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var (total, invited, assigned, unassigned) = await LoadOpportunityCountersAsync(connection, cancellationToken);
        var activeZones = await ExecuteCountAsync(connection, "select count(*) from crm_zones where active", cancellationToken);
        var activeUsers = await ExecuteCountAsync(connection, "select count(*) from crm_users where active", cancellationToken);
        var workflowCount = await ExecuteCountAsync(connection, "select count(*) from workflow_entity", cancellationToken);
        var statuses = await LoadDashboardStatusesAsync(connection, cancellationToken);
        var zoneLoads = await LoadZoneLoadsAsync(connection, cancellationToken);

        return new DashboardSummaryDto(
            total,
            invited,
            assigned,
            unassigned,
            activeZones,
            activeUsers,
            workflowCount,
            statuses,
            zoneLoads);
    }

    public async Task<IReadOnlyList<OpportunityListItemDto>> GetOpportunitiesAsync(
        string? search,
        string? estado,
        long? zoneId,
        long? assignedUserId,
        bool invitedOnly,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                id,
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
                url,
                invited_company_name,
                is_invited_match,
                invitation_source,
                invitation_verified_at,
                match_score,
                ai_score,
                recomendacion,
                estado,
                resultado,
                priority,
                zone_name,
                assigned_user_name
            from crm_opportunity_overview
            where
                (fecha_publicacion at time zone 'America/Guayaquil')::date >= (now() at time zone 'America/Guayaquil')::date
                and
                (@search is null
                    or titulo ilike '%' || @search || '%'
                    or coalesce(entidad, '') ilike '%' || @search || '%'
                    or ocid_or_nic ilike '%' || @search || '%'
                    or coalesce(process_code, '') ilike '%' || @search || '%')
                and (@estado is null or estado = @estado)
                and (@zone_id is null or zone_id = @zone_id)
                and (@assigned_user_id is null or assigned_user_id = @assigned_user_id)
                and (not @invited_only or is_invited_match)
            order by
                is_invited_match desc,
                fecha_publicacion desc nulls last,
                fecha_limite asc nulls last,
                id desc
            limit 250;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("search", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)NullIfWhiteSpace(search) ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("estado", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)NullIfWhiteSpace(estado) ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("zone_id", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)zoneId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("assigned_user_id", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)assignedUserId ?? DBNull.Value });
        command.Parameters.AddWithValue("invited_only", invitedOnly);

        var items = new List<OpportunityListItemDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new OpportunityListItemDto(
                GetInt64(reader, "id"),
                GetString(reader, "source"),
                GetString(reader, "external_id"),
                GetString(reader, "ocid_or_nic"),
                GetNullableString(reader, "process_code") ?? GetString(reader, "ocid_or_nic"),
                GetString(reader, "titulo"),
                GetNullableString(reader, "entidad"),
                GetNullableString(reader, "tipo"),
                GetNullableDateTimeOffset(reader, "fecha_publicacion"),
                GetNullableDateTimeOffset(reader, "fecha_limite"),
                GetNullableDecimal(reader, "monto_ref"),
                GetString(reader, "url"),
                GetNullableString(reader, "invited_company_name"),
                GetBoolean(reader, "is_invited_match"),
                GetNullableString(reader, "invitation_source"),
                GetNullableDateTimeOffset(reader, "invitation_verified_at"),
                GetDecimal(reader, "match_score"),
                GetDecimal(reader, "ai_score"),
                GetNullableString(reader, "recomendacion"),
                GetNullableString(reader, "estado"),
                GetNullableString(reader, "resultado"),
                GetString(reader, "priority"),
                GetNullableString(reader, "zone_name"),
                GetNullableString(reader, "assigned_user_name")));
        }

        return items;
    }

    public async Task<OpportunityDetailDto?> GetOpportunityAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetOpportunityAsync(connection, null, id, cancellationToken);
    }

    public async Task<OpportunityDetailDto?> UpdateAssignmentAsync(long id, OpportunityAssignmentRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string selectSql = """
            select id, estado
            from opportunities
            where id = @id;
            """;

        await using var selectCommand = new NpgsqlCommand(selectSql, connection, transaction);
        selectCommand.Parameters.AddWithValue("id", id);

        string? previousStatus = null;
        await using (var reader = await selectCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            previousStatus = GetNullableString(reader, "estado");
        }

        const string updateSql = """
            update opportunities
            set
                assigned_user_id = @assigned_user_id,
                zone_id = @zone_id,
                estado = coalesce(@estado, estado),
                priority = coalesce(@priority, priority),
                crm_notes = @notes,
                assignment_updated_at = now(),
                vendedor = (
                    select full_name
                    from crm_users
                    where id = @assigned_user_id
                )
            where id = @id;
            """;

        await using var updateCommand = new NpgsqlCommand(updateSql, connection, transaction);
        updateCommand.Parameters.AddWithValue("id", id);
        updateCommand.Parameters.AddWithValue("assigned_user_id", (object?)request.AssignedUserId ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("zone_id", (object?)request.ZoneId ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("estado", (object?)NullIfWhiteSpace(request.Estado) ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("priority", (object?)NullIfWhiteSpace(request.Priority) ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("notes", (object?)NullIfWhiteSpace(request.Notes) ?? DBNull.Value);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        const string historySql = """
            insert into crm_assignment_history (
                opportunity_id,
                assigned_user_id,
                zone_id,
                previous_status,
                new_status,
                notes
            )
            values (
                @opportunity_id,
                @assigned_user_id,
                @zone_id,
                @previous_status,
                @new_status,
                @notes
            );
            """;

        await using var historyCommand = new NpgsqlCommand(historySql, connection, transaction);
        historyCommand.Parameters.AddWithValue("opportunity_id", id);
        historyCommand.Parameters.AddWithValue("assigned_user_id", (object?)request.AssignedUserId ?? DBNull.Value);
        historyCommand.Parameters.AddWithValue("zone_id", (object?)request.ZoneId ?? DBNull.Value);
        historyCommand.Parameters.AddWithValue("previous_status", (object?)previousStatus ?? DBNull.Value);
        historyCommand.Parameters.AddWithValue("new_status", (object?)(NullIfWhiteSpace(request.Estado) ?? previousStatus) ?? DBNull.Value);
        historyCommand.Parameters.AddWithValue("notes", (object?)NullIfWhiteSpace(request.Notes) ?? DBNull.Value);
        await historyCommand.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return await GetOpportunityAsync(id, cancellationToken);
    }

    public async Task<OpportunityDetailDto?> UpdateInvitationAsync(
        long id,
        OpportunityInvitationUpdateRequest request,
        string invitedCompanyName,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var affected = await MarkInvitationAsync(
            connection,
            null,
            id,
            request.IsInvitedMatch,
            request.IsInvitedMatch ? invitedCompanyName : null,
            request.IsInvitedMatch ? NullIfWhiteSpace(request.InvitationSource) : null,
            request.IsInvitedMatch ? NullIfWhiteSpace(request.InvitationEvidenceUrl) : null,
            request.IsInvitedMatch ? NullIfWhiteSpace(request.InvitationNotes) : null,
            request.IsInvitedMatch ? DateTimeOffset.UtcNow : null,
            cancellationToken);

        return affected > 0 ? await GetOpportunityAsync(id, cancellationToken) : null;
    }

    public async Task<BulkInvitationImportResultDto> BulkImportInvitationsAsync(
        BulkInvitationImportRequest request,
        SercopPublicClient sercopPublicClient,
        string invitedCompanyName,
        int fallbackYear,
        CancellationToken cancellationToken)
    {
        var codes = ParseCodes(request.CodesText);
        var updatedCodes = new List<string>();
        var unmatchedCodes = new List<string>();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        foreach (var code in codes)
        {
            try
            {
                var opportunityId = await FindOpportunityIdByCodeAsync(connection, code, cancellationToken);
                if (opportunityId is null)
                {
                    var imported = await sercopPublicClient.ResolveByCodeAsync(code, fallbackYear, cancellationToken);
                    if (imported is null)
                    {
                        unmatchedCodes.Add(code);
                        continue;
                    }

                    await UpsertImportedOpportunityAsync(
                        connection,
                        imported,
                        invitedCompanyName,
                        NullIfWhiteSpace(request.InvitationSource) ?? "manual_import",
                        NullIfWhiteSpace(request.InvitationEvidenceUrl),
                        NullIfWhiteSpace(request.InvitationNotes),
                        cancellationToken);
                }
                else
                {
                    await MarkInvitationAsync(
                        connection,
                        null,
                        opportunityId.Value,
                        true,
                        invitedCompanyName,
                        NullIfWhiteSpace(request.InvitationSource) ?? "manual_import",
                        NullIfWhiteSpace(request.InvitationEvidenceUrl),
                        NullIfWhiteSpace(request.InvitationNotes),
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                }

                updatedCodes.Add(code);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "No se pudo importar invitacion para {Code}", code);
                unmatchedCodes.Add(code);
            }
        }

        return new BulkInvitationImportResultDto(
            codes.Count,
            updatedCodes.Count,
            updatedCodes,
            unmatchedCodes);
    }

    public async Task<InvitationCodeVerificationResultDto> VerifyInvitationCodesAsync(
        InvitationCodeVerificationRequest request,
        SercopPublicClient sercopPublicClient,
        SercopInvitationPublicClient invitationClient,
        string invitedCompanyName,
        string? invitedCompanyRuc,
        int fallbackYear,
        CancellationToken cancellationToken)
    {
        var codes = ParseCodes(request.CodesText);
        var items = new List<InvitationCodeVerificationItemDto>(codes.Count);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        foreach (var code in codes)
        {
            try
            {
                var existing = await FindOpportunityForInvitationVerificationAsync(connection, code, cancellationToken);
                ImportedOpportunityCandidate? imported = null;

                if (existing is null)
                {
                    imported = await sercopPublicClient.ResolveByCodeAsync(code, fallbackYear, cancellationToken);
                    if (imported is null)
                    {
                        items.Add(new InvitationCodeVerificationItemDto(
                            code,
                            false,
                            false,
                            false,
                            null,
                            null,
                            null,
                            null,
                            null,
                            "No se pudo resolver el proceso en SERCOP por codigo."));
                        continue;
                    }
                }

                var processCode = existing?.ProcessCode ?? imported!.ProcessCode;
                var title = existing?.Title ?? imported!.Titulo;
                var entity = existing?.Entity ?? imported!.Entidad;
                var processType = existing?.ProcessType ?? imported!.Tipo;
                var publishedAt = existing?.FechaPublicacion ?? imported!.FechaPublicacion;

                var verification = await invitationClient.VerifyInvitationAsync(
                    processCode,
                    title,
                    entity,
                    processType,
                    invitedCompanyName,
                    invitedCompanyRuc,
                    cancellationToken);

                if (!verification.IsInvited)
                {
                    items.Add(new InvitationCodeVerificationItemDto(
                        code,
                        true,
                        false,
                        false,
                        existing?.Id,
                        verification.PublicProcessCode ?? processCode,
                        publishedAt,
                        verification.MatchedSupplierName,
                        verification.EvidenceUrl,
                        verification.Notes ?? "El reporte publico no confirmo invitacion para la empresa objetivo."));
                    continue;
                }

                long? storedOpportunityId = existing?.Id;
                if (existing is not null)
                {
                    await MarkInvitationAsync(
                        connection,
                        null,
                        existing.Id,
                        true,
                        verification.MatchedSupplierName ?? invitedCompanyName,
                        "reporte_publico_sercop",
                        verification.EvidenceUrl,
                        verification.Notes,
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                }
                else
                {
                    storedOpportunityId = await UpsertImportedOpportunityAsync(
                        connection,
                        imported!,
                        verification.MatchedSupplierName ?? invitedCompanyName,
                        "reporte_publico_sercop",
                        verification.EvidenceUrl,
                        verification.Notes,
                        cancellationToken);
                }

                items.Add(new InvitationCodeVerificationItemDto(
                    code,
                    true,
                    true,
                    true,
                    storedOpportunityId,
                    verification.PublicProcessCode ?? processCode,
                    publishedAt,
                    verification.MatchedSupplierName ?? invitedCompanyName,
                    verification.EvidenceUrl,
                    verification.Notes));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "No se pudo verificar invitacion por codigo para {Code}", code);
                items.Add(new InvitationCodeVerificationItemDto(
                    code,
                    false,
                    false,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    exception.Message));
            }
        }

        return new InvitationCodeVerificationResultDto(
            codes.Count,
            items.Count(item => item.IsInvited),
            items.Count(item => item.StoredInCrm),
            items);
    }

    public async Task<InvitationSyncResultDto> SyncInvitationsFromPublicReportsAsync(
        SercopInvitationPublicClient invitationClient,
        string invitedCompanyName,
        string? invitedCompanyRuc,
        CancellationToken cancellationToken)
    {
        const string candidateSql = """
            with deduped as (
                select distinct on (upper(coalesce(nullif(process_code, ''), ocid_or_nic)))
                    id,
                    process_code,
                    titulo,
                    entidad,
                    tipo,
                    fecha_publicacion,
                    fecha_limite,
                    created_at,
                    upper(coalesce(nullif(process_code, ''), ocid_or_nic)) as process_key
                from opportunities
                where not is_invited_match
                order by
                    upper(coalesce(nullif(process_code, ''), ocid_or_nic)),
                    case
                        when (fecha_publicacion at time zone 'America/Guayaquil')::date >= (now() at time zone 'America/Guayaquil')::date then 0
                        else 1
                    end,
                    fecha_publicacion desc nulls last,
                    fecha_limite asc nulls last,
                    created_at desc,
                    id desc
            )
            select
                id,
                process_code,
                titulo,
                entidad,
                tipo
            from deduped
            order by
                case
                    when (fecha_publicacion at time zone 'America/Guayaquil')::date >= (now() at time zone 'America/Guayaquil')::date then 0
                    else 1
                end,
                fecha_limite asc nulls last,
                fecha_publicacion desc nulls last,
                created_at desc,
                id desc
            limit 120;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(candidateSql, connection);

        var candidates = new List<InvitationSyncCandidate>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new InvitationSyncCandidate(
                    GetInt64(reader, "id"),
                    GetNullableString(reader, "process_code"),
                    GetString(reader, "titulo"),
                    GetNullableString(reader, "entidad"),
                    GetNullableString(reader, "tipo")));
            }
        }

        var confirmedCodes = new List<string>();
        var errors = new List<string>();
        var updatedCount = 0;

        foreach (var candidate in candidates)
        {
            try
            {
                var verification = await invitationClient.VerifyInvitationAsync(
                    candidate.ProcessCode,
                    candidate.Title,
                    candidate.Entity,
                    candidate.ProcessType,
                    invitedCompanyName,
                    invitedCompanyRuc,
                    cancellationToken);

                if (!verification.IsInvited)
                {
                    continue;
                }

                confirmedCodes.Add(verification.PublicProcessCode ?? candidate.ProcessCode ?? candidate.Id.ToString(CultureInfo.InvariantCulture));
                updatedCount += await MarkInvitationAsync(
                    connection,
                    null,
                    candidate.Id,
                    true,
                    verification.MatchedSupplierName ?? invitedCompanyName,
                    "reporte_publico_sercop",
                    verification.EvidenceUrl,
                    verification.Notes,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Error al verificar invitacion publica para oportunidad {OpportunityId}", candidate.Id);
                errors.Add($"{candidate.ProcessCode ?? candidate.Id.ToString(CultureInfo.InvariantCulture)}: {exception.Message}");
            }
        }

        return new InvitationSyncResultDto(
            candidates.Count,
            confirmedCodes.Count,
            updatedCount,
            confirmedCodes,
            errors);
    }

    public async Task<IReadOnlyList<ZoneDto>> GetZonesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select id, name, code, description, active
            from crm_zones
            order by active desc, name asc;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var zones = new List<ZoneDto>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            zones.Add(new ZoneDto(
                GetInt64(reader, "id"),
                GetString(reader, "name"),
                GetString(reader, "code"),
                GetNullableString(reader, "description"),
                GetBoolean(reader, "active")));
        }

        return zones;
    }

    public async Task<ZoneDto> UpsertZoneAsync(long? id, ZoneUpsertRequest request, CancellationToken cancellationToken)
    {
        var sql = id is null
            ? """
                insert into crm_zones (name, code, description, active)
                values (@name, @code, @description, @active)
                returning id, name, code, description, active;
                """
            : """
                update crm_zones
                set
                    name = @name,
                    code = @code,
                    description = @description,
                    active = @active,
                    updated_at = now()
                where id = @id
                returning id, name, code, description, active;
                """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        if (id is not null)
        {
            command.Parameters.AddWithValue("id", id.Value);
        }

        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("code", request.Code.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("description", (object?)NullIfWhiteSpace(request.Description) ?? DBNull.Value);
        command.Parameters.AddWithValue("active", request.Active);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("No se pudo guardar la zona.");
        }

        return new ZoneDto(
            GetInt64(reader, "id"),
            GetString(reader, "name"),
            GetString(reader, "code"),
            GetNullableString(reader, "description"),
            GetBoolean(reader, "active"));
    }

    public async Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select
                u.id,
                u.full_name,
                u.email,
                u.role,
                u.phone,
                u.active,
                u.zone_id,
                z.name as zone_name
            from crm_users u
            left join crm_zones z on z.id = u.zone_id
            order by u.active desc, u.full_name asc;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var users = new List<UserDto>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(new UserDto(
                GetInt64(reader, "id"),
                GetString(reader, "full_name"),
                GetString(reader, "email"),
                GetString(reader, "role"),
                GetNullableString(reader, "phone"),
                GetBoolean(reader, "active"),
                GetNullableInt64(reader, "zone_id"),
                GetNullableString(reader, "zone_name")));
        }

        return users;
    }

    public async Task<UserDto> UpsertUserAsync(long? id, UserUpsertRequest request, CancellationToken cancellationToken)
    {
        var sql = id is null
            ? """
                insert into crm_users (full_name, email, role, phone, active, zone_id)
                values (@full_name, @email, @role, @phone, @active, @zone_id)
                returning id, full_name, email, role, phone, active, zone_id;
                """
            : """
                update crm_users
                set
                    full_name = @full_name,
                    email = @email,
                    role = @role,
                    phone = @phone,
                    active = @active,
                    zone_id = @zone_id,
                    updated_at = now()
                where id = @id
                returning id, full_name, email, role, phone, active, zone_id;
                """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        if (id is not null)
        {
            command.Parameters.AddWithValue("id", id.Value);
        }

        command.Parameters.AddWithValue("full_name", request.FullName.Trim());
        command.Parameters.AddWithValue("email", request.Email.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("role", request.Role.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("phone", (object?)NullIfWhiteSpace(request.Phone) ?? DBNull.Value);
        command.Parameters.AddWithValue("active", request.Active);
        command.Parameters.AddWithValue("zone_id", (object?)request.ZoneId ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("No se pudo guardar el usuario.");
        }

        var zoneId = GetNullableInt64(reader, "zone_id");
        var zoneName = zoneId is null ? null : await LoadZoneNameAsync(connection, zoneId.Value, cancellationToken);

        return new UserDto(
            GetInt64(reader, "id"),
            GetString(reader, "full_name"),
            GetString(reader, "email"),
            GetString(reader, "role"),
            GetNullableString(reader, "phone"),
            GetBoolean(reader, "active"),
            zoneId,
            zoneName);
    }

    public async Task<IReadOnlyList<KeywordRuleDto>> GetKeywordRulesAsync(string? ruleType, string? scope, CancellationToken cancellationToken)
    {
        const string sql = """
            select
                id,
                rule_type,
                scope,
                keyword,
                family,
                weight,
                notes,
                active,
                created_at,
                updated_at
            from keyword_rules
            where (@rule_type is null or rule_type = @rule_type)
              and (@scope is null or scope = @scope)
            order by active desc, updated_at desc, keyword asc;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("rule_type", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)NullIfWhiteSpace(ruleType) ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("scope", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)NullIfWhiteSpace(scope) ?? DBNull.Value });

        var rules = new List<KeywordRuleDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new KeywordRuleDto(
                GetInt64(reader, "id"),
                GetString(reader, "rule_type"),
                GetString(reader, "scope"),
                GetString(reader, "keyword"),
                GetNullableString(reader, "family"),
                GetDecimal(reader, "weight"),
                GetNullableString(reader, "notes"),
                GetBoolean(reader, "active"),
                GetDateTimeOffset(reader, "created_at"),
                GetDateTimeOffset(reader, "updated_at")));
        }

        return rules;
    }

    public async Task<KeywordRuleDto> UpsertKeywordRuleAsync(long? id, KeywordRuleUpsertRequest request, CancellationToken cancellationToken)
    {
        var sql = id is null
            ? """
                insert into keyword_rules (rule_type, scope, keyword, family, weight, notes, active)
                values (@rule_type, @scope, @keyword, @family, @weight, @notes, @active)
                returning id, rule_type, scope, keyword, family, weight, notes, active, created_at, updated_at;
                """
            : """
                update keyword_rules
                set
                    rule_type = @rule_type,
                    scope = @scope,
                    keyword = @keyword,
                    family = @family,
                    weight = @weight,
                    notes = @notes,
                    active = @active,
                    updated_at = now()
                where id = @id
                returning id, rule_type, scope, keyword, family, weight, notes, active, created_at, updated_at;
                """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        if (id is not null)
        {
            command.Parameters.AddWithValue("id", id.Value);
        }

        command.Parameters.AddWithValue("rule_type", request.RuleType.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("scope", request.Scope.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("keyword", request.Keyword.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("family", (object?)NullIfWhiteSpace(request.Family) ?? DBNull.Value);
        command.Parameters.AddWithValue("weight", request.Weight);
        command.Parameters.AddWithValue("notes", (object?)NullIfWhiteSpace(request.Notes) ?? DBNull.Value);
        command.Parameters.AddWithValue("active", request.Active);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("No se pudo guardar la palabra clave.");
        }

        return new KeywordRuleDto(
            GetInt64(reader, "id"),
            GetString(reader, "rule_type"),
            GetString(reader, "scope"),
            GetString(reader, "keyword"),
            GetNullableString(reader, "family"),
            GetDecimal(reader, "weight"),
            GetNullableString(reader, "notes"),
            GetBoolean(reader, "active"),
            GetDateTimeOffset(reader, "created_at"),
            GetDateTimeOffset(reader, "updated_at"));
    }

    public async Task<IReadOnlyList<WorkflowSummaryDto>> GetWorkflowsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select
                id,
                name,
                active,
                description,
                "updatedAt" as updated_at,
                json_array_length(nodes) as node_count
            from workflow_entity
            where not "isArchived"
            order by active desc, name asc;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var workflows = new List<WorkflowSummaryDto>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            workflows.Add(new WorkflowSummaryDto(
                GetString(reader, "id"),
                GetString(reader, "name"),
                GetBoolean(reader, "active"),
                GetNullableString(reader, "description"),
                GetDateTimeOffset(reader, "updated_at"),
                GetInt32(reader, "node_count")));
        }

        return workflows;
    }

    public async Task<WorkflowDetailDto?> GetWorkflowAsync(string id, CancellationToken cancellationToken)
    {
        const string sql = """
            select
                id,
                name,
                active,
                description,
                "updatedAt" as updated_at,
                nodes::text as nodes_json,
                connections::text as connections_json
            from workflow_entity
            where id = @id and not "isArchived";
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var nodesJson = GetString(reader, "nodes_json");
        var connectionsJson = GetString(reader, "connections_json");
        var nodes = ParseWorkflowNodes(nodesJson);

        return new WorkflowDetailDto(
            GetString(reader, "id"),
            GetString(reader, "name"),
            GetBoolean(reader, "active"),
            GetNullableString(reader, "description"),
            GetDateTimeOffset(reader, "updated_at"),
            nodes.Count,
            nodes,
            connectionsJson);
    }

    private async Task<OpportunityDetailDto?> GetOpportunityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long id,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                o.id,
                o.source,
                o.external_id,
                o.ocid_or_nic,
                o.process_code,
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
                o.match_score,
                o.ai_score,
                o.recomendacion,
                o.estado,
                o.vendedor,
                o.resultado,
                o.priority,
                o.crm_notes,
                o.assignment_updated_at,
                z.id as zone_id,
                z.name as zone_name,
                z.code as zone_code,
                u.id as assigned_user_id,
                u.full_name as assigned_user_name,
                u.email as assigned_user_email,
                o.ai_resumen,
                o.ai_riesgos::text as ai_riesgos_json,
                o.ai_checklist::text as ai_checklist_json,
                o.ai_estrategia_abastecimiento,
                o.ai_lista_cotizacion::text as ai_lista_cotizacion_json,
                o.ai_preguntas_abiertas::text as ai_preguntas_abiertas_json,
                o.raw_payload::text as raw_payload_json
            from opportunities o
            left join crm_zones z on z.id = o.zone_id
            left join crm_users u on u.id = o.assigned_user_id
            where o.id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var detail = new OpportunityDetailDto(
            GetInt64(reader, "id"),
            GetString(reader, "source"),
            GetString(reader, "external_id"),
            GetString(reader, "ocid_or_nic"),
            GetNullableString(reader, "process_code") ?? GetString(reader, "ocid_or_nic"),
            GetString(reader, "titulo"),
            GetNullableString(reader, "entidad"),
            GetNullableString(reader, "tipo"),
            GetNullableDateTimeOffset(reader, "fecha_publicacion"),
            GetNullableDateTimeOffset(reader, "fecha_limite"),
            GetNullableDecimal(reader, "monto_ref"),
            GetString(reader, "moneda"),
            GetString(reader, "url"),
            GetNullableString(reader, "invited_company_name"),
            GetBoolean(reader, "is_invited_match"),
            GetNullableString(reader, "invitation_source"),
            GetNullableString(reader, "invitation_notes"),
            GetNullableString(reader, "invitation_evidence_url"),
            GetNullableDateTimeOffset(reader, "invitation_verified_at"),
            GetDecimal(reader, "match_score"),
            GetDecimal(reader, "ai_score"),
            GetNullableString(reader, "recomendacion"),
            GetNullableString(reader, "estado"),
            GetNullableString(reader, "vendedor"),
            GetNullableString(reader, "resultado"),
            GetString(reader, "priority"),
            GetNullableString(reader, "crm_notes"),
            GetNullableDateTimeOffset(reader, "assignment_updated_at"),
            GetNullableInt64(reader, "zone_id"),
            GetNullableString(reader, "zone_name"),
            GetNullableString(reader, "zone_code"),
            GetNullableInt64(reader, "assigned_user_id"),
            GetNullableString(reader, "assigned_user_name"),
            GetNullableString(reader, "assigned_user_email"),
            GetNullableString(reader, "ai_resumen"),
            GetString(reader, "ai_riesgos_json"),
            GetString(reader, "ai_checklist_json"),
            GetNullableString(reader, "ai_estrategia_abastecimiento"),
            GetString(reader, "ai_lista_cotizacion_json"),
            GetString(reader, "ai_preguntas_abiertas_json"),
            GetString(reader, "raw_payload_json"),
            [],
            []);

        await reader.CloseAsync();
        var documents = await LoadDocumentsAsync(connection, transaction, id, cancellationToken);
        var history = await LoadAssignmentHistoryAsync(connection, transaction, id, cancellationToken);

        return detail with
        {
            Documents = documents,
            AssignmentHistory = history
        };
    }

    private static async Task<(int Total, int Invited, int Assigned, int Unassigned)> LoadOpportunityCountersAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                count(*) as total,
                count(*) filter (where is_invited_match) as invited,
                count(*) filter (where assigned_user_id is not null) as assigned,
                count(*) filter (where assigned_user_id is null) as unassigned
            from opportunities
            where (fecha_publicacion at time zone 'America/Guayaquil')::date >= (now() at time zone 'America/Guayaquil')::date;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return (
            GetInt32(reader, "total"),
            GetInt32(reader, "invited"),
            GetInt32(reader, "assigned"),
            GetInt32(reader, "unassigned"));
    }

    private static async Task<IReadOnlyList<DashboardMetricDto>> LoadDashboardStatusesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select coalesce(estado, 'sin_estado') as label, count(*) as count
            from opportunities
            where (fecha_publicacion at time zone 'America/Guayaquil')::date >= (now() at time zone 'America/Guayaquil')::date
            group by coalesce(estado, 'sin_estado')
            order by count(*) desc, label asc;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        var statuses = new List<DashboardMetricDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            statuses.Add(new DashboardMetricDto(GetString(reader, "label"), GetInt32(reader, "count")));
        }

        return statuses;
    }

    private static async Task<IReadOnlyList<ZoneLoadDto>> LoadZoneLoadsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            select
                z.id as zone_id,
                z.name as zone_name,
                count(o.id) as count
            from crm_zones z
            left join opportunities o
                on o.zone_id = z.id
               and (o.fecha_publicacion at time zone 'America/Guayaquil')::date >= (now() at time zone 'America/Guayaquil')::date
            group by z.id, z.name
            order by z.name asc;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        var zoneLoads = new List<ZoneLoadDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            zoneLoads.Add(new ZoneLoadDto(
                GetNullableInt64(reader, "zone_id"),
                GetString(reader, "zone_name"),
                GetInt32(reader, "count")));
        }

        return zoneLoads;
    }

    private static async Task<int> ExecuteCountAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result ?? 0, CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<OpportunityDocumentDto>> LoadDocumentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long opportunityId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                id,
                source_url,
                local_path,
                mime_type,
                sha256,
                chunk_count,
                created_at
            from documents
            where opportunity_id = @opportunity_id
            order by created_at desc, id desc;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("opportunity_id", opportunityId);

        var documents = new List<OpportunityDocumentDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(new OpportunityDocumentDto(
                GetInt64(reader, "id"),
                GetString(reader, "source_url"),
                GetNullableString(reader, "local_path"),
                GetNullableString(reader, "mime_type"),
                GetNullableString(reader, "sha256"),
                GetInt32(reader, "chunk_count"),
                GetDateTimeOffset(reader, "created_at")));
        }

        return documents;
    }

    private static async Task<IReadOnlyList<AssignmentHistoryItemDto>> LoadAssignmentHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long opportunityId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                h.id,
                h.assigned_user_id,
                u.full_name as assigned_user_name,
                h.zone_id,
                z.name as zone_name,
                h.previous_status,
                h.new_status,
                h.notes,
                h.changed_at
            from crm_assignment_history h
            left join crm_users u on u.id = h.assigned_user_id
            left join crm_zones z on z.id = h.zone_id
            where h.opportunity_id = @opportunity_id
            order by h.changed_at desc, h.id desc;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("opportunity_id", opportunityId);

        var history = new List<AssignmentHistoryItemDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            history.Add(new AssignmentHistoryItemDto(
                GetInt64(reader, "id"),
                GetNullableInt64(reader, "assigned_user_id"),
                GetNullableString(reader, "assigned_user_name"),
                GetNullableInt64(reader, "zone_id"),
                GetNullableString(reader, "zone_name"),
                GetNullableString(reader, "previous_status"),
                GetNullableString(reader, "new_status"),
                GetNullableString(reader, "notes"),
                GetDateTimeOffset(reader, "changed_at")));
        }

        return history;
    }

    private async Task<long?> FindOpportunityIdByCodeAsync(NpgsqlConnection connection, string code, CancellationToken cancellationToken)
    {
        const string sql = """
            select id
            from opportunities
            where upper(coalesce(process_code, '')) = upper(@code)
               or upper(ocid_or_nic) = upper(@code)
               or upper(external_id) = upper(@code)
            order by id desc
            limit 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("code", code);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private async Task<OpportunityInvitationLookup?> FindOpportunityForInvitationVerificationAsync(
        NpgsqlConnection connection,
        string code,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                id,
                process_code,
                titulo,
                entidad,
                tipo,
                fecha_publicacion
            from opportunities
            where upper(coalesce(process_code, '')) = upper(@code)
               or upper(ocid_or_nic) = upper(@code)
               or upper(external_id) = upper(@code)
            order by fecha_publicacion desc nulls last, id desc
            limit 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("code", code);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new OpportunityInvitationLookup(
            GetInt64(reader, "id"),
            GetString(reader, "process_code"),
            GetString(reader, "titulo"),
            GetNullableString(reader, "entidad"),
            GetNullableString(reader, "tipo"),
            GetNullableDateTimeOffset(reader, "fecha_publicacion"));
    }

    private async Task<long> UpsertImportedOpportunityAsync(
        NpgsqlConnection connection,
        ImportedOpportunityCandidate candidate,
        string invitedCompanyName,
        string invitationSource,
        string? invitationEvidenceUrl,
        string? invitationNotes,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into opportunities (
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
                invited_company_name,
                is_invited_match,
                invitation_source,
                invitation_notes,
                invitation_evidence_url,
                invitation_verified_at,
                raw_payload
            )
            values (
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
                @invited_company_name,
                true,
                @invitation_source,
                @invitation_notes,
                @invitation_evidence_url,
                now(),
                @raw_payload::jsonb
            )
            on conflict (source, external_id)
            do update set
                ocid_or_nic = excluded.ocid_or_nic,
                process_code = excluded.process_code,
                titulo = excluded.titulo,
                entidad = excluded.entidad,
                tipo = excluded.tipo,
                fecha_publicacion = coalesce(excluded.fecha_publicacion, opportunities.fecha_publicacion),
                fecha_limite = coalesce(excluded.fecha_limite, opportunities.fecha_limite),
                monto_ref = coalesce(excluded.monto_ref, opportunities.monto_ref),
                moneda = coalesce(nullif(excluded.moneda, ''), opportunities.moneda),
                url = excluded.url,
                invited_company_name = excluded.invited_company_name,
                is_invited_match = true,
                invitation_source = excluded.invitation_source,
                invitation_notes = excluded.invitation_notes,
                invitation_evidence_url = excluded.invitation_evidence_url,
                invitation_verified_at = excluded.invitation_verified_at,
                raw_payload = excluded.raw_payload,
                updated_at = now()
            returning id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("source", candidate.Source);
        command.Parameters.AddWithValue("external_id", candidate.ExternalId);
        command.Parameters.AddWithValue("ocid_or_nic", candidate.OcidOrNic);
        command.Parameters.AddWithValue("process_code", candidate.ProcessCode);
        command.Parameters.AddWithValue("titulo", candidate.Titulo);
        command.Parameters.AddWithValue("entidad", (object?)candidate.Entidad ?? DBNull.Value);
        command.Parameters.AddWithValue("tipo", (object?)candidate.Tipo ?? DBNull.Value);
        command.Parameters.AddWithValue("fecha_publicacion", (object?)candidate.FechaPublicacion ?? DBNull.Value);
        command.Parameters.AddWithValue("fecha_limite", (object?)candidate.FechaLimite ?? DBNull.Value);
        command.Parameters.AddWithValue("monto_ref", (object?)candidate.MontoRef ?? DBNull.Value);
        command.Parameters.AddWithValue("moneda", candidate.Moneda);
        command.Parameters.AddWithValue("url", candidate.Url);
        command.Parameters.AddWithValue("invited_company_name", invitedCompanyName);
        command.Parameters.AddWithValue("invitation_source", invitationSource);
        command.Parameters.AddWithValue("invitation_notes", (object?)invitationNotes ?? DBNull.Value);
        command.Parameters.AddWithValue("invitation_evidence_url", (object?)invitationEvidenceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("raw_payload", candidate.RawPayloadJson);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<int> MarkInvitationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long opportunityId,
        bool isInvitedMatch,
        string? invitedCompanyName,
        string? invitationSource,
        string? invitationEvidenceUrl,
        string? invitationNotes,
        DateTimeOffset? invitationVerifiedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update opportunities
            set
                invited_company_name = @invited_company_name,
                is_invited_match = @is_invited_match,
                invitation_source = @invitation_source,
                invitation_evidence_url = @invitation_evidence_url,
                invitation_notes = @invitation_notes,
                invitation_verified_at = @invitation_verified_at
            where id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", opportunityId);
        command.Parameters.AddWithValue("invited_company_name", (object?)invitedCompanyName ?? DBNull.Value);
        command.Parameters.AddWithValue("is_invited_match", isInvitedMatch);
        command.Parameters.AddWithValue("invitation_source", (object?)invitationSource ?? DBNull.Value);
        command.Parameters.AddWithValue("invitation_evidence_url", (object?)invitationEvidenceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("invitation_notes", (object?)invitationNotes ?? DBNull.Value);
        command.Parameters.AddWithValue("invitation_verified_at", (object?)invitationVerifiedAt ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> LoadZoneNameAsync(NpgsqlConnection connection, long zoneId, CancellationToken cancellationToken)
    {
        const string sql = "select name from crm_zones where id = @id;";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", zoneId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static List<WorkflowNodeDto> ParseWorkflowNodes(string nodesJson)
    {
        var projections = JsonSerializer.Deserialize<List<WorkflowNodeProjection>>(nodesJson, JsonOptions) ?? [];
        var nodes = new List<WorkflowNodeDto>(projections.Count);

        foreach (var projection in projections)
        {
            var x = projection.position is { Length: > 0 } ? projection.position[0] : 0;
            var y = projection.position is { Length: > 1 } ? projection.position[1] : 0;

            nodes.Add(new WorkflowNodeDto(
                projection.id ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                projection.name ?? "Nodo",
                projection.type ?? "unknown",
                x,
                y,
                projection.disabled));
        }

        return nodes;
    }

    private static List<string> ParseCodes(string rawCodes)
        => rawCodes
            .Split(['\r', '\n', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(code => code.Trim())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string GetString(NpgsqlDataReader reader, string name)
        => GetNullableString(reader, name) ?? string.Empty;

    private static string? GetNullableString(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static bool GetBoolean(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal);
    }

    private static int GetInt32(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static long GetInt64(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static long? GetNullableInt64(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static decimal GetDecimal(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static decimal? GetNullableDecimal(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset GetDateTimeOffset(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? DateTimeOffset.MinValue : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }

    private static DateTimeOffset? GetNullableDateTimeOffset(NpgsqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }

    private sealed record InvitationSyncCandidate(long Id, string? ProcessCode, string Title, string? Entity, string? ProcessType);

    private sealed record OpportunityInvitationLookup(
        long Id,
        string ProcessCode,
        string Title,
        string? Entity,
        string? ProcessType,
        DateTimeOffset? FechaPublicacion);

    private sealed class WorkflowNodeProjection
    {
        public string? id { get; set; }

        public string? name { get; set; }

        public string? type { get; set; }

        public double[]? position { get; set; }

        public bool disabled { get; set; }
    }
}
