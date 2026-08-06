using CryptoHft.Application.DecisionEngine;
using Xunit;

namespace CryptoHft.Tests;

// Measured over 254 independent hours (2026-08-06): sentiment sat at median 27 and never
// once left 11-35, derivatives at median 45 with a standard deviation of 5.0, onchain at
// median 44. Blended against a fixed neutral of 50 those three pushed the directional score
// down ~2.1 points every single hour, while BTC rose 9.8% across the same window. A reading
// that never moves cannot inform a direction — it can only tilt one.
public sealed class CategoryBaselineTests
{
    // Fail-safe: no history yet means the engine keeps its original behaviour exactly.
    [Fact]
    public void NoBaselineLeavesTheScoreUntouched()
        => Assert.Equal(62m, CategoryBaseline.Recenter(62m, null));

    // An input already sitting near 50 is centred well enough; shifting it would be
    // fitting noise rather than removing a bias.
    [Fact]
    public void WellCentredInputIsNotAdjusted()
    {
        Assert.Equal(62m, CategoryBaseline.Recenter(62m, 51m));
        Assert.Equal(62m, CategoryBaseline.Recenter(62m, 48m));
    }

    // The case this exists for: at its own habitual value the input must contribute nothing.
    [Fact]
    public void ReadingAtItsOwnMedianBecomesNeutral()
    {
        Assert.Equal(50m, CategoryBaseline.Recenter(27m, 27m));   // sentiment
        Assert.Equal(50m, CategoryBaseline.Recenter(45m, 45m));   // derivatives
        Assert.Equal(50m, CategoryBaseline.Recenter(44m, 44m));   // onchain
        Assert.Equal(50m, CategoryBaseline.Recenter(64m, 64m));   // macro, tilted the other way
    }

    // Deviation from its own habit still votes, and keeps its original size.
    [Fact]
    public void DeviationFromHabitStillVotes()
    {
        // sentiment 8 points above its own median stays an 8-point bullish vote
        Assert.Equal(58m, CategoryBaseline.Recenter(35m, 27m));
        // and 8 below stays an 8-point bearish one
        Assert.Equal(42m, CategoryBaseline.Recenter(19m, 27m));
    }

    // Shifting without rescaling is deliberate: dividing by a 5-point spread would turn a
    // near-constant's wobble into a full-sized directional vote.
    [Fact]
    public void CorrectionIsAShiftNotAStretch()
    {
        var low = CategoryBaseline.Recenter(40m, 45m);
        var high = CategoryBaseline.Recenter(50m, 45m);

        Assert.Equal(10m, high - low);   // the 10-point gap survives unchanged
    }

    // A provider stuck at an extreme is breakage, not an off-centre distribution.
    [Fact]
    public void RunawayOffsetIsCapped()
    {
        var corrected = CategoryBaseline.Recenter(0m, 0m);

        Assert.Equal(CategoryBaseline.MaxCorrection, corrected);
        Assert.True(corrected < CategoryBaseline.Neutral,
            "a fully broken input must not be handed a neutral vote it did not earn");
    }

    // The largest offset actually observed must be corrected in full, not clipped.
    [Fact]
    public void RealSentimentOffsetFitsUnderTheCap()
        => Assert.True(CategoryBaseline.MaxCorrection >= 23m,
            "sentiment sat 23 points below neutral; a tighter cap leaves the bias in place");

    [Fact]
    public void ResultStaysOnTheZeroToHundredScale()
    {
        Assert.InRange(CategoryBaseline.Recenter(100m, 30m), 0m, 100m);
        Assert.InRange(CategoryBaseline.Recenter(0m, 70m), 0m, 100m);
    }

    // A median built from a handful of readings describes the sample, not the input.
    [Fact]
    public void ThinHistoryYieldsNoBaseline()
        => Assert.Null(CategoryBaseline.ComputeBaseline(Enumerable.Repeat(27m, CategoryBaseline.MinSamples - 1).ToList()));

    [Fact]
    public void SufficientHistoryYieldsTheMedian()
    {
        var history = Enumerable.Range(0, CategoryBaseline.MinSamples).Select(i => (decimal)i).ToList();

        var baseline = CategoryBaseline.ComputeBaseline(history);

        Assert.NotNull(baseline);
        Assert.Equal((history[^1] + 0m) / 2m, baseline!.Value, precision: 1);
    }

    // Median rather than mean, so a few provider spikes cannot drag the correction.
    [Fact]
    public void OutliersDoNotMoveTheBaseline()
    {
        var history = Enumerable.Repeat(27m, CategoryBaseline.MinSamples).ToList();
        history.AddRange(Enumerable.Repeat(100m, 20));

        Assert.Equal(27m, CategoryBaseline.ComputeBaseline(history));
    }

    // End to end on the measured numbers: the three tilted inputs stop contributing a
    // standing bias, which was ~2.1 points of directional score every hour.
    [Fact]
    public void MeasuredTiltIsRemovedFromTheBlend()
    {
        var weights = new (string Cat, decimal Weight, decimal Median)[]
        {
            ("sentiment", 0.04m, 27m),
            ("derivatives", 0.17m, 45m),
            ("onchain", 0.10m, 44m),
        };

        var before = weights.Sum(w => (w.Median - 50m) * w.Weight);
        var after = weights.Sum(w => (CategoryBaseline.Recenter(w.Median, w.Median) - 50m) * w.Weight);

        Assert.True(before < -2m, $"expected the measured standing tilt, got {before:F2}");
        Assert.Equal(0m, after);
    }
}
