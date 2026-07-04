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
// scores. The preferred evaluation is the REAL outcome: when the position a decision opened
// closes (Position History), win/loss comes from realized PnL and the update is weighted by
// ROI magnitude. Decisions that never became a trade fall back to the 60-minute price-move
// horizon. Each factor that "agreed" with the call has its Beta(alpha,beta) posterior updated;
// the weight multiplier is the posterior mean normalized so the average factor stays at 1.0.
public sealed class AdaptiveWeightService(
    IServiceScopeFactory scopeFactory,
    ILogger<AdaptiveWeightService> logger) : IAdaptiveWeightService
{
    private static readonly TimeSpan EvaluationHorizon = TimeSpan.FromMinutes(60);
    private const decimal WinThresholdPercent = 0.30m; // fallback only: favorable move >= 0.3% counts as a win

    // Realized-outcome learning:
    // Only closed positions this recent are scanned for matching (bounds the query; manual
    // trades without a matching decision are re-scanned harmlessly until they age out).
    private static readonly TimeSpan ClosedPositionLookback = TimeSpan.FromDays(7);
    // The opening decision must precede the first observation of the position by at most this.
    private static readonly TimeSpan PositionMatchWindow = TimeSpan.FromMinutes(10);
    // Small tolerance for clock ordering between decision logging and position observation.
    private static readonly TimeSpan OpenObservationTolerance = TimeSpan.FromMinutes(1);
    // A live entry order this soon after a decision marks that decision as "executed".
    private static readonly TimeSpan ExecutedOrderMatchWindow = TimeSpan.FromMinutes(5);
    // Executed decisions wait for their closed position instead of the price fallback — but
    // never longer than this (fail-safe if the position row was never written).
    private static readonly TimeSpan ExecutedFallbackDeadline = TimeSpan.FromHours(48);
    // Realized updates are weighted by ROI magnitude: 1 + |roi|, capped so one outlier
    // trade cannot dominate the posterior.
    private const decimal MaxOutcomeWeight = 3m;

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

        // Record Claude's verdict alongside the decision so realized outcomes can later be
        // sliced by confirmed vs hesitant (LLM fields stay null when Claude was not consulted).
        var llm = decision.Llm;
        var llmUsed = llm.Used;
        // Stops count as applied when Claude proposed a pair and the final decision carries it
        // (ApplyValidation rounds accepted stops to 2 dp before adopting them).
        var stopsApplied = llmUsed
            && llm.StopLoss is decimal sl && llm.TakeProfit is decimal tp
            && decision.StopLoss == Math.Round(sl, 2)
            && decision.TakeProfit == Math.Round(tp, 2);

        db.AiDecisionLogs.Add(new AiDecisionLog
        {
            Symbol = decision.Symbol,
            Regime = (int)decision.Regime,
            Action = decision.Action,
            Confidence = decision.Confidence,
            EntryPrice = decision.EntryPrice,
            ScoresJson = JsonSerializer.Serialize(decision.Scores),
            LlmConfirmed = llmUsed ? llm.Confirmed : null,
            LlmSizeMultiplier = llmUsed ? llm.SizeMultiplier : null,
            LlmLeverage = llmUsed ? llm.Leverage : null,
            LlmStopsApplied = llmUsed ? stopsApplied : null
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    // Realized-outcome learning: pair each freshly-closed position with the decision that opened
    // it (same symbol, same direction, logged just before the position was first observed) and
    // judge that decision on realized PnL instead of a price snapshot.
    public async Task<int> EvaluateClosedPositionsAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var since = DateTimeOffset.UtcNow - ClosedPositionLookback;
        // Anti-join on MatchedPositionId: one closed position evaluates one decision, once.
        var closed = await db.Positions
            .Where(p => p.ClosedAt != null && p.ClosedAt >= since)
            .Where(p => !db.AiDecisionLogs.Any(d => d.MatchedPositionId == p.Id))
            .OrderBy(p => p.ClosedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        if (closed.Count == 0) return 0;

        var statCache = await db.FactorStats.ToListAsync(cancellationToken);
        var execCache = await db.ExecutionStats.ToListAsync(cancellationToken);
        var matched = 0;

        foreach (var position in closed)
        {
            var log = await FindOpeningDecisionAsync(db, position, cancellationToken);
            if (log is null) continue; // manual/external trade, or opener already evaluated

            var win = position.RealizedPnl > 0m; // realizedPnl <= 0 counts as a loss
            var weight = Math.Clamp(1m + Math.Abs(position.Roi), 1m, MaxOutcomeWeight);

            log.Evaluated = true;
            log.Win = win;
            log.EvaluatedAt = DateTimeOffset.UtcNow;
            log.MatchedPositionId = position.Id;
            // Informational: raw entry -> last-mark move of the position (signed, not directional)
            log.PriceMovePercent = position.EntryPrice == 0
                ? 0
                : Math.Round((position.MarkPrice - position.EntryPrice) / position.EntryPrice * 100m, 4);

            ApplyFactorOutcome(db, statCache, log, win, weight);
            ApplyExecutionOutcome(db, execCache, log.Regime, position, win);
            matched++;

            logger.LogInformation(
                "Adaptive learning: closed {Side} {Symbol} realizedPnl={Pnl:F4} roi={Roi:P2} -> decision {DecisionId} {Outcome} (weight {Weight:F2})",
                position.Side, position.Symbol, position.RealizedPnl, position.Roi, log.Id,
                win ? "WIN" : "LOSS", weight);
        }

        if (matched > 0)
            await db.SaveChangesAsync(cancellationToken);
        return matched;
    }

    public async Task<int> EvaluatePendingAsync(decimal currentPrice, CancellationToken cancellationToken)
    {
        if (currentPrice <= 0) return 0;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var now = DateTimeOffset.UtcNow;
        var cutoff = now - EvaluationHorizon;
        var pending = await db.AiDecisionLogs
            .Where(d => !d.Evaluated && d.CreatedAt <= cutoff)
            .OrderBy(d => d.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0) return 0;

        // Decisions that actually opened a live trade are NOT judged on the 60-minute price
        // move — their realized outcome arrives via EvaluateClosedPositionsAsync when the
        // position closes. Only the single decision that immediately preceded each live entry
        // order counts as executed (older same-direction decisions keep the price fallback).
        var executedIds = await ResolveExecutedDecisionIdsAsync(db, pending, cancellationToken);

        var statCache = await db.FactorStats.ToListAsync(cancellationToken);
        var evaluated = 0;

        foreach (var log in pending)
        {
            if (executedIds.Contains(log.Id) && now - log.CreatedAt < ExecutedFallbackDeadline)
                continue; // realized outcome pending — leave for the closed-position pass

            var isBuy = IsBuyAction(log.Action);
            var movePct = log.EntryPrice == 0 ? 0 : (currentPrice - log.EntryPrice) / log.EntryPrice * 100m;
            var directional = isBuy ? movePct : -movePct; // favorable move in the called direction
            var win = directional >= WinThresholdPercent;

            log.Evaluated = true;
            log.Win = win;
            log.PriceMovePercent = Math.Round(movePct, 4);
            log.EvaluatedAt = DateTimeOffset.UtcNow;

            ApplyFactorOutcome(db, statCache, log, win, weight: 1m);
            evaluated++;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (evaluated > 0)
            logger.LogInformation("Adaptive learning evaluated {Count} decisions (price fallback)", evaluated);
        return evaluated;
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

    // Learned execution baselines for a regime; defaults until enough realized exits exist.
    public async Task<ExecutionTuning> GetExecutionTuningAsync(MarketRegime regime, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var stat = await db.ExecutionStats
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Regime == (int)regime, cancellationToken);
        return stat is null
            ? ExecutionTuning.Default
            : new ExecutionTuning(stat.SlAtrMultiplier, stat.TpAtrMultiplier, stat.LeverageFactor);
    }

    // Compact realized-history digest for the LLM prompt. Null when nothing realized yet,
    // so the prompt stays byte-identical to the pre-learning version (fail-safe).
    public async Task<LearningSnapshot?> GetLearningSnapshotAsync(MarketRegime regime, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var stat = await db.ExecutionStats
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Regime == (int)regime, cancellationToken);
        var validation = await GetValidationPerformanceAsync(cancellationToken);

        if (stat is null && validation.Count == 0) return null;

        var trades = stat is null ? 0 : stat.Wins + stat.Losses;
        return new LearningSnapshot(
            RegimeTrades: trades,
            RegimeWinRate: trades == 0 ? 0m : Math.Round(stat!.Wins * 100m / trades, 1),
            TakeProfitHits: stat?.TakeProfitHits ?? 0,
            StopLossHits: stat?.StopLossHits ?? 0,
            SlAtrMultiplier: stat?.SlAtrMultiplier ?? ExecutionTuningPolicy.DefaultSlAtrMultiplier,
            TpAtrMultiplier: stat?.TpAtrMultiplier ?? ExecutionTuningPolicy.DefaultTpAtrMultiplier,
            LeverageFactor: stat?.LeverageFactor ?? ExecutionTuningPolicy.DefaultLeverageFactor,
            ValidationOutcomes: validation);
    }

    // Realized outcomes sliced by Claude's verdict at decision time. Only decisions matched
    // to a closed position are counted — actual PnL, not the 60-minute price fallback.
    public async Task<IReadOnlyList<ValidationOutcome>> GetValidationPerformanceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var rows = await (
                from d in db.AiDecisionLogs
                where d.MatchedPositionId != null
                join p in db.Positions on d.MatchedPositionId equals p.Id
                select new { d.LlmConfirmed, d.LlmSizeMultiplier, p.Roi, p.RealizedPnl })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.LlmConfirmed switch
            {
                true => "confirmed",
                false => "hesitant",
                null => "no-validation"
            })
            .Select(g =>
            {
                var wins = g.Count(r => r.RealizedPnl > 0m);
                var withMultiplier = g.Where(r => r.LlmSizeMultiplier is not null).ToList();
                return new ValidationOutcome(
                    Verdict: g.Key,
                    Samples: g.Count(),
                    Wins: wins,
                    WinRate: Math.Round(wins * 100m / g.Count(), 1),
                    AvgRoi: Math.Round(g.Average(r => r.Roi), 4),
                    TotalRealizedPnl: Math.Round(g.Sum(r => r.RealizedPnl), 4),
                    AvgSizeMultiplier: withMultiplier.Count == 0
                        ? 1m
                        : Math.Round(withMultiplier.Average(r => r.LlmSizeMultiplier!.Value), 3));
            })
            .OrderByDescending(o => o.Samples)
            .ToList();
    }

    // The opening decision of a position: latest unevaluated log with the matching direction,
    // logged shortly before the position was first observed. Latest wins because the auto-trade
    // loop logs the opener last before entry analysis pauses.
    private static Task<AiDecisionLog?> FindOpeningDecisionAsync(
        TradingDbContext db, Position position, CancellationToken cancellationToken)
    {
        var earliest = position.OpenedAt - PositionMatchWindow;
        var latest = position.OpenedAt + OpenObservationTolerance;
        var wantBuy = position.Side == TradeSide.Long; // BUY decisions open LONG, SELL open SHORT

        return db.AiDecisionLogs
            .Where(d => !d.Evaluated
                        && d.Symbol == position.Symbol
                        && d.CreatedAt >= earliest
                        && d.CreatedAt <= latest)
            .Where(d => wantBuy
                ? d.Action == DecisionAction.WeakBuy || d.Action == DecisionAction.Buy || d.Action == DecisionAction.StrongBuy
                : d.Action == DecisionAction.WeakSell || d.Action == DecisionAction.Sell || d.Action == DecisionAction.StrongSell)
            .OrderByDescending(d => d.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // For each live (non-paper) entry order near the pending decisions, resolve the single
    // decision that opened it: the latest same-direction log at most ExecutedOrderMatchWindow
    // before the order.
    private static async Task<HashSet<Guid>> ResolveExecutedDecisionIdsAsync(
        TradingDbContext db, List<AiDecisionLog> pending, CancellationToken cancellationToken)
    {
        var minCreated = pending.Min(d => d.CreatedAt);
        var maxCreated = pending.Max(d => d.CreatedAt) + ExecutedOrderMatchWindow;

        var entryOrders = await db.Orders
            .AsNoTracking()
            .Where(o => !o.ReduceOnly && !o.IsPaper
                        && o.CreatedAt >= minCreated && o.CreatedAt <= maxCreated)
            .Select(o => new { o.Symbol, o.Side, o.CreatedAt })
            .ToListAsync(cancellationToken);

        var executedIds = new HashSet<Guid>();
        foreach (var order in entryOrders)
        {
            var earliest = order.CreatedAt - ExecutedOrderMatchWindow;
            var wantBuy = order.Side == TradeSide.Long;
            var opener = await db.AiDecisionLogs
                .AsNoTracking()
                .Where(d => d.Symbol == order.Symbol
                            && d.CreatedAt >= earliest
                            && d.CreatedAt <= order.CreatedAt)
                .Where(d => wantBuy
                    ? d.Action == DecisionAction.WeakBuy || d.Action == DecisionAction.Buy || d.Action == DecisionAction.StrongBuy
                    : d.Action == DecisionAction.WeakSell || d.Action == DecisionAction.Sell || d.Action == DecisionAction.StrongSell)
                .OrderByDescending(d => d.CreatedAt)
                .Select(d => (Guid?)d.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (opener is Guid id)
                executedIds.Add(id);
        }
        return executedIds;
    }

    // Credit/blame every factor that agreed with the called direction. Weight scales the
    // Beta-posterior increment (1 = plain fallback observation; realized outcomes use 1+|roi|).
    private static void ApplyFactorOutcome(
        TradingDbContext db, List<FactorStat> statCache, AiDecisionLog log, bool win, decimal weight)
    {
        var isBuy = IsBuyAction(log.Action);

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
            if (win) stat.Alpha += weight; else stat.Beta += weight;
            stat.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    // Per-regime execution learning: exit outcomes recompute the SL/TP geometry and the
    // leverage factor deterministically from the full counters (ExecutionTuningPolicy).
    private static void ApplyExecutionOutcome(
        TradingDbContext db, List<ExecutionStat> cache, int regime, Position position, bool win)
    {
        var stat = cache.FirstOrDefault(s => s.Regime == regime);
        if (stat is null)
        {
            stat = new ExecutionStat { Regime = regime };
            db.ExecutionStats.Add(stat);
            cache.Add(stat);
        }

        if (win) stat.Wins++; else stat.Losses++;
        if (position.CloseReason == PositionCloseReason.TakeProfit) stat.TakeProfitHits++;
        else if (position.CloseReason == PositionCloseReason.StopLoss) stat.StopLossHits++;
        // Other close reasons (auto/manual/unknown) still teach win/loss for leverage, but
        // never distort the SL/TP geometry counters.

        (stat.SlAtrMultiplier, stat.TpAtrMultiplier) =
            ExecutionTuningPolicy.ComputeStops(stat.TakeProfitHits, stat.StopLossHits);
        stat.LeverageFactor = ExecutionTuningPolicy.ComputeLeverageFactor(stat.Wins, stat.Losses);
        stat.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static bool IsBuyAction(DecisionAction action)
        => action is DecisionAction.WeakBuy or DecisionAction.Buy or DecisionAction.StrongBuy;
}
