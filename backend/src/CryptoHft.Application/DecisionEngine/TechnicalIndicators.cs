namespace CryptoHft.Application.DecisionEngine;

// Pure, dependency-free indicator math operating on decimal series.
// All functions return a series aligned to the input length (warm-up values filled forward).
public static class TechnicalIndicators
{
    public static decimal[] Ema(IReadOnlyList<decimal> values, int period)
    {
        var result = new decimal[values.Count];
        if (values.Count == 0) return result;
        var k = 2m / (period + 1);
        var ema = values[0];
        for (var i = 0; i < values.Count; i++)
        {
            ema = (values[i] - ema) * k + ema;
            result[i] = ema;
        }
        return result;
    }

    public static decimal[] Sma(IReadOnlyList<decimal> values, int period)
    {
        var result = new decimal[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var start = Math.Max(0, i - period + 1);
            var count = i - start + 1;
            decimal sum = 0;
            for (var j = start; j <= i; j++) sum += values[j];
            result[i] = sum / count;
        }
        return result;
    }

    public static decimal[] Rsi(IReadOnlyList<decimal> closes, int period = 14)
    {
        var result = new decimal[closes.Count];
        for (var i = 0; i < closes.Count; i++) result[i] = 50m;
        for (var i = period; i < closes.Count; i++)
        {
            decimal gains = 0, losses = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                var change = closes[j] - closes[j - 1];
                if (change >= 0) gains += change; else losses += Math.Abs(change);
            }
            result[i] = losses == 0 ? 100m : 100m - 100m / (1m + gains / losses);
        }
        return result;
    }

    public static (decimal[] macd, decimal[] signal, decimal[] hist) Macd(
        IReadOnlyList<decimal> closes, int fast = 12, int slow = 26, int signalPeriod = 9)
    {
        var emaFast = Ema(closes, fast);
        var emaSlow = Ema(closes, slow);
        var macd = new decimal[closes.Count];
        for (var i = 0; i < closes.Count; i++) macd[i] = emaFast[i] - emaSlow[i];
        var signal = Ema(macd, signalPeriod);
        var hist = new decimal[closes.Count];
        for (var i = 0; i < closes.Count; i++) hist[i] = macd[i] - signal[i];
        return (macd, signal, hist);
    }

    public static decimal[] Atr(IReadOnlyList<Candle> candles, int period = 14)
    {
        var tr = new decimal[candles.Count];
        if (candles.Count == 0) return tr;
        tr[0] = candles[0].High - candles[0].Low;
        for (var i = 1; i < candles.Count; i++)
        {
            var hl = candles[i].High - candles[i].Low;
            var hc = Math.Abs(candles[i].High - candles[i - 1].Close);
            var lc = Math.Abs(candles[i].Low - candles[i - 1].Close);
            tr[i] = Math.Max(hl, Math.Max(hc, lc));
        }
        return Sma(tr, period);
    }

    public static decimal[] Adx(IReadOnlyList<Candle> candles, int period = 14)
    {
        var result = new decimal[candles.Count];
        if (candles.Count < period + 1) return result;
        var plusDm = new decimal[candles.Count];
        var minusDm = new decimal[candles.Count];
        var tr = new decimal[candles.Count];
        for (var i = 1; i < candles.Count; i++)
        {
            var up = candles[i].High - candles[i - 1].High;
            var down = candles[i - 1].Low - candles[i].Low;
            plusDm[i] = up > down && up > 0 ? up : 0;
            minusDm[i] = down > up && down > 0 ? down : 0;
            var hl = candles[i].High - candles[i].Low;
            var hc = Math.Abs(candles[i].High - candles[i - 1].Close);
            var lc = Math.Abs(candles[i].Low - candles[i - 1].Close);
            tr[i] = Math.Max(hl, Math.Max(hc, lc));
        }
        var smTr = Sma(tr, period);
        var smPlus = Sma(plusDm, period);
        var smMinus = Sma(minusDm, period);
        var dx = new decimal[candles.Count];
        for (var i = 0; i < candles.Count; i++)
        {
            if (smTr[i] == 0) { dx[i] = 0; continue; }
            var plusDi = 100m * smPlus[i] / smTr[i];
            var minusDi = 100m * smMinus[i] / smTr[i];
            var sum = plusDi + minusDi;
            dx[i] = sum == 0 ? 0 : 100m * Math.Abs(plusDi - minusDi) / sum;
        }
        return Sma(dx, period);
    }

    public static (decimal upper, decimal middle, decimal lower) BollingerBands(
        IReadOnlyList<decimal> closes, int period = 20, decimal mult = 2m)
    {
        if (closes.Count == 0) return (0, 0, 0);
        var start = Math.Max(0, closes.Count - period);
        var window = new List<decimal>();
        for (var i = start; i < closes.Count; i++) window.Add(closes[i]);
        var mean = window.Average();
        var variance = window.Sum(v => (v - mean) * (v - mean)) / window.Count;
        var sd = Sqrt(variance);
        return (mean + mult * sd, mean, mean - mult * sd);
    }

    public static decimal StochasticK(IReadOnlyList<Candle> candles, int period = 14)
    {
        if (candles.Count == 0) return 50m;
        var start = Math.Max(0, candles.Count - period);
        decimal high = decimal.MinValue, low = decimal.MaxValue;
        for (var i = start; i < candles.Count; i++)
        {
            high = Math.Max(high, candles[i].High);
            low = Math.Min(low, candles[i].Low);
        }
        var close = candles[^1].Close;
        return high > low ? (close - low) / (high - low) * 100m : 50m;
    }

    public static decimal Vwap(IReadOnlyList<Candle> candles, int period = 20)
    {
        if (candles.Count == 0) return 0;
        var start = Math.Max(0, candles.Count - period);
        decimal pv = 0, vol = 0;
        for (var i = start; i < candles.Count; i++)
        {
            var typical = (candles[i].High + candles[i].Low + candles[i].Close) / 3m;
            pv += typical * candles[i].Volume;
            vol += candles[i].Volume;
        }
        return vol == 0 ? candles[^1].Close : pv / vol;
    }

    public static decimal Obv(IReadOnlyList<Candle> candles)
    {
        decimal obv = 0;
        for (var i = 1; i < candles.Count; i++)
        {
            if (candles[i].Close > candles[i - 1].Close) obv += candles[i].Volume;
            else if (candles[i].Close < candles[i - 1].Close) obv -= candles[i].Volume;
        }
        return obv;
    }

    // Swing-based market structure: returns +1 (higher highs/lows), -1 (lower), 0 (mixed)
    public static int MarketStructure(IReadOnlyList<Candle> candles, int lookback = 20)
    {
        if (candles.Count < lookback * 2) return 0;
        var recent = candles.Skip(candles.Count - lookback).ToList();
        var prev = candles.Skip(candles.Count - lookback * 2).Take(lookback).ToList();
        var recentHigh = recent.Max(c => c.High);
        var recentLow = recent.Min(c => c.Low);
        var prevHigh = prev.Max(c => c.High);
        var prevLow = prev.Min(c => c.Low);
        if (recentHigh > prevHigh && recentLow > prevLow) return 1;
        if (recentHigh < prevHigh && recentLow < prevLow) return -1;
        return 0;
    }

    public static decimal Sqrt(decimal value)
        => value <= 0 ? 0 : (decimal)Math.Sqrt((double)value);
}
