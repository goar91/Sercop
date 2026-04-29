using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace backend;

public sealed class PublicInvitationSyncService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<PublicInvitationSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled())
        {
            logger.LogInformation("Sincronizacion publica de invitaciones desactivada.");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(GetIntervalMinutes()));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private bool IsEnabled()
        => !string.Equals(configuration["INVITATION_SYNC_ACTIVE"], "false", StringComparison.OrdinalIgnoreCase);

    private int GetIntervalMinutes()
        => int.TryParse(configuration["INVITATION_SYNC_INTERVAL_MINUTES"], out var minutes) && minutes >= 5
            ? minutes
            : 30;

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<CrmRepository>();
        var invitationClient = scope.ServiceProvider.GetRequiredService<SercopInvitationPublicClient>();
        var credentialStore = scope.ServiceProvider.GetRequiredService<ISercopCredentialStore>();
        var sercopAuthenticatedClient = scope.ServiceProvider.GetRequiredService<SercopAuthenticatedClient>();
        var invitedCompanyName = configuration["INVITED_COMPANY_NAME"] ?? "HDM";
        var invitedCompanyRuc = configuration["INVITED_COMPANY_RUC"];
        if (string.IsNullOrWhiteSpace(invitedCompanyRuc))
        {
            try
            {
                var credential = await credentialStore.GetPortalCredentialAsync(cancellationToken);
                if (credential is not null && !string.IsNullOrWhiteSpace(credential.Ruc))
                {
                    invitedCompanyRuc = credential.Ruc.Trim();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "No se pudo resolver INVITED_COMPANY_RUC desde sercop_credentials.");
            }
        }

        try
        {
            var sessionValidation = await sercopAuthenticatedClient.ValidateStoredCredentialAsync(forceReauthenticate: false, cancellationToken);
            if (!sessionValidation.IsSuccess)
            {
                logger.LogWarning("La cuenta SERCOP configurada no esta autenticada. El sync continuara solo con el RUC configurado. Motivo={FailureReason}", sessionValidation.FailureReason);
            }

            var result = await repository.SyncInvitationsFromPublicReportsAsync(
                invitationClient,
                invitedCompanyName,
                invitedCompanyRuc,
                cancellationToken);

            logger.LogInformation(
                "Sincronizacion de invitaciones completada. Escaneados={Scanned} Confirmados={Confirmed} Actualizados={Updated} Errores={Errors}",
                result.ScannedCount,
                result.ConfirmedCount,
                result.UpdatedCount,
                result.Errors.Count);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Fallo la sincronizacion publica de invitaciones.");
        }
    }
}
