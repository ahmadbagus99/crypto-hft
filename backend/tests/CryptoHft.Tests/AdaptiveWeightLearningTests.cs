using CryptoHft.Application.DecisionEngine;
using CryptoHft.Domain.Entities;
using CryptoHft.Domain.Enums;
using CryptoHft.Infrastructure.Ai;
using CryptoHft.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CryptoHft.Tests;

// Realized-outcome learning: closed positions from Position History are matched to the
// decisions that opened them and evaluated on realized PnL; the 60-minute price-movement
// horizon stays as fallback for decisions that never became a trade.
public sealed class AdaptiveWeightLearningTests
{
    private const string Symbol = "BTCUSDT";
    // Factor score 80 agrees with BUY (>=55); 20 agrees with SELL (<=45).
    private const string BullishScores = """{"technical":80}""";
    private const string BearishScores = """{"technical":20}""";

    private static (AdaptiveWeightService Service, ServiceProvider Provider) CreateService()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString("N");
        services.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var service = new AdaptiveWeightService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AdaptiveWeightService>.Instance);
        return (service, provider);
    }

    private static TradingDbContext Db(ServiceProvider provider)
        => provider.CreateScope().ServiceProvider.GetRequiredService<TradingDbContext>();

    private static AiDecisionLog Decision(DecisionAction action, DateTimeOffset createdAt, string scoresJson)
        => new()
        {
            Symbol = Symbol,
            Regime = 0,
            Action = action,
            Confidence = 85m,
            EntryPrice = 100_000m,
            ScoresJson = scoresJson,
            CreatedAt = createdAt
        };

    private static Position ClosedPosition(
        TradeSide side, DateTimeOffset openedAt, decimal realizedPnl, decimal roi)
        => new()
        {
            Symbol = Symbol,
            Side = side,
            Quantity = 0.001m,
            EntryPrice = 100_000m,
            MarkPrice = 101_000m,
            RealizedPnl = realizedPnl,
            Roi = roi,
            Leverage = 20,
            OpenedAt = openedAt,
            ClosedAt = openedAt.AddHours(2)
        };

    private static async Task<FactorStat> TechnicalStatAsync(ServiceProvider provider)
    {
        using var db = Db(provider);
        return await db.FactorStats.SingleAsync(s => s.Factor == "technical");
    }

    private static void AssertRecentEvidence(decimal expected, decimal actual)
        => Assert.InRange(actual, expected - 0.02m, expected + 0.001m);

    [Fact]
    public async Task ClosedWinningPosition_UpdatesAlpha_ForAgreeingFactor()
    {
        var (service, provider) = CreateService();
        var openedAt = DateTimeOffset.UtcNow.AddHours(-3);
        Guid decisionId;

        using (var db = Db(provider))
        {
            var decision = Decision(DecisionAction.Buy, openedAt.AddSeconds(-30), BullishScores);
            decisionId = decision.Id;
            db.AiDecisionLogs.Add(decision);
            db.Positions.Add(ClosedPosition(TradeSide.Long, openedAt, realizedPnl: 1.2m, roi: 0.4m));
            await db.SaveChangesAsync();
        }

        var matched = await service.EvaluateClosedPositionsAsync(CancellationToken.None);

        Assert.Equal(1, matched);
        var stat = await TechnicalStatAsync(provider);
        AssertRecentEvidence(1m + 1.4m, stat.Alpha); // ROI weight, slightly recency-decayed
        Assert.Equal(1m, stat.Beta);

        using var verify = Db(provider);
        var log = await verify.AiDecisionLogs.SingleAsync(d => d.Id == decisionId);
        Assert.True(log.Evaluated);
        Assert.True(log.Win);
        Assert.NotNull(log.MatchedPositionId);
    }

    [Fact]
    public async Task ClosedLosingShortPosition_UpdatesBeta_ForAgreeingFactor()
    {
        var (service, provider) = CreateService();
        var openedAt = DateTimeOffset.UtcNow.AddHours(-3);

        using (var db = Db(provider))
        {
            db.AiDecisionLogs.Add(Decision(DecisionAction.Sell, openedAt.AddSeconds(-30), BearishScores));
            db.Positions.Add(ClosedPosition(TradeSide.Short, openedAt, realizedPnl: -0.8m, roi: -0.25m));
            await db.SaveChangesAsync();
        }

        var matched = await service.EvaluateClosedPositionsAsync(CancellationToken.None);

        Assert.Equal(1, matched);
        var stat = await TechnicalStatAsync(provider);
        Assert.Equal(1m, stat.Alpha);
        AssertRecentEvidence(1m + 1.25m, stat.Beta); // loss weighted by 1 + |roi|
    }

    [Fact]
    public async Task BreakEvenPosition_CountsAsLoss()
    {
        var (service, provider) = CreateService();
        var openedAt = DateTimeOffset.UtcNow.AddHours(-3);

        using (var db = Db(provider))
        {
            db.AiDecisionLogs.Add(Decision(DecisionAction.Buy, openedAt.AddSeconds(-30), BullishScores));
            db.Positions.Add(ClosedPosition(TradeSide.Long, openedAt, realizedPnl: 0m, roi: 0m));
            await db.SaveChangesAsync();
        }

        await service.EvaluateClosedPositionsAsync(CancellationToken.None);

        var stat = await TechnicalStatAsync(provider);
        Assert.Equal(1m, stat.Alpha);
        AssertRecentEvidence(2m, stat.Beta); // realizedPnl <= 0 is a loss, weight floor 1
    }

    [Fact]
    public async Task ExtremeRoi_WeightIsClamped()
    {
        var (service, provider) = CreateService();
        var openedAt = DateTimeOffset.UtcNow.AddHours(-3);

        using (var db = Db(provider))
        {
            db.AiDecisionLogs.Add(Decision(DecisionAction.Buy, openedAt.AddSeconds(-30), BullishScores));
            db.Positions.Add(ClosedPosition(TradeSide.Long, openedAt, realizedPnl: 30m, roi: 8m));
            await db.SaveChangesAsync();
        }

        await service.EvaluateClosedPositionsAsync(CancellationToken.None);

        var stat = await TechnicalStatAsync(provider);
        AssertRecentEvidence(1m + 3m, stat.Alpha); // 1 + |8| clamped to max weight 3
    }

    [Fact]
    public async Task SamePosition_IsNotDoubleCounted()
    {
        var (service, provider) = CreateService();
        var openedAt = DateTimeOffset.UtcNow.AddHours(-3);

        using (var db = Db(provider))
        {
            db.AiDecisionLogs.Add(Decision(DecisionAction.Buy, openedAt.AddSeconds(-30), BullishScores));
            // A second unevaluated decision in the window must not be consumed on the re-run.
            db.AiDecisionLogs.Add(Decision(DecisionAction.Buy, openedAt.AddSeconds(-90), BullishScores));
            db.Positions.Add(ClosedPosition(TradeSide.Long, openedAt, realizedPnl: 1.2m, roi: 0.4m));
            await db.SaveChangesAsync();
        }

        var first = await service.EvaluateClosedPositionsAsync(CancellationToken.None);
        var second = await service.EvaluateClosedPositionsAsync(CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        var stat = await TechnicalStatAsync(provider);
        AssertRecentEvidence(1m + 1.4m, stat.Alpha); // single weighted update only
        Assert.Equal(1m, stat.Beta);
    }

    [Fact]
    public async Task MatchedDecision_IsNotReevaluatedByPriceFallback()
    {
        var (service, provider) = CreateService();
        var openedAt = DateTimeOffset.UtcNow.AddHours(-3);

        using (var db = Db(provider))
        {
            db.AiDecisionLogs.Add(Decision(DecisionAction.Buy, openedAt.AddSeconds(-30), BullishScores));
            db.Positions.Add(ClosedPosition(TradeSide.Long, openedAt, realizedPnl: 1.2m, roi: 0.4m));
            await db.SaveChangesAsync();
        }

        await service.EvaluateClosedPositionsAsync(CancellationToken.None);
        // A big favorable price would count as a fallback win — but the decision is already matched.
        var evaluated = await service.EvaluatePendingAsync(110_000m, CancellationToken.None);

        Assert.Equal(0, evaluated);
        var stat = await TechnicalStatAsync(provider);
        AssertRecentEvidence(1m + 1.4m, stat.Alpha); // unchanged by the fallback pass
        Assert.Equal(1m, stat.Beta);
    }

    [Fact]
    public async Task PriceFallback_StillEvaluates_UnmatchedDecision()
    {
        var (service, provider) = CreateService();

        using (var db = Db(provider))
        {
            // Actionable decision two hours old that never became a trade (no orders, no position).
            db.AiDecisionLogs.Add(Decision(DecisionAction.Buy, DateTimeOffset.UtcNow.AddHours(-2), BullishScores));
            await db.SaveChangesAsync();
        }

        // +1% favorable move clears the dead band; fallback is intentionally weak (0.25x).
        var evaluated = await service.EvaluatePendingAsync(101_000m, CancellationToken.None);

        Assert.Equal(1, evaluated);
        var stat = await TechnicalStatAsync(provider);
        AssertRecentEvidence(1m + AdaptiveLearningPolicy.FallbackEvidenceWeight, stat.Alpha);
        Assert.Equal(1m, stat.Beta);

        using var verify = Db(provider);
        var log = await verify.AiDecisionLogs.SingleAsync();
        Assert.True(log.Evaluated);
        Assert.True(log.Win);
        Assert.Null(log.MatchedPositionId);
    }

    [Fact]
    public async Task PriceFallback_DeduplicatesCorrelatedDecisionsWithinOneHour()
    {
        var (service, provider) = CreateService();
        var bucket = DateTimeOffset.UtcNow.AddHours(-3);
        bucket = new DateTimeOffset(bucket.Year, bucket.Month, bucket.Day, bucket.Hour, 0, 0, TimeSpan.Zero);

        using (var db = Db(provider))
        {
            db.AiDecisionLogs.AddRange(
                Decision(DecisionAction.Buy, bucket.AddMinutes(5), BullishScores),
                Decision(DecisionAction.Buy, bucket.AddMinutes(20), BullishScores),
                Decision(DecisionAction.Buy, bucket.AddMinutes(45), BullishScores));
            await db.SaveChangesAsync();
        }

        Assert.Equal(3, await service.EvaluatePendingAsync(101_000m, CancellationToken.None));

        var stat = await TechnicalStatAsync(provider);
        AssertRecentEvidence(1m + AdaptiveLearningPolicy.FallbackEvidenceWeight, stat.Alpha);
        Assert.Equal(1m, stat.Beta);
    }

    [Fact]
    public async Task PriceFallback_KeepsIndependentHourlyEvidence()
    {
        var (service, provider) = CreateService();
        var first = DateTimeOffset.UtcNow.AddHours(-4);
        first = new DateTimeOffset(first.Year, first.Month, first.Day, first.Hour, 10, 0, TimeSpan.Zero);

        using (var db = Db(provider))
        {
            db.AiDecisionLogs.AddRange(
                Decision(DecisionAction.Buy, first, BullishScores),
                Decision(DecisionAction.Buy, first.AddHours(1), BullishScores));
            await db.SaveChangesAsync();
        }

        Assert.Equal(2, await service.EvaluatePendingAsync(101_000m, CancellationToken.None));

        var stat = await TechnicalStatAsync(provider);
        AssertRecentEvidence(1m + AdaptiveLearningPolicy.FallbackEvidenceWeight * 2m, stat.Alpha);
    }

    [Fact]
    public async Task DeterministicRebuild_ReplacesLegacyInflatedCounters_AndIsIdempotent()
    {
        var (service, provider) = CreateService();
        var evaluatedAt = DateTimeOffset.UtcNow.AddHours(-1);

        using (var db = Db(provider))
        {
            var log = Decision(DecisionAction.Buy, DateTimeOffset.UtcNow.AddHours(-2), BullishScores);
            log.Evaluated = true;
            log.Win = true;
            log.PriceMovePercent = 1m;
            log.EvaluatedAt = evaluatedAt;
            db.AiDecisionLogs.Add(log);
            db.FactorStats.Add(new FactorStat
            {
                Regime = 0,
                Factor = "technical",
                Alpha = 100m,
                Beta = 1m
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await service.EvaluatePendingAsync(101_000m, CancellationToken.None));
        var first = await TechnicalStatAsync(provider);
        AssertRecentEvidence(1m + AdaptiveLearningPolicy.FallbackEvidenceWeight, first.Alpha);

        var alpha = first.Alpha;
        var beta = first.Beta;
        Assert.Equal(0, await service.EvaluatePendingAsync(101_000m, CancellationToken.None));
        var second = await TechnicalStatAsync(provider);
        Assert.InRange(Math.Abs(second.Alpha - alpha), 0m, 0.0001m);
        Assert.Equal(beta, second.Beta);
    }

    [Fact]
    public async Task RealizedPositionOutcome_OutweighsFallbackSnapshot()
    {
        var (service, provider) = CreateService();
        var now = DateTimeOffset.UtcNow;

        using (var db = Db(provider))
        {
            var position = ClosedPosition(TradeSide.Long, now.AddHours(-3), realizedPnl: -1m, roi: -0.4m);
            var realized = Decision(DecisionAction.Buy, position.OpenedAt.AddSeconds(-30), BullishScores);
            realized.Evaluated = true;
            realized.Win = false;
            realized.MatchedPositionId = position.Id;
            realized.EvaluatedAt = now.AddMinutes(-30);

            var fallback = Decision(DecisionAction.Buy, now.AddHours(-2), BullishScores);
            fallback.Evaluated = true;
            fallback.Win = true;
            fallback.PriceMovePercent = 1m;
            fallback.EvaluatedAt = now.AddMinutes(-20);

            db.Positions.Add(position);
            db.AiDecisionLogs.AddRange(realized, fallback);
            await db.SaveChangesAsync();
        }

        await service.EvaluatePendingAsync(101_000m, CancellationToken.None);

        var stat = await TechnicalStatAsync(provider);
        Assert.True(stat.Beta - 1m > (stat.Alpha - 1m) * 4m);
    }

    [Fact]
    public async Task BuyDecision_DoesNotMatch_ShortPosition()
    {
        var (service, provider) = CreateService();
        var openedAt = DateTimeOffset.UtcNow.AddHours(-3);

        using (var db = Db(provider))
        {
            db.AiDecisionLogs.Add(Decision(DecisionAction.Buy, openedAt.AddSeconds(-30), BullishScores));
            db.Positions.Add(ClosedPosition(TradeSide.Short, openedAt, realizedPnl: 1.2m, roi: 0.4m));
            await db.SaveChangesAsync();
        }

        var matched = await service.EvaluateClosedPositionsAsync(CancellationToken.None);

        Assert.Equal(0, matched);
        using var verify = Db(provider);
        var log = await verify.AiDecisionLogs.SingleAsync();
        Assert.False(log.Evaluated); // stays pending for the price fallback
        Assert.Empty(await verify.FactorStats.ToListAsync());
    }

    [Fact]
    public async Task SellDecision_Matches_ShortPosition()
    {
        var (service, provider) = CreateService();
        var openedAt = DateTimeOffset.UtcNow.AddHours(-3);

        using (var db = Db(provider))
        {
            db.AiDecisionLogs.Add(Decision(DecisionAction.StrongSell, openedAt.AddSeconds(-30), BearishScores));
            db.Positions.Add(ClosedPosition(TradeSide.Short, openedAt, realizedPnl: 0.5m, roi: 0.1m));
            await db.SaveChangesAsync();
        }

        var matched = await service.EvaluateClosedPositionsAsync(CancellationToken.None);

        Assert.Equal(1, matched);
        using var verify = Db(provider);
        var log = await verify.AiDecisionLogs.SingleAsync();
        Assert.True(log.Evaluated);
        Assert.True(log.Win);
        Assert.NotNull(log.MatchedPositionId);
    }

    [Fact]
    public async Task ExecutedDecision_IsDeferred_FromPriceFallback()
    {
        var (service, provider) = CreateService();
        var decidedAt = DateTimeOffset.UtcNow.AddHours(-2);

        using (var db = Db(provider))
        {
            // Decision opened a live trade (entry order seconds later); position still open,
            // so no row in Positions yet.
            db.AiDecisionLogs.Add(Decision(DecisionAction.Buy, decidedAt, BullishScores));
            db.Orders.Add(new Order
            {
                Symbol = Symbol,
                Side = TradeSide.Long,
                Kind = OrderKind.Market,
                Status = OrderStatus.Filled,
                Quantity = 0.001m,
                ReduceOnly = false,
                IsPaper = false,
                Reason = "AI auto Buy",
                CreatedAt = decidedAt.AddSeconds(10)
            });
            await db.SaveChangesAsync();
        }

        var evaluated = await service.EvaluatePendingAsync(110_000m, CancellationToken.None);

        Assert.Equal(0, evaluated); // waits for the realized outcome instead
        using var verify = Db(provider);
        var log = await verify.AiDecisionLogs.SingleAsync();
        Assert.False(log.Evaluated);
    }

    [Fact]
    public async Task ClosedPosition_UpdatesExecutionStats()
    {
        var (service, provider) = CreateService();
        var openedAt = DateTimeOffset.UtcNow.AddHours(-3);

        using (var db = Db(provider))
        {
            db.AiDecisionLogs.Add(Decision(DecisionAction.Buy, openedAt.AddSeconds(-30), BullishScores));
            var pos = ClosedPosition(TradeSide.Long, openedAt, realizedPnl: 1.2m, roi: 0.4m);
            pos.CloseReason = PositionCloseReason.TakeProfit;
            db.Positions.Add(pos);
            await db.SaveChangesAsync();
        }

        await service.EvaluateClosedPositionsAsync(CancellationToken.None);

        using var verify = Db(provider);
        var stat = await verify.ExecutionStats.SingleAsync();
        Assert.Equal(1, stat.Wins);
        Assert.Equal(0, stat.Losses);
        Assert.Equal(1, stat.TakeProfitHits);
        Assert.Equal(0, stat.StopLossHits);
        // Below the minimum sample count the learned values must stay at the defaults.
        Assert.Equal(2m, stat.SlAtrMultiplier);
        Assert.Equal(4m, stat.TpAtrMultiplier);
        Assert.Equal(1m, stat.LeverageFactor);
    }

    [Fact]
    public async Task ExecutionTuning_DefaultsWhenNoHistory()
    {
        var (service, _) = CreateService();
        var tuning = await service.GetExecutionTuningAsync(MarketRegime.Ranging, CancellationToken.None);
        Assert.Equal(ExecutionTuning.Default, tuning);
    }

    [Fact]
    public async Task LearningSnapshot_NullWhenNoRealizedData()
    {
        var (service, _) = CreateService();
        Assert.Null(await service.GetLearningSnapshotAsync(MarketRegime.Trending, CancellationToken.None));
    }

    private static AdvancedDecision EngineDecision(LlmValidation llm, decimal stopLoss, decimal takeProfit)
        => new(
            Symbol: Symbol,
            Action: DecisionAction.Buy,
            Confidence: 85m,
            ConfidenceBuy: 85m,
            ConfidenceSell: 15m,
            ConfidenceHold: 10m,
            ProbabilityOfSuccess: 80m,
            Regime: MarketRegime.Trending,
            EntryPrice: 100_000m,
            StopLoss: stopLoss,
            TakeProfit: takeProfit,
            TrailingStopPercent: 1m,
            RiskReward: 2m,
            PositionSizeQuantity: 0.001m,
            Leverage: 5,
            ShouldTrade: true,
            NoTradeReason: "",
            Cautions: Array.Empty<string>(),
            Scores: new Dictionary<string, decimal> { ["technical"] = 80m },
            Weights: new Dictionary<string, decimal>(),
            Components: Array.Empty<ScoreComponent>(),
            Reasons: Array.Empty<string>(),
            Llm: llm,
            Time: DateTimeOffset.UtcNow);

    [Fact]
    public async Task LogDecision_RecordsLlmVerdict()
    {
        var (service, provider) = CreateService();
        // Hesitant Claude: resized to 0.6x / 8x and its SL/TP pair was adopted by ApplyValidation.
        var llm = new LlmValidation(
            Confirmed: false, AdjustedConfidence: 70m, Narrative: "cautious", Risks: Array.Empty<string>(),
            Used: true, SizeMultiplier: 0.6m, Leverage: 8, StopLoss: 99_000m, TakeProfit: 103_000m);

        await service.LogDecisionAsync(EngineDecision(llm, stopLoss: 99_000m, takeProfit: 103_000m), CancellationToken.None);

        using var db = Db(provider);
        var log = await db.AiDecisionLogs.SingleAsync();
        Assert.False(log.LlmConfirmed);
        Assert.Equal(0.6m, log.LlmSizeMultiplier);
        Assert.Equal(8, log.LlmLeverage);
        Assert.True(log.LlmStopsApplied);
    }

    [Fact]
    public async Task LogDecision_LeavesLlmFieldsNull_WhenLlmNotUsed()
    {
        var (service, provider) = CreateService();
        // Rule-only decision (LLM skipped): the engine default carries Used = false.
        var llm = new LlmValidation(true, 85m, "", Array.Empty<string>(), Used: false);

        await service.LogDecisionAsync(EngineDecision(llm, stopLoss: 99_000m, takeProfit: 103_000m), CancellationToken.None);

        using var db = Db(provider);
        var log = await db.AiDecisionLogs.SingleAsync();
        Assert.Null(log.LlmConfirmed);
        Assert.Null(log.LlmSizeMultiplier);
        Assert.Null(log.LlmLeverage);
        Assert.Null(log.LlmStopsApplied);
    }

    [Fact]
    public async Task LogDecision_StopsNotApplied_WhenBaselineKept()
    {
        var (service, provider) = CreateService();
        // Claude proposed a pair but validation rejected it — final decision kept baseline stops.
        var llm = new LlmValidation(
            Confirmed: false, AdjustedConfidence: 70m, Narrative: "", Risks: Array.Empty<string>(),
            Used: true, SizeMultiplier: 1m, Leverage: null, StopLoss: 101_000m, TakeProfit: 99_500m);

        await service.LogDecisionAsync(EngineDecision(llm, stopLoss: 98_000m, takeProfit: 104_000m), CancellationToken.None);

        using var db = Db(provider);
        var log = await db.AiDecisionLogs.SingleAsync();
        Assert.False(log.LlmStopsApplied);
        Assert.Null(log.LlmLeverage); // null proposal stays null
    }

    [Fact]
    public async Task ValidationPerformance_GroupsRealizedOutcomesByVerdict()
    {
        var (service, provider) = CreateService();
        var openedAt = DateTimeOffset.UtcNow.AddHours(-5);

        using (var db = Db(provider))
        {
            // Confirmed decision -> winning position
            var winPos = ClosedPosition(TradeSide.Long, openedAt, realizedPnl: 2m, roi: 0.5m);
            var winLog = Decision(DecisionAction.Buy, openedAt.AddSeconds(-30), BullishScores);
            winLog.Evaluated = true;
            winLog.MatchedPositionId = winPos.Id;
            winLog.LlmConfirmed = true;
            winLog.LlmSizeMultiplier = 1.0m;

            // Hesitant decision -> losing position
            var lossPos = ClosedPosition(TradeSide.Long, openedAt.AddHours(1), realizedPnl: -1m, roi: -0.25m);
            var lossLog = Decision(DecisionAction.Buy, openedAt.AddHours(1).AddSeconds(-30), BullishScores);
            lossLog.Evaluated = true;
            lossLog.MatchedPositionId = lossPos.Id;
            lossLog.LlmConfirmed = false;
            lossLog.LlmSizeMultiplier = 0.5m;

            // Unmatched decision — must not appear in the realized slice
            db.AiDecisionLogs.AddRange(winLog, lossLog, Decision(DecisionAction.Buy, openedAt, BullishScores));
            db.Positions.AddRange(winPos, lossPos);
            await db.SaveChangesAsync();
        }

        var outcomes = await service.GetValidationPerformanceAsync(CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        var confirmed = Assert.Single(outcomes, o => o.Verdict == "confirmed");
        Assert.Equal(1, confirmed.Samples);
        Assert.Equal(100m, confirmed.WinRate);
        Assert.Equal(0.5m, confirmed.AvgRoi);
        var hesitant = Assert.Single(outcomes, o => o.Verdict == "hesitant");
        Assert.Equal(0m, hesitant.WinRate);
        Assert.Equal(-1m, hesitant.TotalRealizedPnl);
        Assert.Equal(0.5m, hesitant.AvgSizeMultiplier);
    }

    [Fact]
    public async Task OlderDecision_BeforeExecutedOne_StillGetsPriceFallback()
    {
        var (service, provider) = CreateService();
        var decidedAt = DateTimeOffset.UtcNow.AddHours(-2);
        Guid olderId;

        using (var db = Db(provider))
        {
            // Burst of actionable decisions; only the latest one before the order is the opener.
            var older = Decision(DecisionAction.Buy, decidedAt.AddMinutes(-2), BullishScores);
            olderId = older.Id;
            db.AiDecisionLogs.Add(older);
            db.AiDecisionLogs.Add(Decision(DecisionAction.Buy, decidedAt, BullishScores));
            db.Orders.Add(new Order
            {
                Symbol = Symbol,
                Side = TradeSide.Long,
                Kind = OrderKind.Market,
                Status = OrderStatus.Filled,
                Quantity = 0.001m,
                ReduceOnly = false,
                IsPaper = false,
                Reason = "AI auto Buy",
                CreatedAt = decidedAt.AddSeconds(10)
            });
            await db.SaveChangesAsync();
        }

        var evaluated = await service.EvaluatePendingAsync(101_000m, CancellationToken.None);

        Assert.Equal(1, evaluated); // only the non-opener decision
        using var verify = Db(provider);
        var older2 = await verify.AiDecisionLogs.SingleAsync(d => d.Id == olderId);
        var opener = await verify.AiDecisionLogs.SingleAsync(d => d.Id != olderId);
        Assert.True(older2.Evaluated);
        Assert.False(opener.Evaluated);
    }
}
