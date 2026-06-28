using CryptoHft.Application.Abstractions;

namespace CryptoHft.Api.BackgroundServices;

public sealed class KillSwitchHeartbeatWorker(
    IKillSwitchService killSwitchService,
    ILogger<KillSwitchHeartbeatWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var state = killSwitchService.GetState();
            if (!state.Enabled || state.NextHeartbeatAt is null || state.NextHeartbeatAt > DateTimeOffset.UtcNow)
            {
                continue;
            }

            try
            {
                await killSwitchService.SendHeartbeatAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Kill switch heartbeat failed.");
            }
        }
    }
}

