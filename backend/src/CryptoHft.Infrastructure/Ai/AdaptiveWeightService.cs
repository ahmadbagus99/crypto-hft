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
// horizon.
//
// Factors are judged on their OWN directional call versus the realized market direction
// (AdaptiveLearningPolicy): a factor that voted bearish while the trade was long still earns
// credit if price fell. This measures directional ACCURACY, so consistently wrong-way factors
// accumulate evidence and are eventually inverted by the engine instead of never being scored.
// FactorStats is rebuilt deterministically from independent evidence: every matched Position
// History outcome is retained, while unmatched price-fallback snapshots are sampled once per
// one-hour symbol/regime/direction bucket at 0.25x weight. This prevents a 30-second analysis
// loop from manufacturing hundreds of correlated "samples" from one market move. Evidence
// decays with a 30-day half-life, and non-directional gauges are excluded entirely.
public sealed class AdaptiveWeightService(
    IServiceScopeFactory scopeFactory,
    ILogger<AdaptiveWeightService> logger) : IAdaptiveWeightService
{
    private static readonly TimeSpan EvaluationHorizon = TimeSpan.FromMinutes(60);
    private const decimal WinThresholdPercent = 0.30m; // fallback only: favorable move >= 0.3% counts as a win

    // Category baselines: how far back to look, and how long a computed set stays good.
    // The centre of an input's distribution moves on the scale of weeks, so recomputing
    // it more often than hourly only costs queries.
    private static readonly TimeSpan BaselineLookback = TimeSpan.FromDays(30);
    private static readonly TimeSpan BaselineCacheFor = TimeSpan.FromHours(1);
    // Decisions are logged every 30 seconds, so a single long one-sided stretch would
    // otherwise supply thousands of near-identical readings and drag the median with it.
    // One sample per hour keeps the baseline a description of the input, not of how long
    // the market happened to sit somewhere.
    private const int BaselineMaxRows = 60_000;
    private IReadOnlyDictionary<string, decimal>? _baselines;
    private DateTimeOffset _baselinesAt;
    private readonly SemaphoreSlim _baselineLock = new(1, 1);

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

    // Observed centre of each category's own score distribution. Empty until there is
    // enough independent history, which leaves the engine on its original fixed neutral.
    public async Task<IReadOnlyDictionary<string, decimal>> GetCategoryBaselinesAsync(
        CancellationToken cancellationToken)
    {
        if (_baselines is not null && DateTimeOffset.UtcNow - _baselinesAt < BaselineCacheFor)
            return _baselines;

        await _baselineLock.WaitAsync(cancellationToken);
        try
        {
            if (_baselines is not null && DateTimeOffset.UtcNow - _baselinesAt < BaselineCacheFor)
                return _baselines;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var since = DateTimeOffset.UtcNow - BaselineLookback;

            var rows = await db.AiDecisionLogs
                .Where(d => d.CreatedAt >= since && d.ScoresJson != null)
                .OrderBy(d => d.CreatedAt)
                .Select(d => new { d.CreatedAt, d.ScoresJson })
                .Take(BaselineMaxRows)
                .ToListAsync(cancellationToken);

            // One reading per hour: duration must not vote.
            var samples = new Dictionary<string, List<decimal>>();
            var seenHours = new HashSet<long>();
            foreach (var row in rows)
            {
                var hour = row.CreatedAt.ToUnixTimeSeconds() / 3600;
                if (!seenHours.Add(hour)) continue;
                Dictionary<string, decimal>? parsed;
                try { parsed = JsonSerializer.Deserialize<Dictionary<string, decimal>>(row.ScoresJson!); }
                catch { continue; }
                if (parsed is null) continue;
                foreach (var (key, value) in parsed)
                {
                    if (!AdvancedDecisionEngine.DirectionalCategories.Contains(key)) continue;
                    if (!samples.TryGetValue(key, out var list)) samples[key] = list = new List<decimal>();
                    list.Add(value);
                }
            }

            var result = new Dictionary<string, decimal>();
            foreach (var (key, values) in samples)
            {
                if (CategoryBaseline.ComputeBaseline(values) is decimal median)
                    result[key] = median;
            }

            if (result.Count > 0)
            {
                logger.LogInformation(
                    "Category baselines from {Hours} independent hours: {Baselines}",
                    seenHours.Count,
                    string.Join(", ", result.Select(kv => $"{kv.Key}={kv.Value:F1}")));
            }

            _baselines = result;
            _baselinesAt = DateTimeOffset.UtcNow;
            return result;
        }
        finally
        {
            _baselineLock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, FactorAdjustment>> GetFactorAdjustmentsAsync(
        MarketRegime regime, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var stats = (await db.FactorStats
                .Where(s => s.Regime == (int)regime)
                .ToListAsync(cancellationToken))
            .Where(s => AdvancedDecisionEngine.DirectionalCategories.Contains(s.Factor))
            .ToList();

        if (stats.Count == 0) return new Dictionary<string, FactorAdjustment>();

        // Posterior mean directional accuracy per factor. An anti-predictive factor is
        // flagged inverted; its EFFECTIVE accuracy (after the engine folds the score) is
        // 1 - mean, which is what the weight multiplier should reflect.
        var effective = stats.ToDictionary(
            s => s.Factor,
            s =>
            {
                var mean = s.Alpha / (s.Alpha + s.Beta);
                var inverted = AdaptiveLearningPolicy.ShouldInvert(mean, s.Alpha + s.Beta - 2m);
                return (Mean: inverted ? 1m - mean : mean, Inverted: inverted);
            });
        var avg = effective.Values.Average(v => v.Mean);
        if (avg <= 0) return new Dictionary<string, FactorAdjustment>();

        // Multiplier relative to the average performer (clamped later by the engine)
        return effective.ToDictionary(
            kv => kv.Key,
            kv => new FactorAdjustment(Math.Round(kv.Value.Mean / avg, 3), kv.Value.Inverted));
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
            LlmStopsApplied = llmUsed ? stopsApplied : null,
            LlmAdjustedConfidence = llmUsed ? llm.AdjustedConfidence : null,
            LlmAlignedCount = llmUsed ? llm.AlignedCount : null,
            LlmBlockingCount = llmUsed ? llm.BlockingCount : null
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

            ApplyExecutionOutcome(db, execCache, log.Regime, position, win);
            matched++;

            logger.LogInformation(
                "Adaptive learning: closed {Side} {Symbol} realizedPnl={Pnl:F4} roi={Roi:P2} -> decision {DecisionId} {Outcome} (weight {Weight:F2})",
                position.Side, position.Symbol, position.RealizedPnl, position.Roi, log.Id,
                win ? "WIN" : "LOSS", weight);
        }

        if (matched > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await RebuildFactorStatsAsync(db, DateTimeOffset.UtcNow, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
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

        if (pending.Count == 0)
        {
            // Reconcile legacy/inflated counters after a deployment even when there is no new
            // pending decision in this tick.
            await RebuildFactorStatsAsync(db, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return 0;
        }

        // Decisions that actually opened a live trade are NOT judged on the 60-minute price
        // move — their realized outcome arrives via EvaluateClosedPositionsAsync when the
        // position closes. Only the single decision that immediately preceded each live entry
        // order counts as executed (older same-direction decisions keep the price fallback).
        var executedIds = await ResolveExecutedDecisionIdsAsync(db, pending, cancellationToken);

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

            evaluated++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await RebuildFactorStatsAsync(db, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (evaluated > 0)
            logger.LogInformation("Adaptive learning evaluated {Count} decisions (price fallback)", evaluated);
        return evaluated;
    }

    public async Task<IReadOnlyList<FactorPerformance>> GetPerformanceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var stats = (await db.FactorStats.ToListAsync(cancellationToken))
            .Where(s => AdvancedDecisionEngine.DirectionalCategories.Contains(s.Factor))
            .ToList();

        var byRegime = stats.GroupBy(s => s.Regime);
        var result = new List<FactorPerformance>();
        foreach (var group in byRegime)
        {
            // Effective accuracy (post-inversion) drives the multiplier, matching the engine;
            // WinRate reports the RAW directional accuracy so an inverted factor is visible.
            var effective = group.ToDictionary(
                s => s.Factor,
                s =>
                {
                    var mean = s.Alpha / (s.Alpha + s.Beta);
                    return AdaptiveLearningPolicy.ShouldInvert(mean, s.Alpha + s.Beta - 2m)
                        ? 1m - mean
                        : mean;
                });
            var avg = effective.Values.Count > 0 ? effective.Values.Average() : 1m;
            foreach (var s in group)
            {
                var mean = s.Alpha / (s.Alpha + s.Beta);
                var samples = (int)(s.Alpha + s.Beta - 2m);
                var inverted = AdaptiveLearningPolicy.ShouldInvert(mean, s.Alpha + s.Beta - 2m);
                result.Add(new FactorPerformance(
                    s.Factor, s.Regime, Math.Round(mean * 100m, 1),
                    Math.Max(0, samples),
                    avg <= 0 ? 1m : Math.Round(effective[s.Factor] / avg, 3),
                    inverted));
            }
        }
        return result.OrderByDescending(r => r.WinRate).ToList();
    }

    // Confidence calibration curve: realized winrate per 5-point confidence bucket over
    // decisions matched to closed positions. When enough samples exist this answers
    // whether "confidence 70" really wins ~70% — the basis for threshold tuning.
    public async Task<IReadOnlyList<CalibrationBucket>> GetConfidenceCalibrationAsync(
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var rows = await (
                from d in db.AiDecisionLogs
                where d.MatchedPositionId != null
                join p in db.Positions on d.MatchedPositionId equals p.Id
                select new { d.Confidence, p.Roi, p.RealizedPnl })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => (int)(Math.Clamp(r.Confidence, 0m, 99.9m) / 5m) * 5)
            .Select(g =>
            {
                var wins = g.Count(r => r.RealizedPnl > 0m);
                return new CalibrationBucket(
                    BucketFloor: g.Key,
                    Samples: g.Count(),
                    Wins: wins,
                    WinRate: Math.Round(wins * 100m / g.Count(), 1),
                    AvgRoi: Math.Round(g.Average(r => r.Roi), 4),
                    TotalRealizedPnl: Math.Round(g.Sum(r => r.RealizedPnl), 4));
            })
            .OrderBy(b => b.BucketFloor)
            .ToList();
    }

    // Learned execution baselines for a regime. A regime without enough realized exits of
    // its own borrows the pooled cross-regime counters (ExecutionTuningPolicy.Resolve*);
    // defaults only remain until the account's first exits exist anywhere at all.
    public async Task<ExecutionTuning> GetExecutionTuningAsync(MarketRegime regime, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var stats = await db.ExecutionStats.AsNoTracking().ToListAsync(cancellationToken);
        return ResolveTuning(stats, regime);
    }

    private static ExecutionTuning ResolveTuning(IReadOnlyList<ExecutionStat> stats, MarketRegime regime)
    {
        if (stats.Count == 0) return ExecutionTuning.Default;

        var own = stats.FirstOrDefault(s => s.Regime == (int)regime);
        var (sl, tp) = ExecutionTuningPolicy.ResolveStops(
            own?.TakeProfitHits ?? 0, own?.StopLossHits ?? 0,
            stats.Sum(s => s.TakeProfitHits), stats.Sum(s => s.StopLossHits));
        var leverageFactor = ExecutionTuningPolicy.ResolveLeverageFactor(
            own?.Wins ?? 0, own?.Losses ?? 0,
            stats.Sum(s => s.Wins), stats.Sum(s => s.Losses));
        return new ExecutionTuning(sl, tp, leverageFactor);
    }

    // Compact realized-history digest for the LLM prompt. Null when nothing realized yet,
    // so the prompt stays byte-identical to the pre-learning version (fail-safe).
    public async Task<LearningSnapshot?> GetLearningSnapshotAsync(MarketRegime regime, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var stats = await db.ExecutionStats.AsNoTracking().ToListAsync(cancellationToken);
        var stat = stats.FirstOrDefault(s => s.Regime == (int)regime);
        var validation = await GetValidationPerformanceAsync(cancellationToken);

        if (stats.Count == 0 && validation.Count == 0) return null;

        // Report the baselines actually applied to this proposal — the pooled fallback
        // included — so Claude's digest never contradicts the execution.
        var tuning = ResolveTuning(stats, regime);
        var trades = stat is null ? 0 : stat.Wins + stat.Losses;
        return new LearningSnapshot(
            RegimeTrades: trades,
            RegimeWinRate: trades == 0 ? 0m : Math.Round(stat!.Wins * 100m / trades, 1),
            TakeProfitHits: stat?.TakeProfitHits ?? 0,
            StopLossHits: stat?.StopLossHits ?? 0,
            SlAtrMultiplier: tuning.SlAtrMultiplier,
            TpAtrMultiplier: tuning.TpAtrMultiplier,
            LeverageFactor: tuning.LeverageFactor,
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

    // Rebuild from source evidence instead of incrementing opaque counters. This makes the
    // result idempotent, allows legacy over-counting to repair itself automatically, and keeps
    // Position History outcomes stronger than correlated fallback snapshots.
    private static async Task RebuildFactorStatsAsync(
        TradingDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-AdaptiveLearningPolicy.EvidenceRetentionDays);
        var logs = await db.AiDecisionLogs
            .AsNoTracking()
            .Where(d => d.Evaluated && d.CreatedAt >= cutoff)
            .ToListAsync(cancellationToken);

        var matchedPositionIds = logs
            .Where(d => d.MatchedPositionId is not null)
            .Select(d => d.MatchedPositionId!.Value)
            .Distinct()
            .ToList();
        var positions = matchedPositionIds.Count == 0
            ? new Dictionary<Guid, Position>()
            : await db.Positions
                .AsNoTracking()
                .Where(p => matchedPositionIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, cancellationToken);

        var evidence = new List<FactorEvidence>();
        foreach (var log in logs.Where(d => d.MatchedPositionId is not null))
        {
            if (!positions.TryGetValue(log.MatchedPositionId!.Value, out var position)) continue;

            var win = position.RealizedPnl > 0m;
            var outcomeUp = (position.Side == TradeSide.Long) == win;
            var baseWeight = Math.Clamp(1m + Math.Abs(position.Roi), 1m, MaxOutcomeWeight);
            var occurredAt = position.ClosedAt ?? log.EvaluatedAt ?? log.CreatedAt;
            evidence.Add(new FactorEvidence(
                log.Regime,
                log.ScoresJson,
                outcomeUp,
                baseWeight * AdaptiveLearningPolicy.RecencyMultiplier(occurredAt, now)));
        }

        var fallback = logs
            .Where(d => d.MatchedPositionId is null
                        && Math.Abs(d.PriceMovePercent) >= AdaptiveLearningPolicy.FallbackTeachDeadBandPercent)
            .GroupBy(d => new FallbackEvidenceKey(
                d.Symbol,
                d.Regime,
                IsBuyAction(d.Action),
                d.CreatedAt.ToUnixTimeSeconds()
                / (AdaptiveLearningPolicy.FallbackIndependenceMinutes * 60L)))
            .Select(group => group.OrderByDescending(d => d.CreatedAt).First());

        foreach (var log in fallback)
        {
            var occurredAt = log.EvaluatedAt ?? log.CreatedAt;
            evidence.Add(new FactorEvidence(
                log.Regime,
                log.ScoresJson,
                log.PriceMovePercent > 0m,
                AdaptiveLearningPolicy.FallbackEvidenceWeight
                * AdaptiveLearningPolicy.RecencyMultiplier(occurredAt, now)));
        }

        var totals = new Dictionary<(int Regime, string Factor), FactorEvidenceTotal>();
        foreach (var item in evidence)
        {
            Dictionary<string, decimal> scores;
            try { scores = JsonSerializer.Deserialize<Dictionary<string, decimal>>(item.ScoresJson) ?? new(); }
            catch { continue; }

            foreach (var (factor, score) in scores)
            {
                if (!AdvancedDecisionEngine.DirectionalCategories.Contains(factor)) continue;
                if (!AdaptiveLearningPolicy.TryScoreVote(score, item.OutcomeUp, out var factorWin)) continue;

                var key = (item.Regime, factor);
                if (!totals.TryGetValue(key, out var total))
                    total = new FactorEvidenceTotal();
                if (factorWin) total.Alpha += item.Weight;
                else total.Beta += item.Weight;
                totals[key] = total;
            }
        }

        var existing = await db.FactorStats.ToListAsync(cancellationToken);
        foreach (var stat in existing)
        {
            if (!totals.Remove((stat.Regime, stat.Factor), out var total))
            {
                db.FactorStats.Remove(stat);
                continue;
            }

            stat.Alpha = 1m + total.Alpha;
            stat.Beta = 1m + total.Beta;
            stat.UpdatedAt = now;
        }

        foreach (var ((regime, factor), total) in totals)
        {
            db.FactorStats.Add(new FactorStat
            {
                Regime = regime,
                Factor = factor,
                Alpha = 1m + total.Alpha,
                Beta = 1m + total.Beta,
                UpdatedAt = now
            });
        }
    }

    private sealed record FactorEvidence(int Regime, string ScoresJson, bool OutcomeUp, decimal Weight);
    private sealed record FallbackEvidenceKey(string Symbol, int Regime, bool IsBuy, long Window);
    private sealed class FactorEvidenceTotal
    {
        public decimal Alpha { get; set; }
        public decimal Beta { get; set; }
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
        // Other close reasons (trailing/auto/manual/unknown) still teach win/loss for leverage,
        // but never distort the SL/TP geometry counters — a ratcheted trailing exit says nothing
        // about whether the ORIGINAL stop geometry was right.

        (stat.SlAtrMultiplier, stat.TpAtrMultiplier) =
            ExecutionTuningPolicy.ComputeStops(stat.TakeProfitHits, stat.StopLossHits);
        stat.LeverageFactor = ExecutionTuningPolicy.ComputeLeverageFactor(stat.Wins, stat.Losses);
        stat.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static bool IsBuyAction(DecisionAction action)
        => action is DecisionAction.WeakBuy or DecisionAction.Buy or DecisionAction.StrongBuy;
}
