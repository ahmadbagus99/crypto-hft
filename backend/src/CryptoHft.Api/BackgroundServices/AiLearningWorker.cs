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

    // Same watchdog rationale as AutoTradingWorker: a hung await must never freeze the
    // learning loop permanently.
    private static readonly TimeSpan TickBudget = TimeSpan.FromMinutes(4);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AI learning worker started (interval {Interval}m)", Interval.TotalMinutes);
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            tickCts.CancelAfter(TickBudget);

            // Realized-outcome learning first: needs no market data, so a price-feed hiccup
            // never blocks it.
            try
            {
                await adaptiveWeights.EvaluateClosedPositionsAsync(tickCts.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("AI learning tick (closed positions) exceeded its watchdog budget and was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI learning tick failed (closed positions)");
            }

            try
            {
                var price = await priceProvider.GetLastPriceAsync(Symbol, tickCts.Token);
                await adaptiveWeights.EvaluatePendingAsync(price, tickCts.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("AI learning tick (price fallback) exceeded its watchdog budget and was cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI learning tick failed (price fallback)");
            }
        }
    }
}
