using CryptoHft.Application.DecisionEngine;
using Xunit;

namespace CryptoHft.Tests;

// Anti-chasing dampener: a signal pointing the same way as an already-extended move is
// faded toward neutral (late entries are the worst-calibrated bucket in realized data);
// counter-trend and neutral signals, and moves inside the start band, stay untouched.
public sealed class ChasingDampenerTests
{
    [Theory]
    [InlineData(66, 4.0, 0.64)]   // long chasing a 4-ATR rally: t=0.6 -> 1-0.6*0.6
    [InlineData(34, -4.0, 0.64)]  // short chasing a 4-ATR selloff: symmetric
    [InlineData(66, 10.0, 0.4)]   // extreme extension clamps at the floor
    [InlineData(34, -10.0, 0.4)]
    public void AlignedExtendedMove_IsDampened(decimal directional, decimal moveAtr, decimal expected)
        => Assert.Equal(expected, AdvancedDecisionEngine.ChasingDampener(directional, moveAtr));

    [Theory]
    [InlineData(66, 2.5)]    // at the start threshold: not yet chasing
    [InlineData(66, 1.0)]    // ordinary drift
    [InlineData(66, 0.0)]    // flat tape
    public void MoveInsideStartBand_IsUntouched(decimal directional, decimal moveAtr)
        => Assert.Equal(1m, AdvancedDecisionEngine.ChasingDampener(directional, moveAtr));

    [Theory]
    [InlineData(34, 4.0)]    // short into a rally: fading, not chasing
    [InlineData(66, -4.0)]   // long into a selloff: fading, not chasing
    [InlineData(50, 6.0)]    // neutral signal has no direction to chase
    public void CounterTrendOrNeutral_IsNeverDampened(decimal directional, decimal moveAtr)
        => Assert.Equal(1m, AdvancedDecisionEngine.ChasingDampener(directional, moveAtr));

    [Fact]
    public void DampenedLateSignal_FallsUnderEntryThreshold()
    {
        // A Buy at directional 66 (just past the 65 gate) after a 4-ATR rally lands at
        // 50 + 16 * 0.64 = 60.2 — the late entry no longer trades.
        var chase = AdvancedDecisionEngine.ChasingDampener(66m, 4m);
        Assert.True(50m + (66m - 50m) * chase < 65m);
    }

    // ---- RecentMoveInAtr ------------------------------------------------------------------

    private static Candle Bar(decimal close)
        => new(DateTimeOffset.UtcNow, close, close + 1, close - 1, close, 100m);

    [Fact]
    public void RecentMove_MeasuresLookbackInAtrMultiples()
    {
        // Closes 100..107: last close 107, six candles back 101 -> +6 points = 3 ATR at atr=2.
        var candles = Enumerable.Range(100, 8).Select(c => Bar(c)).ToList();
        Assert.Equal(3m, AdvancedDecisionEngine.RecentMoveInAtr(candles, atr: 2m));
    }

    [Fact]
    public void RecentMove_ShortSeries_UsesAvailableSpan()
    {
        var candles = new List<Candle> { Bar(100m), Bar(101m), Bar(104m) };
        Assert.Equal(2m, AdvancedDecisionEngine.RecentMoveInAtr(candles, atr: 2m));
    }

    [Fact]
    public void RecentMove_DegenerateInputs_AreNeutral()
    {
        Assert.Equal(0m, AdvancedDecisionEngine.RecentMoveInAtr(new List<Candle> { Bar(100m) }, atr: 2m));
        Assert.Equal(0m, AdvancedDecisionEngine.RecentMoveInAtr(new List<Candle> { Bar(100m), Bar(110m) }, atr: 0m));
    }
}
