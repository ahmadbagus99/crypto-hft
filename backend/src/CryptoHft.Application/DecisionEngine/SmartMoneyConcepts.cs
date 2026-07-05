namespace CryptoHft.Application.DecisionEngine;

public sealed record SmcSignals(
    decimal Score,             // 0-100 (bullish > 50)
    bool BullishOrderBlock,    // fresh (unmitigated) OB supporting longs within reach
    bool BearishOrderBlock,
    bool BullishFvg,
    bool BearishFvg,
    bool LiquiditySweepLow,    // swept sell-side liquidity then reversed up (bullish)
    bool LiquiditySweepHigh,   // swept buy-side liquidity then reversed down (bearish)
    bool BullishBos,           // break of structure WITH the up-trend (continuation)
    bool BearishBos,           // break of structure WITH the down-trend (continuation)
    bool BullishChoch,         // change of character AGAINST the down-trend (reversal warning)
    bool BearishChoch,         // change of character AGAINST the up-trend (reversal warning)
    decimal RangePosition,     // 0 = dealing-range low … 1 = high; <=0.4 discount, >=0.6 premium
    string Summary);

// Smart Money Concepts detection: order blocks (with mitigation tracking), fair value
// gaps, liquidity sweeps, break of structure / change of character from fractal swing
// points, and premium/discount positioning inside the dealing range.
// Operates on a candle series; designed for the entry timeframe (e.g. 15m/1h).
public static class SmartMoneyConcepts
{
    public static SmcSignals Detect(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 20)
            return new SmcSignals(50, false, false, false, false, false, false,
                false, false, false, false, 0.5m, "Insufficient data");

        var atr = TechnicalIndicators.Atr(candles)[^1];

        var (bullOb, bearOb) = DetectOrderBlocks(candles, atr);
        var (bullFvg, bearFvg) = DetectFairValueGap(candles, atr);
        var (sweepLow, sweepHigh) = DetectLiquiditySweep(candles, atr);
        var swings = DetectSwings(candles);
        var (bullBos, bearBos, bullChoch, bearChoch) = DetectStructureBreaks(candles, swings);
        var rangePos = RangePosition(candles);

        // Aggregate into a directional score. CHoCH weighs heaviest (structure just turned
        // against the prevailing trend); sweeps and BOS next; a fresh OB and FVG add
        // confluence; premium/discount is a mild positional tilt.
        var score = 50m;
        if (bullOb) score += 10; if (bearOb) score -= 10;
        if (bullFvg) score += 8; if (bearFvg) score -= 8;
        if (sweepLow) score += 12; if (sweepHigh) score -= 12;
        if (bullBos) score += 10; if (bearBos) score -= 10;
        if (bullChoch) score += 14; if (bearChoch) score -= 14;
        if (rangePos <= 0.4m) score += 6; else if (rangePos >= 0.6m) score -= 6;
        score = Math.Clamp(score, 0m, 100m);

        var parts = new List<string>();
        if (bullOb) parts.Add("fresh bullish OB");
        if (bearOb) parts.Add("fresh bearish OB");
        if (bullFvg) parts.Add("bullish FVG");
        if (bearFvg) parts.Add("bearish FVG");
        if (sweepLow) parts.Add("liquidity sweep low (bullish)");
        if (sweepHigh) parts.Add("liquidity sweep high (bearish)");
        if (bullBos) parts.Add("bullish BOS");
        if (bearBos) parts.Add("bearish BOS");
        if (bullChoch) parts.Add("bullish CHoCH (reversal)");
        if (bearChoch) parts.Add("bearish CHoCH (reversal)");
        parts.Add(rangePos <= 0.4m ? $"discount zone ({rangePos:F2})"
            : rangePos >= 0.6m ? $"premium zone ({rangePos:F2})"
            : $"equilibrium ({rangePos:F2})");
        var summary = string.Join(", ", parts);

        return new SmcSignals(score, bullOb, bearOb, bullFvg, bearFvg, sweepLow, sweepHigh,
            bullBos, bearBos, bullChoch, bearChoch, rangePos, summary);
    }

    internal readonly record struct SwingPoint(int Index, decimal Price, bool IsHigh);

    // Fractal swing points: a high (low) that stands strictly above (below) `wing`
    // neighbors on each side. The last `wing` candles can never be swings yet, which is
    // exactly what BOS detection needs: a breakout close has no swing of its own.
    internal static List<SwingPoint> DetectSwings(IReadOnlyList<Candle> candles, int wing = 2)
    {
        var swings = new List<SwingPoint>();
        var start = Math.Max(wing, candles.Count - 80);
        for (var i = start; i < candles.Count - wing; i++)
        {
            bool isHigh = true, isLow = true;
            for (var k = 1; k <= wing; k++)
            {
                if (candles[i].High <= candles[i - k].High || candles[i].High <= candles[i + k].High) isHigh = false;
                if (candles[i].Low >= candles[i - k].Low || candles[i].Low >= candles[i + k].Low) isLow = false;
            }
            if (isHigh) swings.Add(new SwingPoint(i, candles[i].High, true));
            if (isLow) swings.Add(new SwingPoint(i, candles[i].Low, false));
        }
        return swings;
    }

    // BOS: the close takes out the most recent swing extreme IN the trend's direction
    // (continuation). CHoCH: the close takes out the protective swing AGAINST the trend
    // (an uptrend losing its last higher low / a downtrend reclaiming its last lower
    // high) — the classic first warning of reversal.
    internal static (bool BullBos, bool BearBos, bool BullChoch, bool BearChoch) DetectStructureBreaks(
        IReadOnlyList<Candle> candles, List<SwingPoint> swings)
    {
        var highs = swings.Where(s => s.IsHigh).ToList();
        var lows = swings.Where(s => !s.IsHigh).ToList();
        if (highs.Count < 2 || lows.Count < 2) return (false, false, false, false);

        var lastHigh = highs[^1].Price;
        var prevHigh = highs[^2].Price;
        var lastLow = lows[^1].Price;
        var prevLow = lows[^2].Price;
        var trendUp = lastHigh > prevHigh && lastLow > prevLow;   // HH + HL
        var trendDown = lastHigh < prevHigh && lastLow < prevLow; // LH + LL
        var close = candles[^1].Close;

        return (
            BullBos: trendUp && close > lastHigh,
            BearBos: trendDown && close < lastLow,
            BullChoch: trendDown && close > lastHigh,
            BearChoch: trendUp && close < lastLow);
    }

    // Order block: the last opposite candle before a strong displacement move. Only FRESH
    // blocks count — once price has traded back into the zone it is mitigated (the resting
    // orders were consumed) and teaches nothing. The zone must also still be within reach
    // (5xATR) and on the supporting side of price to be relevant to this entry.
    internal static (bool bull, bool bear) DetectOrderBlocks(IReadOnlyList<Candle> candles, decimal atr)
    {
        var n = candles.Count;
        var price = candles[^1].Close;
        bool bull = false, bear = false;

        for (var i = Math.Max(1, n - 30); i < n; i++)
        {
            var impulse = candles[i];
            var body = Math.Abs(impulse.Close - impulse.Open);
            if (body < atr * 1.2m) continue; // need a strong impulse candle

            var prev = candles[i - 1];
            var impulseUp = impulse.Close > impulse.Open;
            var zoneLow = Math.Min(prev.Open, prev.Close);
            var zoneHigh = Math.Max(prev.Open, prev.Close);

            // Bullish OB: down candle immediately before an up impulse
            if (impulseUp && prev.Close < prev.Open)
            {
                var mitigated = Enumerable.Range(i + 1, n - i - 1).Any(j => candles[j].Low <= zoneHigh);
                var relevant = price > zoneHigh && price - zoneHigh <= atr * 5m;
                if (!mitigated && relevant) bull = true;
            }
            // Bearish OB: up candle immediately before a down impulse
            if (!impulseUp && prev.Close > prev.Open)
            {
                var mitigated = Enumerable.Range(i + 1, n - i - 1).Any(j => candles[j].High >= zoneLow);
                var relevant = price < zoneLow && zoneLow - price <= atr * 5m;
                if (!mitigated && relevant) bear = true;
            }
        }
        return (bull, bear);
    }

    // Position of the last close inside the dealing range of the recent window.
    // Discount (<= 0.4) favors longs, premium (>= 0.6) favors shorts: buy cheap half,
    // sell expensive half of the range.
    internal static decimal RangePosition(IReadOnlyList<Candle> candles)
    {
        var window = candles.Skip(Math.Max(0, candles.Count - 60)).ToList();
        var high = window.Max(c => c.High);
        var low = window.Min(c => c.Low);
        return high == low ? 0.5m : Math.Clamp((candles[^1].Close - low) / (high - low), 0m, 1m);
    }

    // Fair value gap (imbalance): 3-candle pattern where candle1.high < candle3.low (bullish gap)
    // or candle1.low > candle3.high (bearish gap), with a strong middle candle.
    private static (bool bull, bool bear) DetectFairValueGap(IReadOnlyList<Candle> candles, decimal atr)
    {
        var n = candles.Count;
        bool bull = false, bear = false;
        for (var i = Math.Max(2, n - 8); i < n; i++)
        {
            var c1 = candles[i - 2];
            var c3 = candles[i];
            if (c1.High < c3.Low && c3.Low - c1.High > atr * 0.3m) bull = true;
            if (c1.Low > c3.High && c1.Low - c3.High > atr * 0.3m) bear = true;
        }
        return (bull, bear);
    }

    // Liquidity sweep: price wicks beyond a recent swing high/low then closes back inside,
    // signalling stop-hunt and likely reversal.
    private static (bool low, bool high) DetectLiquiditySweep(IReadOnlyList<Candle> candles, decimal atr)
    {
        var n = candles.Count;
        var lookback = Math.Min(20, n - 2);
        var window = candles.Skip(n - lookback - 1).Take(lookback).ToList();
        if (window.Count == 0) return (false, false);

        var swingHigh = window.Max(c => c.High);
        var swingLow = window.Min(c => c.Low);
        var last = candles[^1];

        // Sweep low: wick pierced below swing low but closed back above it
        var sweepLow = last.Low < swingLow && last.Close > swingLow && (swingLow - last.Low) > atr * 0.2m;
        // Sweep high: wick pierced above swing high but closed back below it
        var sweepHigh = last.High > swingHigh && last.Close < swingHigh && (last.High - swingHigh) > atr * 0.2m;
        return (sweepLow, sweepHigh);
    }
}
