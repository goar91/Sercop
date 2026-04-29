using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace backend;

public sealed class OpportunityRetentionCleanupBackgroundService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<OpportunityRetentionCleanupBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (GetRetentionDays() <= 0)
        {
            logger.LogInformation("Limpieza de retencion desactivada: CRM_RETENTION_DAYS <= 0.");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(35), stoppingToken);
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

    private int GetRetentionDays()
        => int.TryParse(configuration["CRM_RETENTION_DAYS"], out var days) && days >= 1
            ? Math.Clamp(days, 1, 30)
            : 5;

    private int GetIntervalMinutes()
        => int.TryParse(configuration["CRM_RETENTION_CLEANUP_INTERVAL_MINUTES"], out var minutes) && minutes >= 5
            ? Math.Min(1440, minutes)
            : 60;

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var cutoff = EcuadorTime.Now().AddDays(-GetRetentionDays()).ToUniversalTime();

        using var scope = serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<CrmRepository>();

        try
        {
            var result = await repository.CleanupRetentionAsync(cutoff, cancellationToken);
            logger.LogInformation(
                "Limpieza de retencion completada. Cutoff={Cutoff} Opportunities={Opportunities} AnalysisRuns={AnalysisRuns} FeedbackEvents={FeedbackEvents}",
                cutoff,
                result.DeletedOpportunities,
                result.DeletedAnalysisRuns,
                result.DeletedFeedbackEvents);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Fallo la limpieza de retencion.");
        }
    }
}

