using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace backend;

public sealed partial class CrmRepository
{
    public async Task<long> CreatePersonalAssistantSessionAsync(string title, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into personal_ai_sessions (title)
            values (@title)
            returning id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("title", title.Trim());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<PersonalAssistantSessionDto>> GetPersonalAssistantSessionsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select
                s.id,
                s.title,
                s.created_at,
                s.updated_at,
                count(m.id)::int as message_count
            from personal_ai_sessions s
            left join personal_ai_messages m on m.session_id = s.id
            group by s.id, s.title, s.created_at, s.updated_at
            order by s.updated_at desc, s.id desc
            limit 40;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        var sessions = new List<PersonalAssistantSessionDto>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new PersonalAssistantSessionDto(
                GetInt64(reader, "id"),
                GetString(reader, "title"),
                GetDateTimeOffset(reader, "created_at"),
                GetDateTimeOffset(reader, "updated_at"),
                GetInt32(reader, "message_count")));
        }

        return sessions;
    }

    public async Task<PersonalAssistantSessionDetailDto?> GetPersonalAssistantSessionAsync(long sessionId, CancellationToken cancellationToken)
    {
        const string sessionSql = """
            select id, title, created_at, updated_at
            from personal_ai_sessions
            where id = @id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var sessionCommand = new NpgsqlCommand(sessionSql, connection);
        sessionCommand.Parameters.AddWithValue("id", sessionId);

        await using var sessionReader = await sessionCommand.ExecuteReaderAsync(cancellationToken);
        if (!await sessionReader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var title = GetString(sessionReader, "title");
        var createdAt = GetDateTimeOffset(sessionReader, "created_at");
        var updatedAt = GetDateTimeOffset(sessionReader, "updated_at");
        await sessionReader.CloseAsync();

        var messages = await GetRecentPersonalAssistantMessagesAsync(connection, null, sessionId, 200, cancellationToken);
        return new PersonalAssistantSessionDetailDto(sessionId, title, createdAt, updatedAt, messages.OrderBy(message => message.Id).ToList());
    }

    public async Task<IReadOnlyList<PersonalAssistantMessageDto>> GetRecentPersonalAssistantMessagesAsync(
        long sessionId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await GetRecentPersonalAssistantMessagesAsync(connection, null, sessionId, limit, cancellationToken);
    }

    public async Task AddPersonalAssistantMessageAsync(
        long sessionId,
        string role,
        string content,
        string? model,
        string contextJson,
        IReadOnlyList<AssistantSourceDto> sources,
        CancellationToken cancellationToken)
    {
        const string insertSql = """
            insert into personal_ai_messages (
                session_id,
                role,
                content,
                model,
                context_json,
                sources_json
            )
            values (
                @session_id,
                @role,
                @content,
                @model,
                @context_json::jsonb,
                @sources_json::jsonb
            );
            """;

        const string touchSessionSql = """
            update personal_ai_sessions
            set updated_at = now()
            where id = @session_id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var insertCommand = new NpgsqlCommand(insertSql, connection, transaction))
        {
            insertCommand.Parameters.AddWithValue("session_id", sessionId);
            insertCommand.Parameters.AddWithValue("role", role);
            insertCommand.Parameters.AddWithValue("content", content.Trim());
            insertCommand.Parameters.AddWithValue("model", (object?)NullIfWhiteSpace(model) ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("context_json", string.IsNullOrWhiteSpace(contextJson) ? "{}" : contextJson);
            insertCommand.Parameters.AddWithValue("sources_json", SerializeSources(sources));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var touchCommand = new NpgsqlCommand(touchSessionSql, connection, transaction))
        {
            touchCommand.Parameters.AddWithValue("session_id", sessionId);
            await touchCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersonalAssistantMemoryDto>> SearchPersonalAssistantMemoryAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                id,
                memory_kind,
                title,
                content,
                source_kind,
                source_url,
                confidence,
                created_at,
                last_used_at,
                coalesce(sources_json::text, '[]') as sources_json,
                ts_rank_cd(
                    to_tsvector('simple', coalesce(title, '') || ' ' || coalesce(content, '')),
                    plainto_tsquery('simple', @query)
                ) as rank
            from personal_ai_memory
            where
                title ilike '%' || @query || '%'
                or content ilike '%' || @query || '%'
                or to_tsvector('simple', coalesce(title, '') || ' ' || coalesce(content, '')) @@ plainto_tsquery('simple', @query)
            order by rank desc, coalesce(last_used_at, updated_at, created_at) desc, id desc
            limit @limit;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("query", query.Trim());
        command.Parameters.AddWithValue("limit", limit);

        var items = new List<PersonalAssistantMemoryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PersonalAssistantMemoryDto(
                GetInt64(reader, "id"),
                GetString(reader, "memory_kind"),
                GetString(reader, "title"),
                GetString(reader, "content"),
                GetString(reader, "source_kind"),
                GetNullableString(reader, "source_url"),
                GetDecimal(reader, "confidence"),
                GetDateTimeOffset(reader, "created_at"),
                GetNullableDateTimeOffset(reader, "last_used_at"),
                ParseSources(GetString(reader, "sources_json"))));
        }

        return items;
    }

    public async Task<IReadOnlyList<PersonalAssistantMemoryDto>> ListPersonalAssistantMemoryAsync(
        string? search,
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                id,
                memory_kind,
                title,
                content,
                source_kind,
                source_url,
                confidence,
                created_at,
                last_used_at,
                coalesce(sources_json::text, '[]') as sources_json
            from personal_ai_memory
            where
                (@search is null
                    or title ilike '%' || @search || '%'
                    or content ilike '%' || @search || '%'
                    or coalesce(source_url, '') ilike '%' || @search || '%')
            order by coalesce(last_used_at, updated_at, created_at) desc, id desc
            limit @limit;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("search", NpgsqlDbType.Text) { Value = (object?)NullIfWhiteSpace(search) ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);

        var items = new List<PersonalAssistantMemoryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PersonalAssistantMemoryDto(
                GetInt64(reader, "id"),
                GetString(reader, "memory_kind"),
                GetString(reader, "title"),
                GetString(reader, "content"),
                GetString(reader, "source_kind"),
                GetNullableString(reader, "source_url"),
                GetDecimal(reader, "confidence"),
                GetDateTimeOffset(reader, "created_at"),
                GetNullableDateTimeOffset(reader, "last_used_at"),
                ParseSources(GetString(reader, "sources_json"))));
        }

        return items;
    }

    public async Task<long> UpsertPersonalAssistantMemoryAsync(
        PersonalAssistantMemoryUpsert memory,
        CancellationToken cancellationToken)
    {
        var usesWebUpsert = string.Equals(memory.SourceKind, "web", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(memory.SourceUrl);

        var sql = usesWebUpsert
            ? """
                insert into personal_ai_memory (
                    memory_kind,
                    title,
                    content,
                    source_kind,
                    source_url,
                    sources_json,
                    confidence,
                    learned_from_query,
                    last_used_at
                )
                values (
                    @memory_kind,
                    @title,
                    @content,
                    @source_kind,
                    @source_url,
                    @sources_json::jsonb,
                    @confidence,
                    @learned_from_query,
                    now()
                )
                on conflict (source_url)
                where source_kind = 'web' and source_url is not null
                do update set
                    title = excluded.title,
                    content = excluded.content,
                    sources_json = excluded.sources_json,
                    confidence = excluded.confidence,
                    learned_from_query = excluded.learned_from_query,
                    last_used_at = now(),
                    updated_at = now()
                returning id;
                """
            : """
                insert into personal_ai_memory (
                    memory_kind,
                    title,
                    content,
                    source_kind,
                    source_url,
                    sources_json,
                    confidence,
                    learned_from_query,
                    last_used_at
                )
                values (
                    @memory_kind,
                    @title,
                    @content,
                    @source_kind,
                    @source_url,
                    @sources_json::jsonb,
                    @confidence,
                    @learned_from_query,
                    now()
                )
                returning id;
                """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("memory_kind", memory.MemoryKind);
        command.Parameters.AddWithValue("title", memory.Title.Trim());
        command.Parameters.AddWithValue("content", memory.Content.Trim());
        command.Parameters.AddWithValue("source_kind", memory.SourceKind.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("source_url", (object?)NullIfWhiteSpace(memory.SourceUrl) ?? DBNull.Value);
        command.Parameters.AddWithValue("sources_json", SerializeSources(memory.Sources));
        command.Parameters.AddWithValue("confidence", memory.Confidence);
        command.Parameters.AddWithValue("learned_from_query", (object?)NullIfWhiteSpace(memory.LearnedFromQuery) ?? DBNull.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task TouchPersonalAssistantMemoryAsync(IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var memoryIds = ids.Distinct().Where(id => id > 0).ToArray();
        if (memoryIds.Length == 0)
        {
            return;
        }

        const string sql = """
            update personal_ai_memory
            set
                last_used_at = now(),
                updated_at = now()
            where id = any(@ids);
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<long[]>("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { TypedValue = memoryIds });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<PersonalAssistantMessageDto>> GetRecentPersonalAssistantMessagesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        long sessionId,
        int limit,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                id,
                role,
                content,
                model,
                created_at,
                coalesce(sources_json::text, '[]') as sources_json
            from personal_ai_messages
            where session_id = @session_id
            order by id desc
            limit @limit;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.AddWithValue("limit", limit);

        var items = new List<PersonalAssistantMessageDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PersonalAssistantMessageDto(
                GetInt64(reader, "id"),
                GetString(reader, "role"),
                GetString(reader, "content"),
                GetNullableString(reader, "model"),
                GetDateTimeOffset(reader, "created_at"),
                ParseSources(GetString(reader, "sources_json"))));
        }

        return items;
    }

    private static IReadOnlyList<AssistantSourceDto> ParseSources(string rawJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<AssistantSourceDto>>(rawJson, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string SerializeSources(IReadOnlyList<AssistantSourceDto> sources)
        => JsonSerializer.Serialize(sources ?? [], JsonOptions);
}

public sealed record PersonalAssistantMemoryUpsert(
    string MemoryKind,
    string Title,
    string Content,
    string SourceKind,
    string? SourceUrl,
    decimal Confidence,
    string? LearnedFromQuery,
    IReadOnlyList<AssistantSourceDto> Sources
);
