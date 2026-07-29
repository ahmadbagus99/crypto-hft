namespace CryptoHft.Application.DecisionEngine;

public enum ChartPattern
{
    None,
    AscendingTriangle,
    DescendingTriangle,
    SymmetricalTriangle,
    Rectangle
}

public sealed record PatternSignals(
    decimal Score,           // 0-100 (bullish > 50)
    ChartPattern Pattern,
    int BreakoutDirection,   // +1 broke out up, -1 broke out down, 0 still inside
    decimal? MeasuredTarget, // breakout price +/- pattern height; null while inside
    string Summary);

// Classical consolidation patterns from fractal swing points: ascending / descending /
// symmetrical triangles and rectangles. The boundary trendlines are fitted through the
// last three swing highs and lows; a pattern only forms while both boundaries hold.
// Inside the pattern the score carries the textbook bias (flat top + rising lows leans
// bullish, mirror bearish, symmetrical/rectangle neutral); the decisive vote is the
// BREAKOUT — a close beyond the projected boundary, weighted up when volume expands.
// The measured-move target (pattern height projected from the break) is exposed as TP
// context only.
public static class ChartPatternDetector
{
    private const decimal FlatSlopeAtr = 0.04m;   // |slope| per candle below this = flat
    private const decimal BreakBufferAtr = 0.10m; // close must clear the boundary by this

    public static PatternSignals Detect(IReadOnlyList<Candle> candles, decimal atr)
    {
        if (candles.Count < 30 || atr <= 0)
            return None("insufficient data");

        var swings = SmartMoneyConcepts.DetectSwings(candles);
        var highs = swings.Where(s => s.IsHigh).TakeLast(3).ToList();
        var lows = swings.Where(s => !s.IsHigh).TakeLast(3).ToList();
        if (highs.Count < 3 || lows.Count < 3)
            return None("not enough swing points");

        // Boundary slopes in ATR per candle (first -> last swing of each side).
        var slopeH = Slope(highs[0], highs[^1]) / atr;
        var slopeL = Slope(lows[0], lows[^1]) / atr;

        var flatH = Math.Abs(slopeH) < FlatSlopeAtr;
        var flatL = Math.Abs(slopeL) < FlatSlopeAtr;
        var risingL = slopeL >= FlatSlopeAtr;
        var fallingH = slopeH <= -FlatSlopeAtr;

        var pattern =
            flatH && risingL ? ChartPattern.AscendingTriangle :
            fallingH && flatL ? ChartPattern.DescendingTriangle :
            fallingH && risingL ? ChartPattern.SymmetricalTriangle :
            flatH && flatL ? ChartPattern.Rectangle :
            ChartPattern.None;

        if (pattern == ChartPattern.None)
            return None("no consolidation pattern (trending swings)");

        // Project both boundaries to the current candle and check for a decisive close
        // beyond them. Only the most recent candle decides: an old breakout is already
        // in the price and the trend factors own it from there.
        var lastIndex = candles.Count - 1;
        var upper = Project(highs[^1], slopeH * atr, lastIndex);
        var lower = Project(lows[^1], slopeL * atr, lastIndex);
        var close = candles[^1].Close;

        var breakout = close > upper + atr * BreakBufferAtr ? 1
            : close < lower - atr * BreakBufferAtr ? -1
            : 0;

        // Pattern height from the span of all six swings — the classical measured move.
        var height = Math.Max(highs.Max(h => h.Price) - lows.Min(l => l.Price), atr);
        decimal? target = breakout switch
        {
            1 => close + height,
            -1 => close - height,
            _ => null
        };

        var volumeConfirmed = breakout != 0 && VolumeExpanding(candles);
        var score = 50m + PatternBias(pattern, breakout, volumeConfirmed);
        score = Math.Clamp(score, 0m, 100m);

        var state = breakout switch
        {
            1 => $"breakout UP{(volumeConfirmed ? " on expanding volume" : " (volume weak)")}, measured target {target:F0}",
            -1 => $"breakout DOWN{(volumeConfirmed ? " on expanding volume" : " (volume weak)")}, measured target {target:F0}",
            _ => $"forming (upper {upper:F0} / lower {lower:F0})"
        };

        return new PatternSignals(score, pattern, breakout, target,
            $"{PatternName(pattern)} {state}");
    }

    // Textbook bias: inside the pattern, a directional triangle leans its way (pressure
    // building against the flat boundary); the breakout is the real signal and volume
    // expansion strengthens it. A breakout AGAINST a triangle's lean (e.g. an ascending
    // triangle breaking down) is a trap-side move and still scores full weight downward.
    internal static decimal PatternBias(ChartPattern pattern, int breakout, bool volumeConfirmed)
    {
        if (breakout != 0)
        {
            var strength = 12m + (volumeConfirmed ? 5m : 0m);
            return breakout * strength;
        }
        return pattern switch
        {
            ChartPattern.AscendingTriangle => 6m,
            ChartPattern.DescendingTriangle => -6m,
            _ => 0m
        };
    }

    private static decimal Slope(SmartMoneyConcepts.SwingPoint a, SmartMoneyConcepts.SwingPoint b)
        => b.Index == a.Index ? 0m : (b.Price - a.Price) / (b.Index - a.Index);

    private static decimal Project(SmartMoneyConcepts.SwingPoint from, decimal slopePerCandle, int toIndex)
        => from.Price + slopePerCandle * (toIndex - from.Index);

    private static bool VolumeExpanding(IReadOnlyList<Candle> candles)
    {
        var volumes = candles.Select(c => c.Volume).ToList();
        var sma = TechnicalIndicators.Sma(volumes, 20)[^1];
        return sma > 0 && volumes[^1] > sma * 1.3m;
    }

    private static string PatternName(ChartPattern p) => p switch
    {
        ChartPattern.AscendingTriangle => "ascending triangle",
        ChartPattern.DescendingTriangle => "descending triangle",
        ChartPattern.SymmetricalTriangle => "symmetrical triangle",
        ChartPattern.Rectangle => "rectangle",
        _ => "none"
    };

    private static PatternSignals None(string reason)
        => new(50m, ChartPattern.None, 0, null, reason);
}
