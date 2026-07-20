using CryptoHft.Application.DecisionEngine;

namespace CryptoHft.Api.BackgroundServices;

// Periodically evaluates logged AI decisions and updates the Bayesian per-factor performance
// stats that feed the adaptive weighting. Realized outcomes from closed positions (Position
// History) are evaluated first; the price-movement horizon is only a weak, correlation-safe
// fallback for decisions that never became a trade. EvaluatePending also deterministically
// reconciles FactorStats, repairing legacy counters that over-counted repeated loop snapshots.
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
            // Realized-outcome learning first: needs no market data, so a price-feed hiccup
            // never blocks it.
            try
            {
                await adaptiveWeights.EvaluateClosedPositionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI learning tick failed (closed positions)");
            }

            try
            {
                var price = await priceProvider.GetLastPriceAsync(Symbol, stoppingToken);
                await adaptiveWeights.EvaluatePendingAsync(price, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI learning tick failed (price fallback)");
            }
        }
    }
}
