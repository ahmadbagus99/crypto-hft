using CryptoHft.Application.DecisionEngine;
using CryptoHft.Domain.Enums;
using Xunit;

namespace CryptoHft.Tests;

// Break -> Retest -> Rejection as a sequence. The engine already reported "broke above 64200"
// on the breaking candle and "rejection wick off support" on a piercing one, but had no memory
// tying them together — a grep for "retest" across the whole DecisionEngine returned nothing.
//
// Split deliberately between what the source material specifies and what it does not:
//   from the video — the break carries momentum, the return leg is WEAKER, a wick through the
//                    level is normal, and the trigger is the rejection rather than the touch
//   ours          — 20 bars, a 0.25xATR zone, 0.6xATR displacement, 0.75x decay, 40% wick
// The second list is engineering, is named as such in the code, and is what to fit later.
public sealed class BreakRetestRejectionTests
{
    private const decimal Atr = 10m;

    private static Candle C(decimal open, decimal high, decimal low, decimal close, int i = 0)
        => new(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero).AddMinutes(i),
               open, high, low, close, 100m);

    // Drift up to 100, break 100 hard, drift back down to it, then a chosen final bar.
    private static List<Candle> Sequence(Candle final, int pullbackBars = 4, decimal pullbackBody = 2m)
    {
        var bars = new List<Candle>();
        var i = 0;

        // Approach: small bars under the level, carving a swing high at 100.
        for (var p = 88m; p < 100m; p += 3m) bars.Add(C(p, p + 1m, p - 1m, p + 1m, i++));
        bars.Add(C(99m, 100m, 98m, 99m, i++));      // the swing high at 100
        bars.Add(C(99m, 99.5m, 97m, 97.5m, i++));
        bars.Add(C(97.5m, 98m, 96m, 96.5m, i++));

        // Breakout: a decisive body clearing 100 (10 points = 1.0xATR, over the 0.6 minimum).
        bars.Add(C(97m, 108m, 96.8m, 107m, i++));

        // Return leg: smaller bodies drifting back toward the level.
        var price = 107m;
        for (var k = 0; k < pullbackBars; k++)
        {
            var next = price - pullbackBody;
            bars.Add(C(price, price + 0.3m, next - 0.3m, next, i++));
            price = next;
        }

        bars.Add(final with { OpenTime = bars[^1].OpenTime.AddMinutes(1) });
        return bars;
    }

    // ---- The shape the source material calls the trigger --------------------------------

    [Fact]
    public void RejectionAtTheRetestedLevelIsDetected()
    {
        // Wick pierces below 100, close returns above it — the video's own picture.
        var bars = Sequence(C(101m, 101.5m, 97.5m, 101m));

        var s = BreakRetestRejection.Detect(bars, Atr);

        Assert.True(s.Detected);
        Assert.Equal(TradeSide.Long, s.Side);
        Assert.Equal(100m, s.Level);
        Assert.True(s.RejectionConfirmed);
        Assert.Contains("REJECTED", s.Summary);
    }

    // "The touch is not the entry" — the one rule stated most explicitly.
    [Fact]
    public void ReachingTheLevelWithoutRejectingIsNotATrigger()
    {
        // Sits on the level and closes mid-range: interaction, no rejection.
        var bars = Sequence(C(101m, 101.2m, 99m, 99.5m));

        var s = BreakRetestRejection.Detect(bars, Atr);

        Assert.True(s.Detected);
        Assert.False(s.RejectionConfirmed);
        Assert.Contains("no rejection yet", s.Summary);
    }

    // A bare wick is explicitly not enough; the bar has to spend real range rejecting.
    [Fact]
    public void ShallowWickDoesNotCountAsRejection()
    {
        var bars = Sequence(C(101m, 103m, 100.6m, 101m));   // barely reaches the zone, tiny lower wick

        var s = BreakRetestRejection.Detect(bars, Atr);

        Assert.False(s.RejectionConfirmed);
    }

    // ---- The fading return leg ----------------------------------------------------------

    // The tell is that the move back is weaker than the break. Coming back just as hard is a
    // failed breakout, not a retest.
    [Fact]
    public void ReturnLegAsStrongAsTheBreakIsNotARetest()
    {
        var bars = Sequence(C(101m, 101.5m, 97.5m, 101m), pullbackBars: 2, pullbackBody: 9m);

        var s = BreakRetestRejection.Detect(bars, Atr);

        Assert.False(s.Detected);
    }

    [Fact]
    public void DecayIsReportedSoTheFadeCanBeJudged()
    {
        var s = BreakRetestRejection.Detect(Sequence(C(101m, 101.5m, 97.5m, 101m)), Atr);

        Assert.True(s.PullbackDecay < BreakRetestRejection.MaxPullbackDecay);
        Assert.True(s.Displacement >= BreakRetestRejection.MinDisplacementAtr);
    }

    // ---- Our parameters, marked as ours -------------------------------------------------

    // The video sets no bar count. This one is ours, and a break older than it is just a level
    // with history rather than a live retest.
    [Fact]
    public void ABreakOlderThanTheWindowIsNoLongerARetest()
    {
        var bars = Sequence(C(101m, 101.5m, 97.5m, 101m));
        for (var k = 0; k < BreakRetestRejection.MaxBarsSinceBreak + 5; k++)
            bars.Insert(bars.Count - 1, C(101m, 101.4m, 100.6m, 101m, 200 + k));

        Assert.False(BreakRetestRejection.Detect(bars, Atr).Detected);
    }

    [Fact]
    public void EngineeringParametersAreSeparableFromTheSourceMaterial()
    {
        // Named constants rather than literals buried in the scan, because these are the
        // numbers to fit once enough trades carry their outcome.
        Assert.Equal(20, BreakRetestRejection.MaxBarsSinceBreak);
        Assert.Equal(0.25m, BreakRetestRejection.ZoneAtr);
        Assert.Equal(0.6m, BreakRetestRejection.MinDisplacementAtr);
        Assert.Equal(0.75m, BreakRetestRejection.MaxPullbackDecay);
        Assert.Equal(0.4m, BreakRetestRejection.MinRejectionWick);
    }

    // ---- Degrades quietly ---------------------------------------------------------------

    [Fact]
    public void ShortHistoryOrNoAtrReportsNothing()
    {
        Assert.False(BreakRetestRejection.Detect(new[] { C(1, 2, 0, 1) }, Atr).Detected);
        Assert.False(BreakRetestRejection.Detect(Sequence(C(101m, 101.5m, 97.5m, 101m)), 0m).Detected);
    }

    // ---- Wiring into the gates ----------------------------------------------------------

    // A confirmed retest running with the trend answers location and timing in one statement,
    // and a sharper one than "price is in the discount half".
    [Fact]
    public void ConfirmedBrrSatisfiesLocationAndTimingTogether()
    {
        var brr = BreakRetestRejection.Detect(Sequence(C(101m, 101.5m, 97.5m, 101m)), Atr);

        var v = ScalperSequentialGate.Evaluate(
            vote4h: 89m, vote1h: 85m,
            rangePosition: 0.95m,          // would fail the generic location gate
            atNamedLevel: false,
            entryCandles: Array.Empty<Candle>(),   // would fail the generic timing gate
            brr: brr);

        Assert.True(v.Allowed);
        Assert.Contains("BRR", v.Reason);
    }

    // A retest still waiting for its rejection is refused by name, not by a vaguer reason.
    [Fact]
    public void PendingBrrIsRefusedAtTiming()
    {
        var brr = BreakRetestRejection.Detect(Sequence(C(101m, 101.2m, 99m, 99.5m)), Atr);

        var v = ScalperSequentialGate.Evaluate(
            vote4h: 89m, vote1h: 85m, rangePosition: 0.30m, atNamedLevel: true,
            entryCandles: Array.Empty<Candle>(), brr: brr);

        Assert.False(v.Allowed);
        Assert.Equal("timing", v.Stage);
        Assert.Contains("without rejection", v.Reason);
    }

    // A retest pointing the other way must not be borrowed to justify the trade.
    [Fact]
    public void BrrOnTheOppositeSideIsIgnored()
    {
        var brr = BreakRetestRejection.Detect(Sequence(C(101m, 101.5m, 97.5m, 101m)), Atr);

        var v = ScalperSequentialGate.Evaluate(
            vote4h: 20m, vote1h: 25m,      // both bearish -> short
            rangePosition: 0.80m, atNamedLevel: true,
            entryCandles: new[]
            {
                new Candle(DateTimeOffset.UtcNow, 100m, 101m, 99m, 100.5m, 10m),
                new Candle(DateTimeOffset.UtcNow.AddMinutes(1), 100.5m, 101m, 98m, 98.5m, 10m),
            },
            brr: brr);   // long-side BRR

        Assert.True(v.Allowed);
        Assert.Equal(TradeSide.Short, v.Side);
        Assert.DoesNotContain("BRR", v.Reason);   // the long retest played no part
    }
}
