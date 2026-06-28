namespace CryptoHft.Application.DecisionEngine;

public sealed record SmcSignals(
    decimal Score,             // 0-100 (bullish > 50)
    bool BullishOrderBlock,
    bool BearishOrderBlock,
    bool BullishFvg,
    bool BearishFvg,
    bool LiquiditySweepLow,    // swept sell-side liquidity then reversed up (bullish)
    bool LiquiditySweepHigh,   // swept buy-side liquidity then reversed down (bearish)
    string Summary);

// Smart Money Concepts detection: order blocks, fair value gaps, and liquidity sweeps.
// Operates on a candle series; designed for the entry timeframe (e.g. 15m/1h).
public static class SmartMoneyConcepts
{
    public static SmcSignals Detect(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 20)
            return new SmcSignals(50, false, false, false, false, false, false, "Insufficient data");

        var n = candles.Count;
        var atr = TechnicalIndicators.Atr(candles)[^1];

        var (bullOb, bearOb) = DetectOrderBlock(candles, atr);
        var (bullFvg, bearFvg) = DetectFairValueGap(candles, atr);
        var (sweepLow, sweepHigh) = DetectLiquiditySweep(candles, atr);

        // Aggregate into a directional score
        var score = 50m;
        if (bullOb) score += 12; if (bearOb) score -= 12;
        if (bullFvg) score += 10; if (bearFvg) score -= 10;
        if (sweepLow) score += 15; if (sweepHigh) score -= 15;
        score = Math.Clamp(score, 0m, 100m);

        var parts = new List<string>();
        if (bullOb) parts.Add("bullish OB");
        if (bearOb) parts.Add("bearish OB");
        if (bullFvg) parts.Add("bullish FVG");
        if (bearFvg) parts.Add("bearish FVG");
        if (sweepLow) parts.Add("liquidity sweep low (bullish)");
        if (sweepHigh) parts.Add("liquidity sweep high (bearish)");
        var summary = parts.Count > 0 ? string.Join(", ", parts) : "no SMC pattern";

        return new SmcSignals(score, bullOb, bearOb, bullFvg, bearFvg, sweepLow, sweepHigh, summary);
    }

    // Order block: the last opposite candle before a strong displacement move.
    private static (bool bull, bool bear) DetectOrderBlock(IReadOnlyList<Candle> candles, decimal atr)
    {
        var n = candles.Count;
        bool bull = false, bear = false;
        // Look at the last ~10 candles for a displacement leg
        for (var i = Math.Max(1, n - 10); i < n; i++)
        {
            var body = Math.Abs(candles[i].Close - candles[i].Open);
            if (body < atr * 1.2m) continue; // need a strong impulse candle

            var prev = candles[i - 1];
            var impulseUp = candles[i].Close > candles[i].Open;
            // Bullish OB: down candle immediately before an up impulse
            if (impulseUp && prev.Close < prev.Open) bull = true;
            // Bearish OB: up candle immediately before a down impulse
            if (!impulseUp && prev.Close > prev.Open) bear = true;
        }
        return (bull, bear);
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
