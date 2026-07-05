using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Risk;
using CryptoHft.Infrastructure.Ai;
using Xunit;

namespace CryptoHft.Tests;

// Covers the multi-timeframe voting consensus (higher timeframes outvote lower-timeframe
// noise) and the live economic calendar (Forex Factory feed parsing + static fallback
// window logic + engine caution passthrough).
public sealed class MtfConsensusAndCalendarTests
{
    // ---- Forex Factory feed parsing -------------------------------------------------------

    private const string SampleFeed = """
    [
      {"title":"CPI m/m","country":"USD","date":"2026-07-14T08:30:00-04:00","impact":"High","forecast":"0.2%","previous":"0.3%"},
      {"title":"Retail Sales m/m","country":"USD","date":"2026-07-16T08:30:00-04:00","impact":"Medium"},
      {"title":"Main Refinancing Rate","country":"EUR","date":"2026-07-16T08:15:00-04:00","impact":"High"},
      {"title":"Bank Holiday","country":"USD","date":"2026-07-17T00:00:00-04:00","impact":"Holiday"}
    ]
    """;

    [Fact]
    public void FeedParser_KeepsOnlyHighImpactUsd()
    {
        var events = ForexFactoryCalendarProvider.ParseHighImpactUsdEvents(SampleFeed);
        var evt = Assert.Single(events);
        Assert.Contains("CPI m/m", evt.Label);
        // 08:30 ET (EDT, UTC-4) -> 12:30 UTC
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 12, 30, 0, TimeSpan.Zero), evt.TimeUtc);
    }

    [Fact]
    public void FeedParser_EmptyOrMalformedItems_AreSkipped()
    {
        var events = ForexFactoryCalendarProvider.ParseHighImpactUsdEvents(
            """[{"title":"No date","country":"USD","impact":"High"},{"country":"USD","impact":"High","date":"not-a-date"}]""");
        Assert.Empty(events);
    }

    [Fact]
    public void ActiveLabel_UsesParsedFeedEvents()
    {
        var events = ForexFactoryCalendarProvider.ParseHighImpactUsdEvents(SampleFeed);
        // 11:45 UTC is inside the 60-minute pre-window of the 12:30 UTC CPI print.
        var label = EconomicEventCalendar.ActiveLabel(events, new DateTimeOffset(2026, 7, 14, 11, 45, 0, TimeSpan.Zero));
        Assert.NotNull(label);
        Assert.Contains("CPI", label);
        // Two hours later the window has passed.
        Assert.Null(EconomicEventCalendar.ActiveLabel(events, new DateTimeOffset(2026, 7, 14, 14, 0, 0, TimeSpan.Zero)));
    }

    // ---- Engine caution passthrough --------------------------------------------------------

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

    private static readonly RiskProfile Profile = new(
        MaxDailyLoss: 5m,
        MaxConsecutiveLosses: 3,
        MaxOpenPositions: 1,
        MaxExposure: 1m,
        RiskPerTrade: 0.01m,
        MinimumRiskReward: 2m,
        AutoTradeConfidenceThreshold: 62m);

    private static AdvancedDecisionInput Input(
        IReadOnlyList<TimeframeData> timeframes, string? activeEventWindow = null)
        => new(
            "BTCUSDT",
            timeframes[0].Candles[^1].Close,
            timeframes,
            new DerivativesSnapshot(0.0001m, 1000m, 0m, 1m, 1m, 0m, 1m),
            new SentimentSnapshot(50m, 50m, "Neutral", 50, "Neutral", Array.Empty<string>()),
            new MacroSnapshot(50m, "", false),
            new OnchainSnapshot(50m, "", false),
            activeEventWindow);

    [Fact]
    public void ActiveEventWindow_SurfacesAsCaution()
    {
        var engine = new AdvancedDecisionEngine();
        var tfs = new[] { new TimeframeData("1h", Ramp(250, 100_000m, 80m)) };

        var quiet = engine.Evaluate(Input(tfs), Profile, 10_000m);
        var eventful = engine.Evaluate(Input(tfs, "US CPI at 12:30 UTC — scheduled-event volatility window"), Profile, 10_000m);

        Assert.DoesNotContain(quiet.Cautions, c => c.Contains("volatility window"));
        Assert.Contains(eventful.Cautions, c => c.Contains("volatility window"));
    }

    // ---- Multi-timeframe consensus -----------------------------------------------------------

    private static IReadOnlyList<TimeframeData> MixedTimeframes(bool higherTfBullish)
    {
        // Higher timeframes (1h/4h/1d, combined weight 0.70) trend one way; the lower
        // timeframes (5m/15m, weight 0.30) trend the other way.
        var upper = higherTfBullish ? 60m : -60m;
        var lower = higherTfBullish ? -8m : 8m;
        return
        [
            new TimeframeData("5m", Ramp(250, 100_000m, lower)),
            new TimeframeData("15m", Ramp(250, 100_000m, lower * 2)),
            new TimeframeData("1h", Ramp(250, higherTfBullish ? 90_000m : 115_000m, upper)),
            new TimeframeData("4h", Ramp(250, higherTfBullish ? 80_000m : 125_000m, upper * 2)),
            new TimeframeData("1d", Ramp(250, higherTfBullish ? 60_000m : 145_000m, upper * 3))
        ];
    }

    private static decimal TrendScore(AdvancedDecision decision)
        => decision.Components.First(c => c.Name == "Trend").Score;

    [Fact]
    public void HigherTimeframes_OutvoteLowerTimeframeNoise()
    {
        var engine = new AdvancedDecisionEngine();

        var bullish = engine.Evaluate(Input(MixedTimeframes(higherTfBullish: true)), Profile, 10_000m);
        var bearish = engine.Evaluate(Input(MixedTimeframes(higherTfBullish: false)), Profile, 10_000m);

        // Consensus must land on the higher-timeframe side despite lower-TF disagreement.
        Assert.True(TrendScore(bullish) > 55m, $"expected bullish consensus, got {TrendScore(bullish)}");
        Assert.True(TrendScore(bearish) < 45m, $"expected bearish consensus, got {TrendScore(bearish)}");
    }

    [Fact]
    public void DisagreeingTimeframes_SurfaceVoteDetailInCaution()
    {
        var engine = new AdvancedDecisionEngine();
        var decision = engine.Evaluate(Input(MixedTimeframes(higherTfBullish: true)), Profile, 10_000m);

        // Whenever the trade side conflicts with part of the stack, the caution lists votes.
        if (decision.Cautions.Any(c => c.Contains("not aligned")))
            Assert.Contains(decision.Cautions, c => c.Contains("trend votes:"));
        // The Trend component always exposes the per-timeframe votes for the dashboard.
        var trend = decision.Components.First(c => c.Name == "Trend");
        Assert.Contains("5m", trend.Reason);
        Assert.Contains("1d", trend.Reason);
    }

    [Fact]
    public void SingleTimeframe_StillProducesConsensus()
    {
        var votes = AdvancedDecisionEngine.CollectTimeframeVotes(
            Input(new[] { new TimeframeData("1h", Ramp(250, 100_000m, 60m)) }));

        var vote = Assert.Single(votes);
        Assert.Equal("1h", vote.Interval);
        Assert.True(vote.Trend > 55m);
    }

    [Fact]
    public void ShortTimeframes_AreExcludedFromVoting()
    {
        var votes = AdvancedDecisionEngine.CollectTimeframeVotes(
            Input(new[]
            {
                new TimeframeData("1h", Ramp(250, 100_000m, 60m)),
                new TimeframeData("5m", Ramp(20, 100_000m, 5m)) // < 60 candles: not enough history
            }));

        Assert.Single(votes);
    }
}
