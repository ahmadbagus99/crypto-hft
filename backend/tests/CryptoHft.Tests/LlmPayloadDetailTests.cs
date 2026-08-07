using CryptoHft.Application.DecisionEngine;
using CryptoHft.Domain.Enums;
using CryptoHft.Infrastructure.Ai;
using Xunit;

namespace CryptoHft.Tests;

// The engine already computes order blocks, fair value gaps, liquidity sweeps, BOS/CHoCH,
// Fibonacci retracement, chart patterns and horizontal S/R — and then averaged all of it into
// a single "structure" number before anything reached Claude. It was being asked to judge a
// setup it had no way to see: structure=52 says nothing about an unfilled FVG at 64,200 or a
// support band with four touches. These tests keep that detail on the wire.
public sealed class LlmPayloadDetailTests
{
    private static AdvancedDecision DecisionWith(params ScoreComponent[] components)
        => new(
            "BTCUSDT", DecisionAction.Buy, 60m, 60m, 40m, 20m, 55m,
            MarketRegime.Trending, 64_800m, 64_600m, 65_200m, 0m, 2m, 0.002m, 20, true, "",
            Array.Empty<string>(), new Dictionary<string, decimal>(), new Dictionary<string, decimal>(),
            components, Array.Empty<string>(),
            new LlmValidation(true, 60m, "", Array.Empty<string>(), false), DateTimeOffset.UtcNow);

    private static Candle Bar(int minute, decimal open, decimal high, decimal low, decimal close)
        => new(new DateTimeOffset(2026, 8, 6, 9, minute, 0, TimeSpan.Zero), open, high, low, close, 100m);

    private static AdvancedDecisionInput InputWith(params TimeframeData[] timeframes)
        => new(
            "BTCUSDT", 64_800m, timeframes,
            new DerivativesSnapshot(0.0001m, 1000m, 0m, 1m, 1m, 0m, 1m),
            new SentimentSnapshot(50m, 50m, "Neutral", 50, "Neutral", Array.Empty<string>()),
            new MacroSnapshot(50m, "", false),
            new OnchainSnapshot(50m, "", false));

    [Fact]
    public void NamedLevelsReachThePayload()
    {
        var block = ClaudeDecisionValidator.BuildStructureBlock(DecisionWith(
            new ScoreComponent("SmartMoney", 68m, 0m, "bullish FVG, liquidity sweep low (bullish), discount zone (0.32)"),
            new ScoreComponent("SupportResistance", 61m, 0m, "S 64150 (4t) / R 64890 (3t)"),
            new ScoreComponent("Fibonacci", 58m, 0m, "retrace 0.62 of up impulse — golden pocket")));

        Assert.Contains("bullish FVG", block);
        Assert.Contains("64150 (4t)", block);
        Assert.Contains("golden pocket", block);
        Assert.Contains("68", block);   // the component's own score travels with its reason
    }

    // A decision whose analyses produced nothing must not emit an empty labelled section.
    [Fact]
    public void NoStructureEvidenceEmitsNothing()
    {
        Assert.Equal("", ClaudeDecisionValidator.BuildStructureBlock(DecisionWith()));
        Assert.Equal("", ClaudeDecisionValidator.BuildStructureBlock(DecisionWith(
            new ScoreComponent("SmartMoney", 50m, 0m, ""))));
    }

    // Categories outside the structure family are already in the score line; repeating them
    // would spend tokens twice.
    [Fact]
    public void OnlyStructureComponentsAreIncluded()
    {
        var block = ClaudeDecisionValidator.BuildStructureBlock(DecisionWith(
            new ScoreComponent("SmartMoney", 68m, 0m, "bullish FVG"),
            new ScoreComponent("News", 10m, 0m, "very bearish headlines"),
            new ScoreComponent("Derivatives", 62m, 0m, "funding neutral")));

        Assert.Contains("bullish FVG", block);
        Assert.DoesNotContain("very bearish headlines", block);
        Assert.DoesNotContain("funding neutral", block);
    }

    // Confirmation is a question about the fastest bars: did price turn at the level, or is it
    // still falling into it. Answering from an hourly candle would miss the turn entirely.
    [Fact]
    public void CandleBlockUsesTheFastestTimeframe()
    {
        var block = ClaudeDecisionValidator.BuildCandleBlock(InputWith(
            new TimeframeData("4h", new[] { Bar(0, 1m, 1m, 1m, 1m) }),
            new TimeframeData("5m", new[] { Bar(0, 64_700m, 64_760m, 64_690m, 64_750m) }),
            new TimeframeData("1h", new[] { Bar(0, 2m, 2m, 2m, 2m) })));

        Assert.Contains("5m", block);
        Assert.Contains("64750", block.Replace(",", "").Replace(".0", ""));
    }

    [Fact]
    public void CandleBlockKeepsTheMostRecentBarsInOrder()
    {
        var bars = Enumerable.Range(0, 20)
            .Select(i => Bar(i, 64_000m + i, 64_010m + i, 63_990m + i, 64_005m + i))
            .ToArray();

        var block = ClaudeDecisionValidator.BuildCandleBlock(InputWith(new TimeframeData("5m", bars)));
        var lines = block.Split('\n').Skip(1).ToList();

        Assert.Equal(6, lines.Count);
        Assert.Contains("09:14", lines[0]);   // oldest of the tail
        Assert.Contains("09:19", lines[^1]);  // newest last
    }

    [Fact]
    public void NoCandlesEmitsNothing()
    {
        Assert.Equal("", ClaudeDecisionValidator.BuildCandleBlock(InputWith()));
        Assert.Equal("", ClaudeDecisionValidator.BuildCandleBlock(
            InputWith(new TimeframeData("5m", Array.Empty<Candle>()))));
    }

    private static AdvancedDecision WithScores(params (string Key, decimal Value)[] scores)
        => DecisionWith() with { Scores = scores.ToDictionary(s => s.Key, s => s.Value) };

    // Production 2026-08-07 10:17: sentiment read 29 and Claude counted it as a blocking
    // factor "more than 15 points against", while the engine — knowing sentiment normally
    // sits at 28 — had already treated it as neutral. Once the engine started centring inputs
    // on their own habit, sending raw numbers meant the two sides judged the same setup on
    // different rulers.
    [Fact]
    public void ScoreLineCarriesTheValueTheEngineActuallyVotesOn()
    {
        var line = ClaudeDecisionValidator.BuildScoreLine(
            WithScores(("sentiment", 29m)),
            new Dictionary<string, decimal> { ["sentiment"] = 28m });

        Assert.Contains("sentiment=51", line);          // what the engine blends
        Assert.Contains("raw 29", line);                // absolute level still visible
        Assert.Contains("its own normal 28", line);
    }

    // The same correction the other way: 61 looks mildly bullish next to 50 and is a strong
    // reading next to news's own 32.
    [Fact]
    public void AnUnusuallyHighReadingIsShownAsUnusual()
    {
        var line = ClaudeDecisionValidator.BuildScoreLine(
            WithScores(("news", 61m)),
            new Dictionary<string, decimal> { ["news"] = 32m });

        Assert.Contains("news=79", line);
        Assert.Contains("raw 61", line);
    }

    // A well-centred category is left plain, so the annotation marks the ones that moved.
    [Fact]
    public void WellCentredCategoriesStayUnannotated()
    {
        var line = ClaudeDecisionValidator.BuildScoreLine(
            WithScores(("technical", 67m)),
            new Dictionary<string, decimal> { ["technical"] = 51m });

        Assert.Equal("technical=67", line);
    }

    // No baselines yet (fresh database) must reproduce the original raw score line exactly.
    [Fact]
    public void WithoutBaselinesTheLineIsUnchanged()
    {
        var line = ClaudeDecisionValidator.BuildScoreLine(
            WithScores(("sentiment", 29m), ("news", 61m)),
            new Dictionary<string, decimal>());

        Assert.Equal("sentiment=29, news=61", line);
    }

    // The owner's method, encoded as a rule: a level touched is a setup, a level rejected is a
    // trigger, and only the trigger earns size.
    [Fact]
    public void UnconfirmedLevelIsABlockingFactor()
    {
        Assert.Contains("UNCONFIRMED LEVEL", ClaudeDecisionValidator.SystemPrompt);
        Assert.Contains("no reversal bar has closed", ClaudeDecisionValidator.SystemPrompt);
    }

    // Structure must be judged from the levels, not from the average that hid them.
    [Fact]
    public void PromptDirectsStructureJudgementToTheLevels()
        => Assert.Contains("rather than the rolled-up number alone", ClaudeDecisionValidator.SystemPrompt);
}
