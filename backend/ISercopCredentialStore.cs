namespace backend;

internal sealed record SercopConfiguredCredential(
    string Ruc,
    string UserName,
    string Password
);

internal interface ISercopCredentialStore
{
    Task<SercopCredentialStatusDto> GetStatusAsync(CancellationToken cancellationToken);

    Task SaveAsync(string ruc, string userName, string password, long? configuredByUserId, string? configuredByLoginName, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);

    Task<SercopConfiguredCredential?> GetPortalCredentialAsync(CancellationToken cancellationToken);

    Task UpdateValidationAsync(string validationStatus, DateTimeOffset validatedAt, string? validationError, CancellationToken cancellationToken);
}
