using System.Text.Json;
using backend.Auth;
using Npgsql;

namespace backend;

public sealed partial class CrmRepository
{
    public async Task EnsureBootstrapAdminAsync(
        string loginName,
        string fullName,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """
            INSERT INTO crm_users (login_name, full_name, email, role, active, password_hash, must_change_password)
            VALUES (@login_name, @full_name, @email, 'admin', TRUE, @password_hash, FALSE)
            ON CONFLICT (email) DO UPDATE
            SET login_name = EXCLUDED.login_name,
                full_name = EXCLUDED.full_name,
                role = 'admin',
                active = TRUE,
                password_hash = COALESCE(crm_users.password_hash, EXCLUDED.password_hash),
                must_change_password = FALSE,
                updated_at = NOW();
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("login_name", loginName.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("full_name", fullName.Trim());
        command.Parameters.AddWithValue("email", email.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("password_hash", CrmPasswordHasher.HashPassword(password));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CurrentUserDto?> AuthenticateAsync(string identifier, string password, CancellationToken cancellationToken)
    {
        var normalizedIdentifier = NormalizeNullableText(identifier)?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedIdentifier) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var record = await GetAuthUserRecordAsync(connection, normalizedIdentifier, cancellationToken);
        if (record is null || !record.Active || !CrmPasswordHasher.Verify(password, record.PasswordHash))
        {
            return null;
        }

        const string touchSql = """
            UPDATE crm_users
            SET last_login_at = NOW(),
                updated_at = NOW()
            WHERE id = @id;
            """;

        await using var touchCommand = new NpgsqlCommand(touchSql, connection);
        touchCommand.Parameters.AddWithValue("id", record.Id);
        await touchCommand.ExecuteNonQueryAsync(cancellationToken);

        return new CurrentUserDto(
            record.Id,
            record.LoginName,
            record.FullName,
            record.Email,
            record.Role,
            record.ZoneId,
            record.ZoneName,
            record.MustChangePassword);
    }

    public async Task<CurrentUserDto?> GetCurrentUserAsync(long userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """
            SELECT u.id, u.login_name, u.full_name, u.email, u.role, u.zone_id, z.name, u.must_change_password
            FROM crm_users u
            LEFT JOIN crm_zones z ON z.id = u.zone_id
            WHERE u.id = @id
              AND u.active = TRUE;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CurrentUserDto(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            GetNullableInt64(reader, 5),
            GetNullableString(reader, 6),
            reader.GetBoolean(7));
    }

    public async Task WriteAuditLogAsync(
        long? actorUserId,
        string? actorLoginName,
        string actionType,
        string entityType,
        string? entityId,
        string? ipAddress,
        string? userAgent,
        object? details,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await WriteAuditLogAsync(connection, actorUserId, actorLoginName, actionType, entityType, entityId, ipAddress, userAgent, details, cancellationToken);
    }

    private static async Task WriteAuditLogAsync(
        NpgsqlConnection connection,
        long? actorUserId,
        string? actorLoginName,
        string actionType,
        string entityType,
        string? entityId,
        string? ipAddress,
        string? userAgent,
        object? details,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO crm_audit_logs (
              actor_user_id,
              actor_login_name,
              action_type,
              entity_type,
              entity_id,
              ip_address,
              user_agent,
              details_json
            )
            VALUES (
              @actor_user_id,
              @actor_login_name,
              @action_type,
              @entity_type,
              @entity_id,
              @ip_address,
              @user_agent,
              @details_json::jsonb
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        AddNullableInt64(command.Parameters, "actor_user_id", actorUserId);
        AddNullableText(command.Parameters, "actor_login_name", NormalizeNullableText(actorLoginName));
        command.Parameters.AddWithValue("action_type", actionType.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("entity_type", entityType.Trim().ToLowerInvariant());
        AddNullableText(command.Parameters, "entity_id", NormalizeNullableText(entityId));
        AddNullableText(command.Parameters, "ip_address", NormalizeNullableText(ipAddress));
        AddNullableText(command.Parameters, "user_agent", NormalizeNullableText(userAgent));
        command.Parameters.AddWithValue("details_json", JsonSerializer.Serialize(details ?? new { }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AuthUserRecord?> GetAuthUserRecordAsync(
        NpgsqlConnection connection,
        string identifier,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT u.id, u.login_name, u.full_name, u.email, u.role, u.active, u.zone_id, z.name, u.must_change_password, u.password_hash
            FROM crm_users u
            LEFT JOIN crm_zones z ON z.id = u.zone_id
            WHERE lower(u.login_name) = @identifier
               OR lower(u.email) = @identifier
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("identifier", identifier);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AuthUserRecord(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetBoolean(5),
            GetNullableInt64(reader, 6),
            GetNullableString(reader, 7),
            reader.GetBoolean(8),
            GetNullableString(reader, 9));
    }

    private sealed record AuthUserRecord(
        long Id,
        string LoginName,
        string FullName,
        string Email,
        string Role,
        bool Active,
        long? ZoneId,
        string? ZoneName,
        bool MustChangePassword,
        string? PasswordHash);
}
