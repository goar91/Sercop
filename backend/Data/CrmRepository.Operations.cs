using Npgsql;

namespace backend;

public sealed partial class CrmRepository
{
    public async Task<PagedResultDto<UserDto>> SearchUsersAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        page = NormalizePage(page);
        pageSize = NormalizePageSize(pageSize);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string countSql = "SELECT COUNT(*)::int FROM crm_users;";
        await using var countCommand = new NpgsqlCommand(countSql, connection);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT u.id, u.login_name, u.full_name, u.email, u.role, u.phone, u.active, u.zone_id, z.name, u.must_change_password, u.last_login_at
            FROM crm_users u
            LEFT JOIN crm_zones z ON z.id = u.zone_id
            ORDER BY u.active DESC, u.full_name ASC
            OFFSET @offset ROWS FETCH NEXT @page_size ROWS ONLY;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        command.Parameters.AddWithValue("page_size", pageSize);

        var items = new List<UserDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new UserDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                GetNullableString(reader, 5),
                reader.GetBoolean(6),
                GetNullableInt64(reader, 7),
                GetNullableString(reader, 8),
                reader.GetBoolean(9),
                GetNullableDateTimeOffset(reader, 10)));
        }

        return new PagedResultDto<UserDto>(items, totalCount, page, pageSize);
    }

    public async Task<PagedResultDto<KeywordRuleDto>> SearchKeywordRulesAsync(
        string? ruleType,
        string? scope,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = NormalizePage(page);
        pageSize = NormalizePageSize(pageSize);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string countSql = """
            SELECT COUNT(*)::int
            FROM keyword_rules
            WHERE (@rule_type = '' OR rule_type = @rule_type)
              AND (@scope = '' OR scope = @scope);
            """;

        await using var countCommand = new NpgsqlCommand(countSql, connection);
        countCommand.Parameters.AddWithValue("rule_type", NormalizeNullableText(ruleType) ?? string.Empty);
        countCommand.Parameters.AddWithValue("scope", NormalizeNullableText(scope) ?? string.Empty);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT id, rule_type, scope, keyword, family, weight, notes, active, created_at, updated_at
            FROM keyword_rules
            WHERE (@rule_type = '' OR rule_type = @rule_type)
              AND (@scope = '' OR scope = @scope)
            ORDER BY active DESC, updated_at DESC, keyword ASC
            OFFSET @offset ROWS FETCH NEXT @page_size ROWS ONLY;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("rule_type", NormalizeNullableText(ruleType) ?? string.Empty);
        command.Parameters.AddWithValue("scope", NormalizeNullableText(scope) ?? string.Empty);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        command.Parameters.AddWithValue("page_size", pageSize);

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

        return new PagedResultDto<KeywordRuleDto>(items, totalCount, page, pageSize);
    }

    public async Task<PagedResultDto<WorkflowSummaryDto>> SearchWorkflowsAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        page = NormalizePage(page);
        pageSize = NormalizePageSize(pageSize);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string countSql = "SELECT COUNT(*)::int FROM workflow_entity;";
        await using var countCommand = new NpgsqlCommand(countSql, connection);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        const string sql = """
            SELECT id, name, active, description, "updatedAt", json_array_length(nodes)::int AS node_count
            FROM workflow_entity
            ORDER BY active DESC, "updatedAt" DESC, name ASC
            OFFSET @offset ROWS FETCH NEXT @page_size ROWS ONLY;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);
        command.Parameters.AddWithValue("page_size", pageSize);

        var items = new List<WorkflowSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new WorkflowSummaryDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                GetNullableString(reader, 3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetInt32(5)));
        }

        return new PagedResultDto<WorkflowSummaryDto>(items, totalCount, page, pageSize);
    }
}
