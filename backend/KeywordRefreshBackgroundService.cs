using System.Diagnostics;
using System.Threading.Channels;

namespace backend;

internal interface IKeywordRefreshDispatcher
{
    ValueTask QueueAsync(long runId, CancellationToken cancellationToken = default);
}

internal sealed class KeywordRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment hostEnvironment,
    IConfiguration configuration,
    ILogger<KeywordRefreshBackgroundService> logger)
    : BackgroundService, IKeywordRefreshDispatcher
{
    private readonly Channel<long> queue = Channel.CreateUnbounded<long>();
    private readonly string rootPath = ResolveRootPath(hostEnvironment);
    private readonly TimeSpan workflowExecutionTimeout = ResolveWorkflowExecutionTimeout(configuration);

    public ValueTask QueueAsync(long runId, CancellationToken cancellationToken = default)
        => queue.Writer.WriteAsync(runId, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnqueuePendingRunsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var runId = await queue.Reader.ReadAsync(stoppingToken);
            await ProcessRunAsync(runId, stoppingToken);
        }
    }

    private async Task EnqueuePendingRunsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<CrmRepository>();
        foreach (var runId in await repository.GetQueuedKeywordRefreshRunIdsAsync(cancellationToken))
        {
            queue.Writer.TryWrite(runId);
        }
    }

    private async Task ProcessRunAsync(long runId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<CrmRepository>();
        var context = await repository.MarkKeywordRefreshRunRunningAsync(runId, cancellationToken);
        if (context is null)
        {
            return;
        }

        var reevaluatedCount = 0;
        var capturedCount = 0;
        var errorCount = 0;

        try
        {
            reevaluatedCount = await repository.ReevaluateCurrentOpportunitiesAsync(context.RequestedWindowDays, cancellationToken);

            if (ShouldCaptureNewOpportunities(context.TriggerType))
            {
                await EnsureN8nStartedAsync(cancellationToken);
                await CleanupEphemeralRunContainersAsync(cancellationToken);
                await ExecuteWorkflowAsync("1001", context.RequestedWindowDays, cancellationToken);
                capturedCount = await repository.CountNewOpportunitiesCapturedSinceAsync(context.StartedAt, context.RequestedWindowDays, cancellationToken);
            }

            await repository.CompleteKeywordRefreshRunAsync(runId, reevaluatedCount, capturedCount, errorCount, null, cancellationToken);
        }
        catch (Exception exception)
        {
            errorCount = Math.Max(errorCount, 1);
            logger.LogError(exception, "Fallo el refresh de palabras clave {RunId}", runId);
            await repository.FailKeywordRefreshRunAsync(runId, reevaluatedCount, capturedCount, errorCount, exception.Message, cancellationToken);
        }
    }

    private async Task EnsureN8nStartedAsync(CancellationToken cancellationToken)
    {
        var (_, standardError) = await RunProcessAsync(
            "docker",
            ["compose", "up", "-d", "n8n"],
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(standardError) && standardError.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("docker compose up -d n8n devolvio stderr: {Error}", standardError);
        }
    }

    private async Task ExecuteWorkflowAsync(string workflowId, int recentWindowDays, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(workflowExecutionTimeout);

        try
        {
            await RunProcessAsync(
                "docker",
                [
                    "compose",
                    "run",
                    "--rm",
                    "-T",
                    "--entrypoint",
                    "n8n",
                    "-e",
                    $"SERCOP_RECENT_WINDOW_DAYS={recentWindowDays}",
                    "-e",
                    "OCDS_MAX_SEARCH_TERMS=0",
                    "n8n",
                    "execute",
                    $"--id={workflowId}",
                    "--rawOutput"
                ],
                timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await CleanupEphemeralRunContainersAsync(CancellationToken.None);
            throw new InvalidOperationException($"La ejecucion del workflow {workflowId} supero el timeout de {workflowExecutionTimeout.TotalMinutes:0} minutos.");
        }
    }

    private async Task CleanupEphemeralRunContainersAsync(CancellationToken cancellationToken)
    {
        var (containerIds, _) = await RunProcessAsync(
            "docker",
            ["ps", "-aq", "--filter", "name=n8n-run-"],
            cancellationToken);

        var ids = containerIds
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var id in ids)
        {
            await RunProcessAsync("docker", ["rm", "-f", id], cancellationToken);
        }
    }

    private async Task<(string StandardOutput, string StandardError)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = rootPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var dockerConfig = Path.Combine(rootPath, ".docker");
        if (Directory.Exists(dockerConfig))
        {
            process.StartInfo.Environment["DOCKER_CONFIG"] = dockerConfig;
        }

        try
        {
            process.Start();
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var standardOutput = await standardOutputTask;
            var standardError = await standardErrorTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"El comando '{fileName} {string.Join(' ', arguments)}' fallo con codigo {process.ExitCode}. {standardError}".Trim());
            }

            return (standardOutput, standardError);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore cleanup failures; the caller will handle timeout semantics.
            }

            throw;
        }
    }

    private static string ResolveRootPath(IHostEnvironment hostEnvironment)
    {
        foreach (var candidate in new[]
        {
            hostEnvironment.ContentRootPath,
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
        })
        {
            var current = Path.GetFullPath(candidate);
            for (var depth = 0; depth < 8 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                if (File.Exists(Path.Combine(current, "docker-compose.yml")))
                {
                    return current;
                }

                var parent = Directory.GetParent(current);
                if (parent is null)
                {
                    break;
                }

                current = parent.FullName;
            }
        }

        throw new DirectoryNotFoundException("No se encontro la raiz del proyecto con docker-compose.yml para ejecutar refresh de keywords.");
    }

    private static bool ShouldCaptureNewOpportunities(string? triggerType)
        => string.Equals(triggerType, "manual", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan ResolveWorkflowExecutionTimeout(IConfiguration configuration)
    {
        if (int.TryParse(configuration["KEYWORD_REFRESH_WORKFLOW_TIMEOUT_MINUTES"], out var configuredMinutes))
        {
            return TimeSpan.FromMinutes(Math.Clamp(configuredMinutes, 2, 60));
        }

        return TimeSpan.FromMinutes(15);
    }
}
