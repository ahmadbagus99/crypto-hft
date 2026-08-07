using CryptoHft.Application.DecisionEngine;
using CryptoHft.Domain.Enums;
using Xunit;

namespace CryptoHft.Tests;

// Measured on 4-7 August: 20 scalper entries, every one long, 11 stopped out — and in 11 of
// 11 cases price returned above entry within four hours. The direction was right every time
// and the moment was wrong every time. A single weighted score cannot say that: 4h at 89 with
// 5m at 17 averages to 46.8, which reads exactly like "no idea" and is really "right way,
// wrong moment". These gates ask the three questions separately and stop at the first no.
public sealed class ScalperSequentialGateTests
{
    private static Candle Bar(decimal open, decimal high, decimal low, decimal close, int minute = 0)
        => new(new DateTimeOffset(2026, 8, 7, 10, minute, 0, TimeSpan.Zero), open, high, low, close, 100m);

    // ---- Gate 1: direction ------------------------------------------------------------

    [Fact]
    public void BothHigherTimeframesBullishTakesTheLongSide()
    {
        var v = ScalperSequentialGate.Direction(vote4h: 89m, vote1h: 85m);

        Assert.True(v.Allowed);
        Assert.Equal(TradeSide.Long, v.Side);
    }

    [Fact]
    public void BothHigherTimeframesBearishTakesTheShortSide()
    {
        var v = ScalperSequentialGate.Direction(vote4h: 30m, vote1h: 38m);

        Assert.True(v.Allowed);
        Assert.Equal(TradeSide.Short, v.Side);
    }

    // The whole point of a veto rather than a weight: a 60% higher-timeframe weighting would
    // have carried an emphatic 4h straight over a conflicted 1h and produced a trade.
    [Fact]
    public void DisagreementBetweenHigherTimeframesStandsAside()
    {
        var v = ScalperSequentialGate.Direction(vote4h: 89m, vote1h: 47m);

        Assert.False(v.Allowed);
        Assert.Null(v.Side);
        Assert.Equal("direction", v.Stage);
    }

    // ---- Gate 2: location -------------------------------------------------------------

    [Fact]
    public void LongIsAllowedFromTheDiscountHalf()
        => Assert.True(ScalperSequentialGate.Location(TradeSide.Long, rangePosition: 0.30m, atNamedLevel: false).Allowed);

    [Fact]
    public void LongIsRefusedAtTheTopOfTheRangeWithNoLevel()
    {
        var v = ScalperSequentialGate.Location(TradeSide.Long, rangePosition: 0.88m, atNamedLevel: false);

        Assert.False(v.Allowed);
        Assert.Equal("location", v.Stage);
    }

    // A named level — an unfilled gap, a fib zone, a tested band — is a reason to trade from
    // wherever price happens to sit in the range.
    [Fact]
    public void ANamedLevelOverridesRangePosition()
        => Assert.True(ScalperSequentialGate.Location(TradeSide.Long, rangePosition: 0.88m, atNamedLevel: true).Allowed);

    [Fact]
    public void ShortIsRefusedFromTheDiscountHalf()
        => Assert.False(ScalperSequentialGate.Location(TradeSide.Short, rangePosition: 0.20m, atNamedLevel: false).Allowed);

    // ---- Gate 3: timing ---------------------------------------------------------------

    // The shape every stopped-out entry had: price still travelling into the level.
    [Fact]
    public void StillFallingIntoTheLevelIsNotATurn()
    {
        var bars = new[] { Bar(64850, 64860, 64800, 64810), Bar(64810, 64820, 64760, 64770, 1) };

        var v = ScalperSequentialGate.Timing(TradeSide.Long, bars);

        Assert.False(v.Allowed);
        Assert.Equal("timing", v.Stage);
    }

    // A bar that closes up after a red bar, in the upper half of its own range, is the turn.
    [Fact]
    public void ReversalBarAfterARedBarConfirmsALong()
    {
        var bars = new[] { Bar(64850, 64860, 64790, 64800), Bar(64800, 64880, 64795, 64870, 1) };

        var v = ScalperSequentialGate.Timing(TradeSide.Long, bars);

        Assert.True(v.Allowed);
        Assert.Contains("reversal bar", v.Reason);
    }

    // Sweeping the prior low and closing back up is the classic stop-hunt reversal.
    [Fact]
    public void SweepingThePriorLowAndClosingUpConfirmsALong()
    {
        var bars = new[] { Bar(64800, 64880, 64790, 64860), Bar(64860, 64900, 64780, 64890, 1) };

        var v = ScalperSequentialGate.Timing(TradeSide.Long, bars);

        Assert.True(v.Allowed);
        Assert.Contains("swept", v.Reason);
    }

    // A doji that merely ticks the right way is not conviction.
    [Fact]
    public void WeakCloseInsideTheRangeIsNotATurn()
    {
        var bars = new[] { Bar(64850, 64860, 64790, 64800), Bar(64800, 64900, 64790, 64810, 1) };

        Assert.False(ScalperSequentialGate.Timing(TradeSide.Long, bars).Allowed);
    }

    [Fact]
    public void ContinuationOfAMoveAlreadyUnderWayIsNotATurn()
    {
        // Two green bars in a row, second neither sweeps the first's low nor follows a red bar.
        var bars = new[] { Bar(64800, 64870, 64795, 64860), Bar(64862, 64920, 64858, 64910, 1) };

        var v = ScalperSequentialGate.Timing(TradeSide.Long, bars);

        Assert.False(v.Allowed);
        Assert.Contains("already under way", v.Reason);
    }

    [Fact]
    public void ShortNeedsItsOwnReversalBar()
    {
        var down = new[] { Bar(64800, 64870, 64795, 64860), Bar(64860, 64880, 64790, 64800, 1) };
        var up = new[] { Bar(64850, 64860, 64790, 64800), Bar(64800, 64880, 64795, 64870, 1) };

        Assert.True(ScalperSequentialGate.Timing(TradeSide.Short, down).Allowed);
        Assert.False(ScalperSequentialGate.Timing(TradeSide.Short, up).Allowed);
    }

    [Fact]
    public void TooFewBarsCannotConfirmATurn()
        => Assert.False(ScalperSequentialGate.Timing(TradeSide.Long, new[] { Bar(1, 2, 0, 1) }).Allowed);

    // ---- The sequence -----------------------------------------------------------------

    // The exact reading from production on 7 August that the averaged score turned into a
    // meaningless 46.8: higher timeframes emphatic, price in discount, bars still falling.
    // It has to resolve to WAIT, and the log has to say which question stopped it.
    [Fact]
    public void RightDirectionWrongMomentResolvesToWaitAtTheTimingGate()
    {
        var v = ScalperSequentialGate.Evaluate(
            vote4h: 89m, vote1h: 85m, rangePosition: 0.30m, atNamedLevel: true,
            entryCandles: new[] { Bar(64850, 64860, 64800, 64810), Bar(64810, 64820, 64760, 64770, 1) });

        Assert.False(v.Allowed);
        Assert.Equal("timing", v.Stage);
        Assert.Equal(TradeSide.Long, v.Side);   // the side is known, the moment is not here
    }

    [Fact]
    public void AllThreeGatesPassingAllowsTheTrade()
    {
        var v = ScalperSequentialGate.Evaluate(
            vote4h: 89m, vote1h: 85m, rangePosition: 0.30m, atNamedLevel: true,
            entryCandles: new[] { Bar(64850, 64860, 64790, 64800), Bar(64800, 64880, 64795, 64870, 1) });

        Assert.True(v.Allowed);
        Assert.Equal(TradeSide.Long, v.Side);
        Assert.Equal("pass", v.Stage);
    }

    // A failure reports the gate that stopped it, so a quiet stretch can be read as "the
    // timeframes never agreed" rather than only as "nothing happened".
    [Fact]
    public void TheFirstFailingGateIsTheOneReported()
    {
        var v = ScalperSequentialGate.Evaluate(
            vote4h: 89m, vote1h: 40m, rangePosition: 0.95m, atNamedLevel: false,
            entryCandles: Array.Empty<Candle>());

        Assert.Equal("direction", v.Stage);
    }

    // ---- Isolation from intraday ------------------------------------------------------

    [Fact]
    public void OnlyTheScalperProfileRunsTheGates()
    {
        Assert.True(TradingStyleProfile.Scalper.UsesSequentialGate);
        Assert.False(TradingStyleProfile.Intraday.UsesSequentialGate);
    }

    [Fact]
    public void ScalperTimesEntriesOnTheOneMinuteSeries()
        => Assert.Equal("1m", TradingStyleProfile.Scalper.TimingInterval);

    // The stop that was measured losing (t = -2.62) sat at 1.5xATR, inside a 0.450% ordinary
    // drawdown. Anything back under 2xATR reopens that.
    [Fact]
    public void ScalperStopClearsTheNoiseBand()
        => Assert.True(TradingStyleProfile.Scalper.FallbackSlAtrMultiplier >= 2.5m,
            "a stop below 2.5xATR sits inside the drawdown that stopped 15 of 20 entries");

    [Fact]
    public void IntradayGeometryIsUntouched()
    {
        Assert.Equal(2m, TradingStyleProfile.Intraday.FallbackSlAtrMultiplier);
        Assert.Equal(4m, TradingStyleProfile.Intraday.FallbackTpAtrMultiplier);
        Assert.True(TradingStyleProfile.Intraday.UseLearnedTuning);
    }
}
