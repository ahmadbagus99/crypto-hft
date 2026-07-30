using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Risk;
using Xunit;

namespace CryptoHft.Tests;

// Covers the accuracy improvements to the rule engine: condition dampener (volatility and
// liquidity no longer cast directional votes), OI x price context, CVD, liquidation
// pressure, RSI divergence, directional regime split, factor inversion, recency decay,
// and the scheduled-event calendar.
public sealed class DecisionEngineImprovementTests
{
    // ---- ConditionDampener --------------------------------------------------------------

    [Theory]
    [InlineData(1.0, 0.0001, 1.0)]    // healthy vol, tight spread: full conviction
    [InlineData(4.0, 0.0001, 0.85)]   // hot tape
    [InlineData(6.0, 0.0001, 0.75)]   // extreme tape
    [InlineData(0.2, 0.0001, 0.92)]   // dead tape
    [InlineData(1.0, 0.001, 0.9)]     // wide spread only
    [InlineData(6.0, 0.001, 0.675)]   // extreme vol + wide spread: 0.75 * 0.9
    public void ConditionDampener_CompressesConviction(decimal atrPct, decimal spreadFraction, decimal expected)
        => Assert.Equal(expected, AdvancedDecisionEngine.ConditionDampener(atrPct, spreadFraction));

    [Fact]
    public void ConditionDampener_NeverBelowFloor()
        => Assert.True(AdvancedDecisionEngine.ConditionDampener(99m, 1m) >= 0.65m);

    // 0.4% is the exact boundary at which the regime detector starts calling the tape
    // LowVolatility, so the dampener must already be engaged there rather than one tick later.
    [Fact]
    public void ConditionDampener_DeadTapeBoundaryIsInclusive()
        => Assert.Equal(0.92m, AdvancedDecisionEngine.ConditionDampener(0.4m, 0.0001m));

    // ---- Liquidation pressure -----------------------------------------------------------

    [Fact]
    public void LiquidationPressure_QuietFeed_IsNeutral()
        => Assert.Equal(0m, AdvancedDecisionEngine.ScoreLiquidationPressure(200_000m, 100_000m));

    [Fact]
    public void LiquidationPressure_LongFlush_IsContrarianBullish()
        => Assert.Equal(10m, AdvancedDecisionEngine.ScoreLiquidationPressure(3_000_000m, 500_000m));

    [Fact]
    public void LiquidationPressure_ShortSqueeze_IsContrarianBearish()
        => Assert.Equal(-10m, AdvancedDecisionEngine.ScoreLiquidationPressure(500_000m, 3_000_000m));

    [Fact]
    public void LiquidationPressure_TwoSidedCascade_IsNeutral()
        => Assert.Equal(0m, AdvancedDecisionEngine.ScoreLiquidationPressure(2_000_000m, 2_000_000m));

    // ---- CVD ------------------------------------------------------------------------------

    private static Candle Bar(decimal close, decimal volume, decimal takerBuy)
        => new(DateTimeOffset.UtcNow, close, close + 1, close - 1, close, volume, takerBuy);

    [Fact]
    public void Cvd_WithoutTakerVolume_ReportsNoData()
    {
        var candles = Enumerable.Range(0, 20).Select(i => Bar(100 + i, 10m, 0m)).ToList();
        var (_, _, hasData) = AdvancedDecisionEngine.ComputeCvd(candles, window: 12);
        Assert.False(hasData);
    }

    [Fact]
    public void Cvd_NetBuying_YieldsPositiveRatio()
    {
        // 80% of volume is taker buys: delta = 0.8v*2 - v = 0.6v -> ratio 0.6
        var candles = Enumerable.Range(0, 20).Select(i => Bar(100 + i, 10m, 8m)).ToList();
        var (ratio, movePct, hasData) = AdvancedDecisionEngine.ComputeCvd(candles, window: 12);
        Assert.True(hasData);
        Assert.Equal(0.6m, ratio);
        Assert.True(movePct > 0);
    }

    [Fact]
    public void Cvd_NetSelling_YieldsNegativeRatio()
    {
        var candles = Enumerable.Range(0, 20).Select(i => Bar(100 + i, 10m, 2m)).ToList();
        var (ratio, _, _) = AdvancedDecisionEngine.ComputeCvd(candles, window: 12);
        Assert.Equal(-0.6m, ratio);
    }

    // ---- RSI divergence -------------------------------------------------------------------

    [Fact]
    public void RsiDivergence_HigherHighPrice_LowerRsi_IsBearish()
    {
        // Prior window peaks at 110 with RSI 70; recent window peaks higher (112) with RSI 60.
        var closes = new List<decimal> { 100, 104, 110, 106, 103, 105, 104, 106, 108, 112, 111, 110, 109, 110 };
        var rsi = new decimal[] { 50, 55, 70, 60, 52, 55, 54, 56, 58, 60, 57, 55, 54, 55 };
        Assert.Equal(-1, AdvancedDecisionEngine.DetectRsiDivergence(closes, rsi));
    }

    [Fact]
    public void RsiDivergence_LowerLowPrice_HigherRsi_IsBullish()
    {
        var closes = new List<decimal> { 110, 106, 100, 104, 107, 105, 106, 104, 102, 98, 99, 100, 101, 100 };
        var rsi = new decimal[] { 50, 45, 30, 40, 48, 45, 46, 44, 42, 40, 41, 42, 43, 42 };
        Assert.Equal(1, AdvancedDecisionEngine.DetectRsiDivergence(closes, rsi));
    }

    [Fact]
    public void RsiDivergence_ConfirmedMove_IsNone()
    {
        // Higher price high WITH higher RSI high: momentum confirms, no divergence.
        var closes = new List<decimal> { 100, 102, 104, 103, 105, 106, 107, 108, 110, 112, 111, 113, 114, 115 };
        var rsi = new decimal[] { 50, 52, 55, 54, 56, 58, 60, 62, 64, 66, 65, 67, 68, 70 };
        Assert.Equal(0, AdvancedDecisionEngine.DetectRsiDivergence(closes, rsi));
    }

    // ---- Regime split -----------------------------------------------------------------------

    private static List<Candle> Ramp(int count, decimal start, decimal step)
        => Enumerable.Range(0, count)
            .Select(i =>
            {
                var close = start + step * i;
                return new Candle(
                    DateTimeOffset.UtcNow.AddHours(-count + i),
                    close - step, close + Math.Abs(step) * 0.7m, close - Math.Abs(step) * 0.7m, close, 100m);
            })
            .ToList();

    [Fact]
    public void Detect_UpTrend_ReturnsTrendingUp()
        => Assert.Equal(MarketRegime.TrendingUp, MarketRegimeDetector.Detect(Ramp(120, 100m, 0.4m)));

    [Fact]
    public void Detect_DownTrend_ReturnsTrendingDown()
        => Assert.Equal(MarketRegime.TrendingDown, MarketRegimeDetector.Detect(Ramp(120, 148m, -0.4m)));

    // ---- Adaptive learning policy -------------------------------------------------------------

    [Fact]
    public void Decay_HalvesExcessEvidence_AtHalfLife()
    {
        var (alpha, beta) = AdaptiveLearningPolicy.Decay(3m, 1m, elapsedDays: 30m);
        Assert.Equal(2m, alpha); // 1 + (3-1) * 0.5
        Assert.Equal(1m, beta);  // prior mass never decays
    }

    [Fact]
    public void Decay_NoElapsedTime_IsIdentity()
        => Assert.Equal((3m, 2m), AdaptiveLearningPolicy.Decay(3m, 2m, 0m));

    [Theory]
    [InlineData(0.35, 20, true)]   // clearly anti-predictive with samples
    [InlineData(0.35, 10, false)]  // not enough samples yet
    [InlineData(0.45, 50, false)]  // mediocre but not invertible
    public void ShouldInvert_RequiresAccuracyAndSamples(decimal mean, decimal samples, bool expected)
        => Assert.Equal(expected, AdaptiveLearningPolicy.ShouldInvert(mean, samples));

    [Fact]
    public void ScoreVote_MidBand_Abstains()
        => Assert.False(AdaptiveLearningPolicy.TryScoreVote(50m, outcomeUp: true, out _));

    [Fact]
    public void ScoreVote_BearishVote_WinsWhenPriceFalls()
    {
        Assert.True(AdaptiveLearningPolicy.TryScoreVote(30m, outcomeUp: false, out var win));
        Assert.True(win);
    }

    [Fact]
    public void ScoreVote_BullishVote_LosesWhenPriceFalls()
    {
        Assert.True(AdaptiveLearningPolicy.TryScoreVote(70m, outcomeUp: false, out var win));
        Assert.False(win);
    }

    // ---- Economic event calendar ---------------------------------------------------------------

    [Fact]
    public void Calendar_InsideCpiWindow_ReturnsCaution()
        => Assert.NotNull(EconomicEventCalendar.GetActiveEventLabel(
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero))); // CPI at 12:30 UTC

    [Fact]
    public void Calendar_InsideFomcWindow_ReturnsCaution()
        => Assert.NotNull(EconomicEventCalendar.GetActiveEventLabel(
            new DateTimeOffset(2026, 7, 29, 18, 30, 0, TimeSpan.Zero))); // FOMC at 18:00 UTC

    [Fact]
    public void Calendar_QuietDay_ReturnsNull()
        => Assert.Null(EconomicEventCalendar.GetActiveEventLabel(
            new DateTimeOffset(2026, 7, 5, 12, 0, 0, TimeSpan.Zero)));

    // ---- Engine integration: inversion + non-directional exclusion -----------------------------

    private static AdvancedDecisionInput SyntheticInput()
    {
        var h1 = Ramp(250, 100_000m, 80m);
        var m15 = Ramp(250, 100_000m, 20m);
        var m5 = Ramp(250, 100_000m, 7m);
        return new AdvancedDecisionInput(
            "BTCUSDT",
            h1[^1].Close,
            new[] { new TimeframeData("1h", h1), new TimeframeData("15m", m15), new TimeframeData("5m", m5) },
            new DerivativesSnapshot(0.0001m, 1000m, 0m, 1m, 1m, 0m, 1m),
            new SentimentSnapshot(50m, 50m, "Neutral", 50, "Neutral", Array.Empty<string>()),
            new MacroSnapshot(50m, "", false),
            new OnchainSnapshot(50m, "", false));
    }

    private static readonly RiskProfile Profile = new(
        MaxDailyLoss: 5m,
        MaxConsecutiveLosses: 3,
        MaxOpenPositions: 1,
        MaxExposure: 1m,
        RiskPerTrade: 0.01m,
        MinimumRiskReward: 2m,
        AutoTradeConfidenceThreshold: 62m);

    [Fact]
    public void InvertedFactor_FlipsItsContribution()
    {
        var engine = new AdvancedDecisionEngine();
        var input = SyntheticInput();

        var baseline = engine.Evaluate(input, Profile, 10_000m);
        var inverted = engine.Evaluate(input, Profile, 10_000m,
            new Dictionary<string, FactorAdjustment> { ["technical"] = new(1m, Inverted: true) });

        // The uptrend makes "technical" bullish; folding it must pull ConfidenceBuy down.
        Assert.True(baseline.ConfidenceBuy > inverted.ConfidenceBuy);
    }

    [Fact]
    public void VolatilityAndLiquidity_CarryNoDirectionalWeight()
    {
        var engine = new AdvancedDecisionEngine();
        var decision = engine.Evaluate(SyntheticInput(), Profile, 10_000m);

        Assert.Equal(0m, decision.Weights.GetValueOrDefault("volatility", 0m));
        Assert.Equal(0m, decision.Weights.GetValueOrDefault("liquidity", 0m));
        // Still scored for the dashboard and the LLM payload.
        Assert.True(decision.Scores.ContainsKey("volatility"));
        Assert.True(decision.Scores.ContainsKey("liquidity"));
    }

    [Fact]
    public void Engine_IncludesLevelAnalysisComponents()
    {
        var engine = new AdvancedDecisionEngine();
        var decision = engine.Evaluate(SyntheticInput(), Profile, 10_000m);

        // The level analyses vote inside the structure category and must always be
        // present (neutral 50 when they have nothing to say), so the dashboard and the
        // LLM payload can explain what each one saw.
        Assert.Contains(decision.Components, c => c.Name == "Fibonacci");
        Assert.Contains(decision.Components, c => c.Name == "Pattern");
        Assert.Contains(decision.Components, c => c.Name == "SupportResistance");
    }

    // ---- Trading style profile -----------------------------------------------------------------

    [Fact]
    public void ScalperStyle_AnchorsOn15mAndUsesScalperGeometry()
    {
        var engine = new AdvancedDecisionEngine();

        var decision = engine.Evaluate(
            SyntheticInput(), Profile, 10_000m,
            styleProfile: TradingStyleProfile.Scalper);

        Assert.Contains(decision.Reasons, r => r.Contains("style scalper: primary TF 15m"));
        Assert.Contains(decision.Reasons, r => r.Contains("SL 1.5x / TP 4.5x ATR"));
    }

    [Fact]
    public void ScalperStyle_IgnoresLearnedIntradayTuning()
    {
        var engine = new AdvancedDecisionEngine();
        var learned = new ExecutionTuning(2.6m, 3.2m, 0.5m);

        var intraday = engine.Evaluate(SyntheticInput(), Profile, 10_000m, tuning: learned);
        var scalper = engine.Evaluate(SyntheticInput(), Profile, 10_000m, tuning: learned,
            styleProfile: TradingStyleProfile.Scalper);

        Assert.Contains(intraday.Reasons, r => r.Contains("SL 2.6x / TP 3.2x ATR"));
        Assert.Contains(scalper.Reasons, r => r.Contains("SL 1.5x / TP 4.5x ATR"));
    }

    [Fact]
    public void DefaultStyle_IsIntradayOn1h()
    {
        var engine = new AdvancedDecisionEngine();
        var decision = engine.Evaluate(SyntheticInput(), Profile, 10_000m);

        Assert.Contains(decision.Reasons, r => r.Contains("style intraday: primary TF 1h"));
    }

    // ---- OI thresholds sit inside the range the series actually visits -------------------------

    // Regression for a threshold that could never fire: hourly BTCUSDT open interest moves
    // a median of 0.209% and reached 1.049% at most over 500 buckets, so the old 1.5% bar
    // left the whole derivatives category pinned at neutral.
    [Fact]
    public void OiThresholds_AreReachableByTheMeasuredDistribution()
    {
        const decimal measuredHourlyMedian = 0.209m;
        const decimal measuredHourlyP90 = 0.606m;

        Assert.True(AdvancedDecisionEngine.OiSignificantChangePercent > measuredHourlyMedian,
            "a threshold below the median would fire on more than half of all ticks");
        Assert.True(AdvancedDecisionEngine.OiSignificantChangePercent <= measuredHourlyP90,
            "a threshold above the p90 fires too rarely to inform anything");
        Assert.True(AdvancedDecisionEngine.OiStrongChangePercent > AdvancedDecisionEngine.OiSignificantChangePercent);
    }

    // ---- Fee-aware risk/reward -----------------------------------------------------------------

    [Fact]
    public void RiskReward_IsNetOfTheRoundTripFee()
    {
        var engine = new AdvancedDecisionEngine();
        var decision = engine.Evaluate(SyntheticInput(), Profile, 10_000m);

        var grossReward = Math.Abs(decision.TakeProfit - decision.EntryPrice);
        var grossRisk = Math.Abs(decision.EntryPrice - decision.StopLoss);
        var fee = decision.EntryPrice * AdvancedDecisionEngine.RoundTripFeeFraction;
        var expected = Math.Round((grossReward - fee) / (grossRisk + fee), 2);

        Assert.Equal(expected, decision.RiskReward);
        // The fee always costs something, so the reported figure must sit below the gross one.
        Assert.True(decision.RiskReward < grossReward / grossRisk);
    }

    // A scalper target must clear the fee by enough that the exchange is not the main
    // beneficiary: 1.5x/4.5x ATR is the geometry that keeps the NET ratio near 2:1.
    [Fact]
    public void ScalperGeometry_KeepsNetRiskRewardNearTwo()
    {
        const decimal atr = 128m;      // ~ATR(15m) on BTC around 64.8k
        const decimal entry = 64_800m;
        var risk = atr * TradingStyleProfile.Scalper.FallbackSlAtrMultiplier;
        var reward = atr * TradingStyleProfile.Scalper.FallbackTpAtrMultiplier;
        var fee = entry * AdvancedDecisionEngine.RoundTripFeeFraction;

        var netRr = (reward - fee) / (risk + fee);

        Assert.InRange(netRr, 1.8m, 2.2m);
    }
}
