using CryptoHft.Application.DecisionEngine;

namespace CryptoHft.Api.BackgroundServices;

// Periodically evaluates logged AI decisions against realized price and updates the
// Bayesian per-factor performance stats that feed the adaptive weighting.
public sealed class AiLearningWorker(
    IAdaptiveWeightService adaptiveWeights,
    IMultiTimeframeProvider priceProvider,
    ILogger<AiLearningWorker> logger) : BackgroundService
{
    private const string Symbol = "BTCUSDT";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AI learning worker started (interval {Interval}m)", Interval.TotalMinutes);
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var price = await priceProvider.GetLastPriceAsync(Symbol, stoppingToken);
                await adaptiveWeights.EvaluatePendingAsync(price, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI learning tick failed");
            }
        }
    }
}
