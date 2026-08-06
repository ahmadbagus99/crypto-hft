using System.Text.RegularExpressions;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Domain.Enums;
using CryptoHft.Infrastructure.Ai;
using Xunit;

namespace CryptoHft.Tests;

// Claude's sizing used to be a constant: across 608 logged decisions it returned 0.25 in 83%
// of them, and setups with 6 of 8 categories aligned were sized the same as setups with 2
// (0.27 vs 0.25). A constant multiplier carries no information — it is arithmetically identical
// to lowering the baseline and never calling the model.
//
// The cause was an asymmetric prompt: an eight-item failure checklist plus a mandatory risk
// list guaranteed "several failure factors" would always be found, and that phrase alone
// triggered the lowest sizing band regardless of confluence. These tests lock in the fix —
// sizing is now a computation over two reported tallies, not a judgement call.
public sealed class LlmSizingPolicyTests
{
    private sealed record Band(int Net, decimal Low, decimal High);

    // Pulls "net>=6 -> 0.70-0.90 | net=5 -> ..." straight out of the shipped prompt, so the
    // assertions below test the policy the model actually receives.
    private static List<Band> ParseBands()
        => Regex.Matches(ClaudeDecisionValidator.SystemPrompt, @"net(?:>=|<=|=)(\d) -> (\d\.\d+)-(\d\.\d+)")
            .Select(m => new Band(
                int.Parse(m.Groups[1].Value),
                decimal.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

    [Fact]
    public void EveryNetValueFromZeroToSixSelectsABand()
    {
        var bands = ParseBands();

        Assert.Equal(7, bands.Count);
        Assert.Equal(new[] { 6, 5, 4, 3, 2, 1, 0 }, bands.Select(b => b.Net).ToArray());
    }

    // Higher confluence must never be allowed to size smaller than lower confluence.
    [Fact]
    public void BandsRiseMonotonicallyWithNet()
    {
        var bands = ParseBands();

        for (var i = 0; i < bands.Count; i++)
            Assert.True(bands[i].Low < bands[i].High, $"net={bands[i].Net} band is empty or inverted");

        for (var i = 0; i < bands.Count - 1; i++)
            Assert.True(bands[i].Low >= bands[i + 1].High,
                $"net={bands[i].Net} overlaps net={bands[i + 1].Net} — the mapping stops being deterministic");
    }

    // The failure this file exists to prevent: a table so narrow that every net collapses onto
    // the same number. The old behavior had a standard deviation of 0.05 across 608 calls.
    [Fact]
    public void TableSpreadsSizeWidelyEnoughToCarryInformation()
    {
        var bands = ParseBands();
        var best = bands.First(b => b.Net == 6);
        var worst = bands.First(b => b.Net == 0);

        Assert.True(best.Low / worst.High >= 4m,
            $"top band {best.Low} is only {best.Low / worst.High:F1}x the bottom band {worst.High}");
    }

    // Fees scale linearly with notional and the engine has no demonstrated directional edge,
    // so restoring spread must not quietly raise average size. Capping the table below 1.0
    // keeps the mean near where it was; the exceptional clause above it needs net>=7.
    [Fact]
    public void TableNeverSizesAboveTheBaseline()
    {
        var bands = ParseBands();

        Assert.True(bands.Max(b => b.High) <= 0.90m,
            "a band above the baseline bets on an edge that has not been measured yet");
    }

    // The exact phrase that made the lowest band self-triggering.
    [Fact]
    public void SeveralFailureFactorsIsNoLongerABandTrigger()
        => Assert.DoesNotContain("several failure factors", ClaudeDecisionValidator.SystemPrompt);

    [Fact]
    public void PromptNoLongerLeansDefensiveByDefault()
        => Assert.DoesNotContain("your default posture is to look for reasons the trade FAILS",
            ClaudeDecisionValidator.SystemPrompt);

    // Breaks the loop where the mandatory risk list fed straight back into a smaller size.
    [Fact]
    public void NamingARiskDoesNotShrinkTheTrade()
    {
        Assert.Contains("naming a risk does NOT by itself lower the size", ClaudeDecisionValidator.SystemPrompt);
        Assert.Contains("A mild or borderline reading is NOT a blocking factor", ClaudeDecisionValidator.SystemPrompt);
    }

    [Fact]
    public void BothTalliesAreRequiredInTheResponseSchema()
    {
        Assert.Contains("\"aligned_count\":number", ClaudeDecisionValidator.SystemPrompt);
        Assert.Contains("\"blocking_count\":number", ClaudeDecisionValidator.SystemPrompt);
    }

    private static AdvancedDecision Baseline(decimal confidence = 60m)
        => new(
            "BTCUSDT", DecisionAction.Buy, confidence, confidence, 100m - confidence, 20m, 55m,
            MarketRegime.Trending, 100_000m, 99_000m, 102_000m, 0m, 2m, 0.01m, 5, true, "",
            Array.Empty<string>(), new Dictionary<string, decimal>(), new Dictionary<string, decimal>(),
            Array.Empty<ScoreComponent>(), Array.Empty<string>(),
            new LlmValidation(true, confidence, "", Array.Empty<string>(), false), DateTimeOffset.UtcNow);

    // Without the tallies persisted, there is no way to check afterwards whether the multiplier
    // followed the counts — which is exactly why the constant went unnoticed for 608 calls.
    [Fact]
    public void ParserCapturesBothTallies()
    {
        var json = """
        {"aligned_count":5,"blocking_count":2,"confirmed":true,"adjusted_confidence":64,
         "size_multiplier":0.47,"leverage":5,"stop_loss":99000,"take_profit":102000,
         "narrative":"five aligned, two blocking","risks":["funding"]}
        """;

        var v = ClaudeDecisionValidator.ParseResponse(json, Baseline());

        Assert.Equal(5, v.AlignedCount);
        Assert.Equal(2, v.BlockingCount);
        Assert.Equal(64m, v.AdjustedConfidence);
        Assert.Equal(0.47m, v.SizeMultiplier);
    }

    // Older models or a truncated reply must not break the call — the tallies simply go unlogged.
    [Fact]
    public void ParserTreatsMissingTalliesAsUnknown()
    {
        var json = """{"confirmed":false,"adjusted_confidence":45,"size_multiplier":0.2}""";

        var v = ClaudeDecisionValidator.ParseResponse(json, Baseline());

        Assert.Null(v.AlignedCount);
        Assert.Null(v.BlockingCount);
        Assert.True(v.Used);
        Assert.Equal(45m, v.AdjustedConfidence);
    }

    // The first two calls under the three-step prompt produced 865 output tokens on average
    // against a 1024 ceiling, and one was cut mid-JSON. A truncated reply must degrade to the
    // engine's own read rather than half-parsing into a fabricated conviction.
    [Fact]
    public void TruncatedReplyIsNotHalfParsed()
    {
        var cut = """{"aligned_count":5,"blocking_count":1,"confirmed":true,"adjusted_confidence":68,"narrative":"five categories align and one blocking fact""";

        var v = ClaudeDecisionValidator.ParseResponse(cut, Baseline(confidence: 59m));

        Assert.False(v.Used);
        Assert.Equal(59m, v.AdjustedConfidence);
        Assert.Null(v.AlignedCount);
    }

    // A garbled reply falls back to the engine's own confidence rather than a fabricated one.
    [Fact]
    public void UnparseableReplyFallsBackToTheEngine()
    {
        var v = ClaudeDecisionValidator.ParseResponse("sorry, I cannot help with that", Baseline(confidence: 61m));

        Assert.False(v.Used);
        Assert.Equal(61m, v.AdjustedConfidence);
        Assert.Null(v.AlignedCount);
    }
}
