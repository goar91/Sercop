using Npgsql;
using NpgsqlTypes;

namespace backend;

public sealed partial class CrmRepository
{
    public async Task<KeywordRefreshRunDto> CreateKeywordRefreshRunAsync(
        string triggerType,
        long? keywordRuleId,
        long? initiatedByUserId,
        string? initiatedByLoginName,
        int requestedWindowDays,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO crm_keyword_refresh_runs (
              trigger_type,
              status,
              keyword_rule_id,
              initiated_by_user_id,
              initiated_by_login_name,
              requested_window_days
            )
            VALUES (
              @trigger_type,
              'pending',
              @keyword_rule_id,
              @initiated_by_user_id,
              @initiated_by_login_name,
              @requested_window_days
            )
            RETURNING id, trigger_type, status, keyword_rule_id, initiated_by_user_id, initiated_by_login_name, requested_window_days, created_at, started_at, finished_at, reevaluated_count, captured_count, error_count, error_message;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("trigger_type", NormalizeNullableText(triggerType) ?? "manual");
        AddNullableInt64(command.Parameters, "keyword_rule_id", keywordRuleId);
        AddNullableInt64(command.Parameters, "initiated_by_user_id", initiatedByUserId);
        AddNullableText(command.Parameters, "initiated_by_login_name", NormalizeNullableText(initiatedByLoginName));
        command.Parameters.AddWithValue("requested_window_days", Math.Clamp(requestedWindowDays, 1, 30));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadKeywordRefreshRun(reader);
    }

    public async Task<KeywordRefreshRunDto?> GetLatestKeywordRefreshRunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, trigger_type, status, keyword_rule_id, initiated_by_user_id, initiated_by_login_name, requested_window_days, created_at, started_at, finished_at, reevaluated_count, captured_count, error_count, error_message
            FROM crm_keyword_refresh_runs
            ORDER BY created_at DESC, id DESC
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadKeywordRefreshRun(reader) : null;
    }

    internal async Task<IReadOnlyList<long>> GetQueuedKeywordRefreshRunIdsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id
            FROM crm_keyword_refresh_runs
            WHERE status IN ('pending', 'running')
            ORDER BY created_at ASC, id ASC;
            """;

        var ids = new List<long>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    internal async Task<KeywordRefreshRunExecutionContext?> MarkKeywordRefreshRunRunningAsync(long runId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE crm_keyword_refresh_runs
            SET status = 'running',
                started_at = COALESCE(started_at, NOW()),
                finished_at = NULL,
                error_message = NULL,
                updated_at = NOW()
            WHERE id = @id
              AND status IN ('pending', 'running')
            RETURNING id, trigger_type, requested_window_days, started_at;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new KeywordRefreshRunExecutionContext(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetFieldValue<DateTimeOffset>(3));
    }

    internal async Task<int> ReevaluateCurrentOpportunitiesAsync(int requestedWindowDays, CancellationToken cancellationToken)
    {
        var cutoff = EcuadorTime.Now().AddDays(-Math.Clamp(requestedWindowDays, 1, 30)).ToUniversalTime();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var keywordRules = await LoadKeywordRuleSnapshotAsync(connection, cancellationToken);

        const string loadSql = """
            SELECT id, source, COALESCE(process_code, ocid_or_nic) AS process_code, titulo, entidad, tipo, capture_scope, COALESCE(raw_payload::text, '') AS raw_payload_text
            FROM opportunities
            WHERE COALESCE(resultado, '') NOT IN ('ganado', 'perdido', 'no_presentado')
               OR COALESCE(fecha_publicacion, created_at) >= @cutoff
            ORDER BY id ASC;
            """;

        var rows = new List<KeywordRefreshOpportunityRow>();
        await using (var loadCommand = new NpgsqlCommand(loadSql, connection))
        {
            loadCommand.Parameters.AddWithValue("cutoff", cutoff.UtcDateTime);
            await using var reader = await loadCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new KeywordRefreshOpportunityRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    GetNullableString(reader, 4),
                    GetNullableString(reader, 5),
                    GetNullableString(reader, 6),
                    reader.GetString(7)));
            }
        }

        const string updateSql = """
            UPDATE opportunities
            SET keywords_hit = @keywords_hit,
                process_category = @process_category,
                capture_scope = @capture_scope,
                is_chemistry_candidate = @is_chemistry_candidate,
                classification_payload = @classification_payload::jsonb,
                match_score = @match_score,
                recomendacion = @recomendacion,
                updated_at = CASE
                  WHEN keywords_hit IS DISTINCT FROM @keywords_hit
                    OR process_category IS DISTINCT FROM @process_category
                    OR capture_scope IS DISTINCT FROM @capture_scope
                    OR is_chemistry_candidate IS DISTINCT FROM @is_chemistry_candidate
                    OR classification_payload IS DISTINCT FROM @classification_payload::jsonb
                    OR match_score IS DISTINCT FROM @match_score
                    OR recomendacion IS DISTINCT FROM @recomendacion
                  THEN NOW()
                  ELSE updated_at
                END
            WHERE id = @id;
            """;

        await using var updateCommand = new NpgsqlCommand(updateSql, connection);
        updateCommand.Parameters.Add("keywords_hit", NpgsqlDbType.Array | NpgsqlDbType.Text);
        updateCommand.Parameters.Add("process_category", NpgsqlDbType.Text);
        updateCommand.Parameters.Add("capture_scope", NpgsqlDbType.Text);
        updateCommand.Parameters.Add("is_chemistry_candidate", NpgsqlDbType.Boolean);
        updateCommand.Parameters.Add("classification_payload", NpgsqlDbType.Jsonb);
        updateCommand.Parameters.Add("match_score", NpgsqlDbType.Numeric);
        updateCommand.Parameters.Add("recomendacion", NpgsqlDbType.Text);
        updateCommand.Parameters.Add("id", NpgsqlDbType.Bigint);

        foreach (var row in rows)
        {
            var classification = BuildClassificationSnapshot(row.Source, row.ProcessCode, row.Tipo, row.Titulo, row.Entidad, row.RawPayloadText, row.CaptureScope, keywordRules);
            updateCommand.Parameters["keywords_hit"].Value = classification.KeywordsHit.ToArray();
            updateCommand.Parameters["process_category"].Value = classification.ProcessCategory;
            updateCommand.Parameters["capture_scope"].Value = classification.CaptureScope;
            updateCommand.Parameters["is_chemistry_candidate"].Value = classification.IsChemistryCandidate;
            updateCommand.Parameters["classification_payload"].Value = classification.PayloadJson;
            updateCommand.Parameters["match_score"].Value = classification.MatchScore;
            updateCommand.Parameters["recomendacion"].Value = classification.Recommendation;
            updateCommand.Parameters["id"].Value = row.Id;
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return rows.Count;
    }

    internal async Task<int> CountNewOpportunitiesCapturedSinceAsync(DateTimeOffset startedAt, int requestedWindowDays, CancellationToken cancellationToken)
    {
        var cutoff = EcuadorTime.Now().AddDays(-Math.Clamp(requestedWindowDays, 1, 30)).ToUniversalTime();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT COUNT(*)::int
            FROM opportunities
            WHERE source IN ('ocds', 'nco')
              AND created_at >= @started_at
              AND COALESCE(fecha_publicacion, created_at) >= @cutoff;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("started_at", startedAt.ToUniversalTime().UtcDateTime);
        command.Parameters.AddWithValue("cutoff", cutoff.UtcDateTime);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    internal async Task CompleteKeywordRefreshRunAsync(
        long runId,
        int reevaluatedCount,
        int capturedCount,
        int errorCount,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE crm_keyword_refresh_runs
            SET status = 'completed',
                finished_at = NOW(),
                reevaluated_count = @reevaluated_count,
                captured_count = @captured_count,
                error_count = @error_count,
                error_message = @error_message,
                updated_at = NOW()
            WHERE id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", runId);
        command.Parameters.AddWithValue("reevaluated_count", reevaluatedCount);
        command.Parameters.AddWithValue("captured_count", capturedCount);
        command.Parameters.AddWithValue("error_count", errorCount);
        AddNullableText(command.Parameters, "error_message", NormalizeNullableText(errorMessage));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task FailKeywordRefreshRunAsync(
        long runId,
        int reevaluatedCount,
        int capturedCount,
        int errorCount,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE crm_keyword_refresh_runs
            SET status = 'error',
                finished_at = NOW(),
                reevaluated_count = @reevaluated_count,
                captured_count = @captured_count,
                error_count = @error_count,
                error_message = @error_message,
                updated_at = NOW()
            WHERE id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", runId);
        command.Parameters.AddWithValue("reevaluated_count", reevaluatedCount);
        command.Parameters.AddWithValue("captured_count", capturedCount);
        command.Parameters.AddWithValue("error_count", errorCount);
        AddNullableText(command.Parameters, "error_message", NormalizeNullableText(errorMessage));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static KeywordRefreshRunDto ReadKeywordRefreshRun(NpgsqlDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            GetNullableInt64(reader, 3),
            GetNullableInt64(reader, 4),
            GetNullableString(reader, 5),
            reader.GetInt32(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            GetNullableDateTimeOffset(reader, 8),
            GetNullableDateTimeOffset(reader, 9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            GetNullableString(reader, 13));

    internal sealed record KeywordRefreshRunExecutionContext(
        long RunId,
        string TriggerType,
        int RequestedWindowDays,
        DateTimeOffset StartedAt);

    private sealed record KeywordRefreshOpportunityRow(
        long Id,
        string Source,
        string ProcessCode,
        string Titulo,
        string? Entidad,
        string? Tipo,
        string? CaptureScope,
        string RawPayloadText);
}
