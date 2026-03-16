using System.Text.Json;
using Npgsql;

namespace backend;

public sealed partial class CrmRepository
{
    public async Task<IReadOnlyList<StudioAssetDto>> GetStudioAssetsAsync(
        string assetScope,
        long? opportunityId,
        string? workflowId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                id,
                asset_type,
                asset_scope,
                opportunity_id,
                workflow_id,
                title,
                format,
                audience,
                tone,
                model_name,
                content_text,
                payload_json::text as payload_json,
                created_at
            from crm_content_assets
            where asset_scope = @asset_scope
              and (@opportunity_id is null or opportunity_id = @opportunity_id)
              and (@workflow_id is null or workflow_id = @workflow_id)
            order by created_at desc
            limit 24;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("asset_scope", assetScope);
        command.Parameters.Add(new NpgsqlParameter("opportunity_id", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)opportunityId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("workflow_id", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)NullIfWhiteSpace(workflowId) ?? DBNull.Value });

        var items = new List<StudioAssetDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadStudioAsset(reader));
        }

        return items;
    }

    public async Task<StudioAssetDto?> GetStudioAssetAsync(long id, CancellationToken cancellationToken)
    {
        const string sql = """
            select
                id,
                asset_type,
                asset_scope,
                opportunity_id,
                workflow_id,
                title,
                format,
                audience,
                tone,
                model_name,
                content_text,
                payload_json::text as payload_json,
                created_at
            from crm_content_assets
            where id = @id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadStudioAsset(reader) : null;
    }

    public async Task<StudioAssetDto> CreateStudioAssetAsync(
        string assetType,
        string assetScope,
        long? opportunityId,
        string? workflowId,
        string title,
        string format,
        string? audience,
        string? tone,
        string modelName,
        string? contentText,
        object payload,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into crm_content_assets (
                asset_type,
                asset_scope,
                opportunity_id,
                workflow_id,
                title,
                format,
                audience,
                tone,
                model_name,
                content_text,
                payload_json
            )
            values (
                @asset_type,
                @asset_scope,
                @opportunity_id,
                @workflow_id,
                @title,
                @format,
                @audience,
                @tone,
                @model_name,
                @content_text,
                @payload_json::jsonb
            )
            returning
                id,
                asset_type,
                asset_scope,
                opportunity_id,
                workflow_id,
                title,
                format,
                audience,
                tone,
                model_name,
                content_text,
                payload_json::text as payload_json,
                created_at;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("asset_type", assetType);
        command.Parameters.AddWithValue("asset_scope", assetScope);
        command.Parameters.Add(new NpgsqlParameter("opportunity_id", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)opportunityId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("workflow_id", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)NullIfWhiteSpace(workflowId) ?? DBNull.Value });
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("format", format);
        command.Parameters.Add(new NpgsqlParameter("audience", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)NullIfWhiteSpace(audience) ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("tone", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)NullIfWhiteSpace(tone) ?? DBNull.Value });
        command.Parameters.AddWithValue("model_name", modelName);
        command.Parameters.Add(new NpgsqlParameter("content_text", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)NullIfWhiteSpace(contentText) ?? DBNull.Value });
        command.Parameters.AddWithValue("payload_json", JsonSerializer.Serialize(payload));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadStudioAsset(reader);
    }

    private static StudioAssetDto ReadStudioAsset(NpgsqlDataReader reader)
        => new(
            GetInt64(reader, "id"),
            GetString(reader, "asset_type"),
            GetString(reader, "asset_scope"),
            GetNullableInt64(reader, "opportunity_id"),
            GetNullableString(reader, "workflow_id"),
            GetString(reader, "title"),
            GetString(reader, "format"),
            GetNullableString(reader, "audience"),
            GetNullableString(reader, "tone"),
            GetString(reader, "model_name"),
            GetNullableString(reader, "content_text"),
            GetString(reader, "payload_json"),
            GetDateTimeOffset(reader, "created_at"));
}
