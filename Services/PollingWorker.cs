using Microsoft.Extensions.Options;
using Watchboard.Api.Models;

namespace Watchboard.Api.Services;

public sealed class PollingWorker(RefreshCoordinator refresh, IOptions<WatchboardOptions> options,
    TimeProvider clock, ILogger<PollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.EnablePolling) return;
        // Runs only inside this process. No scheduled tasks, OS services, or external workers.
        await Task.Delay(TimeSpan.FromSeconds(3), clock, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            try { await refresh.RefreshAsync(stoppingToken, automatic: true); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Polling failed; the next in-process tick will retry"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
