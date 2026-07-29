using CryptoHft.Application.DecisionEngine;
using Xunit;

namespace CryptoHft.Tests;

// Covers the classical level analyses added for the responsiveness batch: Fibonacci
// retracement of the last impulse, chart patterns (triangles/rectangles + breakout),
// and horizontal support/resistance clustering with the TP snap.
public sealed class LevelAnalysisTests
{
    // Same candle idiom as SmartMoneyConceptsTests: asymmetric wicks keep fractal
    // extremes unique even when consecutive peaks share a close.
    private static Candle Bar(decimal open, decimal close)
        => new(DateTimeOffset.UtcNow,
            open,
            Math.Max(open, close) + (close >= open ? 0.5m : 0.2m),
            Math.Min(open, close) - (close <= open ? 0.5m : 0.2m),
            close,
            100m);

    private static List<Candle> Flat(int count, decimal price)
        => Enumerable.Range(0, count).Select(_ => Bar(price, price)).ToList();

    private static List<Candle> FromCloses(decimal start, params decimal[] closes)
    {
        var candles = new List<Candle>();
        var prev = start;
        foreach (var close in closes)
        {
            candles.Add(Bar(prev, close));
            prev = close;
        }
        return candles;
    }

    private static decimal AtrOf(List<Candle> candles) => TechnicalIndicators.Atr(candles)[^1];

    // ---- Fibonacci -----------------------------------------------------------------------------

    [Fact]
    public void Fib_GoldenPocketPullback_OfUpImpulse_IsBullish()
    {
        // Impulse 100 -> 130, then a pullback to ~61.8% of the range.
        var candles = Flat(12, 100m);
        candles.AddRange(FromCloses(100, 106, 112, 118, 124, 130, 124, 118, 112));
        var atr = AtrOf(candles);

        var fib = FibonacciAnalysis.Analyze(candles, atr);

        Assert.True(fib.ImpulseUp);
        Assert.InRange(fib.RetraceRatio, 0.585m, 0.68m);
        Assert.True(fib.Score > 50m);
    }

    [Fact]
    public void Fib_GoldenPocketRally_OfDownImpulse_IsBearish()
    {
        var candles = Flat(12, 130m);
        candles.AddRange(FromCloses(130, 124, 118, 112, 106, 100, 106, 112, 118));
        var atr = AtrOf(candles);

        var fib = FibonacciAnalysis.Analyze(candles, atr);

        Assert.False(fib.ImpulseUp);
        Assert.True(fib.Score < 50m);
    }

    [Fact]
    public void Fib_RetracementBeyond90Percent_InvalidatesImpulse()
    {
        var candles = Flat(14, 100m);
        candles.AddRange(FromCloses(100, 110, 120, 130, 120, 110, 101));
        var atr = AtrOf(candles);

        var fib = FibonacciAnalysis.Analyze(candles, atr);

        Assert.True(fib.Score < 50m);
    }

    [Theory]
    [InlineData(0.62, 12, "golden pocket 0.618-0.65")]
    [InlineData(0.50, 8, "0.5 zone")]
    [InlineData(0.38, 6, "0.382 zone")]
    [InlineData(0.95, -6, "impulse invalidated")]
    [InlineData(0.10, 0, "shallow pullback")]
    public void Fib_ZoneBias_MapsRetracementToBias(double retrace, decimal bias, string zone)
    {
        var (b, z) = FibonacciAnalysis.ZoneBias((decimal)retrace);
        Assert.Equal(bias, b);
        Assert.Equal(zone, z);
    }

    // ---- Chart patterns ------------------------------------------------------------------------

    // Flat swing highs near 120.5 and rising swing lows (103.5 -> 107.5 -> 111.5):
    // the textbook ascending triangle. The final close decides breakout vs forming.
    private static List<Candle> AscendingTriangle(decimal finalClose)
    {
        var candles = Flat(20, 100m);
        candles.AddRange(FromCloses(100,
            108, 116, 120,   // first swing high 120.5
            112, 104,        // swing low 103.5
            112, 120,        // second swing high 120.5
            114, 108,        // swing low 107.5
            116, 120,        // third swing high 120.5
            116, 112,        // swing low 111.5
            115, finalClose));
        return candles;
    }

    [Fact]
    public void Pattern_AscendingTriangle_LeansBullishWhileForming()
    {
        var candles = AscendingTriangle(116m);
        var result = ChartPatternDetector.Detect(candles, AtrOf(candles));

        Assert.Equal(ChartPattern.AscendingTriangle, result.Pattern);
        Assert.Equal(0, result.BreakoutDirection);
        Assert.True(result.Score > 50m);
        Assert.Null(result.MeasuredTarget);
    }

    [Fact]
    public void Pattern_BreakoutUp_VotesBullishWithMeasuredTarget()
    {
        var candles = AscendingTriangle(125m);
        var result = ChartPatternDetector.Detect(candles, AtrOf(candles));

        Assert.Equal(ChartPattern.AscendingTriangle, result.Pattern);
        Assert.Equal(1, result.BreakoutDirection);
        Assert.True(result.Score >= 60m);
        Assert.True(result.MeasuredTarget > 125m);
    }

    [Fact]
    public void Pattern_TrendingSwings_AreNotAPattern()
    {
        // Higher highs AND higher lows — a trend, not a consolidation.
        var candles = Flat(20, 100m);
        candles.AddRange(FromCloses(100,
            110, 104, 114, 108, 118, 112, 122, 116, 126, 120, 124, 125));
        var result = ChartPatternDetector.Detect(candles, AtrOf(candles));

        Assert.Equal(ChartPattern.None, result.Pattern);
        Assert.Equal(50m, result.Score);
    }

    [Theory]
    [InlineData(ChartPattern.AscendingTriangle, 0, false, 6)]
    [InlineData(ChartPattern.DescendingTriangle, 0, false, -6)]
    [InlineData(ChartPattern.SymmetricalTriangle, 0, false, 0)]
    [InlineData(ChartPattern.Rectangle, 1, false, 12)]
    [InlineData(ChartPattern.Rectangle, 1, true, 17)]
    [InlineData(ChartPattern.AscendingTriangle, -1, true, -17)]
    public void Pattern_Bias_FollowsBreakoutAndVolume(
        ChartPattern pattern, int breakout, bool volume, decimal expected)
        => Assert.Equal(expected, ChartPatternDetector.PatternBias(pattern, breakout, volume));

    // ---- Support / resistance ------------------------------------------------------------------

    [Fact]
    public void Sr_BuildLevels_ClustersNearbyPivots_AndDropsSingles()
    {
        var swings = new List<SmartMoneyConcepts.SwingPoint>
        {
            new(1, 100.0m, false),
            new(5, 100.2m, false),
            new(9, 100.3m, false),
            new(13, 105.0m, true) // single touch — noise
        };

        var levels = SupportResistanceLevels.BuildLevels(swings, atr: 1m);

        var level = Assert.Single(levels);
        Assert.Equal(3, level.Touches);
        Assert.InRange(level.Price, 100.0m, 100.3m);
    }

    [Fact]
    public void Sr_HoldingAboveTestedSupport_LeansBullish()
    {
        // Range 100-110 tested three times on each side; price sits just above support.
        var candles = Flat(20, 105m);
        candles.AddRange(FromCloses(105,
            101, 106, 110, 106, 101, 106, 110, 106, 101, 106, 110, 106, 102));
        var result = SupportResistanceLevels.Analyze(candles, AtrOf(candles));

        Assert.NotNull(result.NearestSupport);
        Assert.True(result.NearestSupport!.Touches >= 2);
        Assert.True(result.Score > 50m);
    }

    [Fact]
    public void Sr_SnapTakeProfit_PullsTpInFrontOfWall()
    {
        var signals = new SrSignals(50m, null, new SrLevel(110m, 3), "");

        var (snapped, note) = SupportResistanceLevels.SnapTakeProfit(
            signals, isLong: true, entry: 100m, takeProfit: 115m, atr: 2m);

        Assert.NotNull(snapped);
        Assert.Equal(109.5m, snapped);
        Assert.Contains("resistance 110", note);
    }

    [Fact]
    public void Sr_SnapTakeProfit_RefusesWhenRewardCollapses_AndWarns()
    {
        var signals = new SrSignals(50m, null, new SrLevel(110m, 3), "");

        var (snapped, note) = SupportResistanceLevels.SnapTakeProfit(
            signals, isLong: true, entry: 100m, takeProfit: 140m, atr: 2m);

        Assert.Null(snapped);
        Assert.Contains("optimistic", note);
    }

    [Fact]
    public void Sr_SnapTakeProfit_LeavesTpInsideTheWallAlone()
    {
        var signals = new SrSignals(50m, null, new SrLevel(110m, 3), "");

        var (snapped, note) = SupportResistanceLevels.SnapTakeProfit(
            signals, isLong: true, entry: 100m, takeProfit: 108m, atr: 2m);

        Assert.Null(snapped);
        Assert.Null(note);
    }
}
