using System.Text.Json;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Domain.Entities;
using CryptoHft.Domain.Enums;
using CryptoHft.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoHft.Infrastructure.Ai;

// Bayesian online-learning of factor weights. Each AI decision is logged with its factor
// scores; after an evaluation horizon, the realized price move decides win/loss, and each
// factor that "agreed" with the call has its Beta(alpha,beta) posterior updated. The weight
// multiplier is the posterior mean normalized so the average factor stays at 1.0.
public sealed class AdaptiveWeightService(
    IServiceScopeFactory scopeFactory,
    ILogger<AdaptiveWeightService> logger) : IAdaptiveWeightService
{
    private static readonly TimeSpan EvaluationHorizon = TimeSpan.FromMinutes(60);
    private const decimal WinThresholdPercent = 0.30m; // favorable move >= 0.3% counts as a win

    public async Task<IReadOnlyDictionary<string, decimal>> GetMultipliersAsync(
        MarketRegime regime, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var stats = await db.FactorStats
            .Where(s => s.Regime == (int)regime)
            .ToListAsync(cancellationToken);

        if (stats.Count == 0) return new Dictionary<string, decimal>();

        // Posterior mean win-rate per factor
        var means = stats.ToDictionary(s => s.Factor, s => s.Alpha / (s.Alpha + s.Beta));
        var avg = means.Values.Average();
        if (avg <= 0) return new Dictionary<string, decimal>();

        // Multiplier relative to the average performer (clamped later by the engine)
        return means.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value / avg, 3));
    }

    public async Task LogDecisionAsync(AdvancedDecision decision, CancellationToken cancellationToken)
    {
        // Only learn from actionable directional calls
        if (decision.Action == DecisionAction.NoTrade) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        db.AiDecisionLogs.Add(new AiDecisionLog
        {
            Symbol = decision.Symbol,
            Regime = (int)decision.Regime,
            Action = decision.Action,
            Confidence = decision.Confidence,
            EntryPrice = decision.EntryPrice,
            ScoresJson = JsonSerializer.Serialize(decision.Scores)
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> EvaluatePendingAsync(decimal currentPrice, CancellationToken cancellationToken)
    {
        if (currentPrice <= 0) return 0;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var cutoff = DateTimeOffset.UtcNow - EvaluationHorizon;
        var pending = await db.AiDecisionLogs
            .Where(d => !d.Evaluated && d.CreatedAt <= cutoff)
            .OrderBy(d => d.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0) return 0;

        var statCache = await db.FactorStats.ToListAsync(cancellationToken);

        foreach (var log in pending)
        {
            var isBuy = log.Action is DecisionAction.WeakBuy or DecisionAction.Buy or DecisionAction.StrongBuy;
            var movePct = log.EntryPrice == 0 ? 0 : (currentPrice - log.EntryPrice) / log.EntryPrice * 100m;
            var directional = isBuy ? movePct : -movePct; // favorable move in the called direction
            var win = directional >= WinThresholdPercent;

            log.Evaluated = true;
            log.Win = win;
            log.PriceMovePercent = Math.Round(movePct, 4);
            log.EvaluatedAt = DateTimeOffset.UtcNow;

            Dictionary<string, decimal> scores;
            try { scores = JsonSerializer.Deserialize<Dictionary<string, decimal>>(log.ScoresJson) ?? new(); }
            catch { scores = new(); }

            foreach (var (factor, score) in scores)
            {
                // Did this factor agree with the called direction?
                var agreed = isBuy ? score >= 55m : score <= 45m;
                if (!agreed) continue; // only credit/blame factors that took a side

                var stat = statCache.FirstOrDefault(s => s.Regime == log.Regime && s.Factor == factor);
                if (stat is null)
                {
                    stat = new FactorStat { Regime = log.Regime, Factor = factor };
                    db.FactorStats.Add(stat);
                    statCache.Add(stat);
                }
                if (win) stat.Alpha += 1m; else stat.Beta += 1m;
                stat.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Adaptive learning evaluated {Count} decisions", pending.Count);
        return pending.Count;
    }

    public async Task<IReadOnlyList<FactorPerformance>> GetPerformanceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var stats = await db.FactorStats.ToListAsync(cancellationToken);

        var byRegime = stats.GroupBy(s => s.Regime);
        var result = new List<FactorPerformance>();
        foreach (var group in byRegime)
        {
            var means = group.ToDictionary(s => s.Factor, s => s.Alpha / (s.Alpha + s.Beta));
            var avg = means.Values.Count > 0 ? means.Values.Average() : 1m;
            foreach (var s in group)
            {
                var mean = s.Alpha / (s.Alpha + s.Beta);
                var samples = (int)(s.Alpha + s.Beta - 2m);
                result.Add(new FactorPerformance(
                    s.Factor, s.Regime, Math.Round(mean * 100m, 1),
                    Math.Max(0, samples), avg <= 0 ? 1m : Math.Round(mean / avg, 3)));
            }
        }
        return result.OrderByDescending(r => r.WinRate).ToList();
    }
}
