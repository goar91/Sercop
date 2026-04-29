using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace backend;

internal sealed class SercopCredentialVault(NpgsqlDataSource dataSource, IDataProtectionProvider dataProtectionProvider) : ISercopCredentialStore
{
    private const string CredentialKey = "portal";
    private const string ValidationStatusPending = "pending";
    private const string PortalSessionNotAuthenticated = "not_authenticated";
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector("HDM.CRM.SERCOP.CREDENTIALS.V2");

    public async Task<SercopCredentialStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT masked_ruc,
                   masked_username,
                   username,
                   configured_by_login_name,
                   configured_at,
                   validation_status,
                   last_validated_at,
                   validation_error
            FROM sercop_credentials
            WHERE credential_key = @credential_key
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("credential_key", CredentialKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new SercopCredentialStatusDto(false, null, null, null, null, null, null, null, PortalSessionNotAuthenticated, null);
        }

        var maskedRuc = GetNullableString(reader, 0);
        var maskedUserName = GetNullableString(reader, 1);
        var legacyUserName = GetNullableString(reader, 2);

        return new SercopCredentialStatusDto(
            true,
            maskedRuc,
            maskedUserName ?? MaskSensitiveValue(legacyUserName),
            GetNullableString(reader, 3),
            reader.GetFieldValue<DateTimeOffset>(4),
            GetNullableString(reader, 5),
            GetNullableDateTimeOffset(reader, 6),
            GetNullableString(reader, 7),
            PortalSessionNotAuthenticated,
            null);
    }

    public async Task SaveAsync(string ruc, string userName, string password, long? configuredByUserId, string? configuredByLoginName, CancellationToken cancellationToken)
    {
        var normalizedRuc = ruc.Trim();
        var normalizedUserName = userName.Trim();
        var normalizedPassword = password.Trim();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO sercop_credentials (
              credential_key,
              username,
              username_encrypted,
              masked_username,
              ruc_encrypted,
              masked_ruc,
              password_encrypted,
              encryption_scope,
              configured_by_user_id,
              configured_by_login_name,
              configured_at,
              validation_status,
              last_validated_at,
              validation_error
            )
            VALUES (
              @credential_key,
              NULL,
              @username_encrypted,
              @masked_username,
              @ruc_encrypted,
              @masked_ruc,
              @password_encrypted,
              'aspnet_data_protection',
              @configured_by_user_id,
              @configured_by_login_name,
              NOW(),
              @validation_status,
              NULL,
              NULL
            )
            ON CONFLICT (credential_key)
            DO UPDATE SET
              username = NULL,
              username_encrypted = EXCLUDED.username_encrypted,
              masked_username = EXCLUDED.masked_username,
              ruc_encrypted = EXCLUDED.ruc_encrypted,
              masked_ruc = EXCLUDED.masked_ruc,
              password_encrypted = EXCLUDED.password_encrypted,
              encryption_scope = EXCLUDED.encryption_scope,
              configured_by_user_id = EXCLUDED.configured_by_user_id,
              configured_by_login_name = EXCLUDED.configured_by_login_name,
              configured_at = NOW(),
              validation_status = EXCLUDED.validation_status,
              last_validated_at = NULL,
              validation_error = NULL,
              updated_at = NOW();
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("credential_key", CredentialKey);
        command.Parameters.AddWithValue("username_encrypted", Protect(normalizedUserName));
        command.Parameters.AddWithValue("masked_username", (object)MaskSensitiveValue(normalizedUserName)!);
        command.Parameters.AddWithValue("ruc_encrypted", Protect(normalizedRuc));
        command.Parameters.AddWithValue("masked_ruc", (object)MaskSensitiveValue(normalizedRuc)!);
        command.Parameters.AddWithValue("password_encrypted", Protect(normalizedPassword));
        AddNullableInt64(command.Parameters, "configured_by_user_id", configuredByUserId);
        AddNullableText(command.Parameters, "configured_by_login_name", NormalizeOptionalText(configuredByLoginName));
        command.Parameters.AddWithValue("validation_status", ValidationStatusPending);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            DELETE FROM sercop_credentials
            WHERE credential_key = @credential_key;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("credential_key", CredentialKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SercopConfiguredCredential?> GetPortalCredentialAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT ruc_encrypted, username_encrypted, username, password_encrypted
            FROM sercop_credentials
            WHERE credential_key = @credential_key
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("credential_key", CredentialKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var encryptedRuc = GetNullableString(reader, 0);
        var encryptedUserName = GetNullableString(reader, 1);
        var legacyUserName = GetNullableString(reader, 2);
        var encryptedPassword = GetNullableString(reader, 3);

        if (string.IsNullOrWhiteSpace(encryptedPassword))
        {
            return null;
        }

        var resolvedRuc = string.IsNullOrWhiteSpace(encryptedRuc) ? null : Unprotect(encryptedRuc);
        var resolvedUserName = string.IsNullOrWhiteSpace(encryptedUserName)
            ? legacyUserName
            : Unprotect(encryptedUserName);

        if (string.IsNullOrWhiteSpace(resolvedRuc) || string.IsNullOrWhiteSpace(resolvedUserName))
        {
            return null;
        }

        return new SercopConfiguredCredential(
            resolvedRuc,
            resolvedUserName,
            Unprotect(encryptedPassword));
    }

    public async Task UpdateValidationAsync(string validationStatus, DateTimeOffset validatedAt, string? validationError, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE sercop_credentials
            SET validation_status = @validation_status,
                last_validated_at = @last_validated_at,
                validation_error = @validation_error,
                updated_at = NOW()
            WHERE credential_key = @credential_key;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("credential_key", CredentialKey);
        command.Parameters.AddWithValue("validation_status", validationStatus);
        command.Parameters.AddWithValue("last_validated_at", validatedAt);
        AddNullableText(command.Parameters, "validation_error", NormalizeOptionalText(validationError));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddNullableText(NpgsqlParameterCollection parameters, string name, string? value)
        => parameters.AddWithValue(name, string.IsNullOrWhiteSpace(value) ? DBNull.Value : value);

    private static void AddNullableInt64(NpgsqlParameterCollection parameters, string name, long? value)
        => parameters.AddWithValue(name, value.HasValue ? value.Value : DBNull.Value);

    private static string? GetNullableString(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? GetNullableDateTimeOffset(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static string? MaskSensitiveValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= 4)
        {
            return new string('*', trimmed.Length);
        }

        if (trimmed.Contains('@', StringComparison.Ordinal))
        {
            var atIndex = trimmed.IndexOf('@', StringComparison.Ordinal);
            var prefix = atIndex <= 2 ? trimmed[..1] : trimmed[..2];
            return $"{prefix}***{trimmed[atIndex..]}";
        }

        return $"{trimmed[..2]}***{trimmed[^2..]}";
    }

    private string Protect(string value)
        => protector.Protect(value);

    private string Unprotect(string value)
        => protector.Unprotect(value);
}
