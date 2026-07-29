namespace CryptoHft.Application.DecisionEngine;

public sealed record FibSignals(
    decimal Score,          // 0-100 (bullish > 50): pullback into a fib zone of the last impulse
    bool ImpulseUp,         // direction of the impulse being retraced
    decimal RetraceRatio,   // 0 = at the impulse extreme, 1 = fully retraced
    decimal Extension1272,  // 1.272 extension of the impulse (continuation target)
    decimal Extension1618,  // 1.618 extension of the impulse
    string Summary);

// Fibonacci retracement of the most recent significant impulse on the analysis timeframe.
// The trade idea it encodes: a pullback into the classic retracement zones (0.382 / 0.5 /
// golden pocket 0.618-0.65 / 0.786) of an impulse is a continuation entry WITH that
// impulse — deepest edge at the golden pocket. A retracement beyond ~0.9 means the
// impulse failed, which flips the read against it. Extensions (1.272 / 1.618) are
// exposed as continuation-target context for TP placement, never as a directional vote.
public static class FibonacciAnalysis
{
    private const int Window = 60;              // candles forming the dealing range
    private const decimal MinRangeAtr = 3m;     // impulse must be significant vs ATR

    public static FibSignals Analyze(IReadOnlyList<Candle> candles, decimal atr)
    {
        if (candles.Count < 20 || atr <= 0)
            return Neutral("insufficient data");

        var window = candles.Skip(Math.Max(0, candles.Count - Window)).ToList();
        int hiIdx = 0, loIdx = 0;
        for (var i = 1; i < window.Count; i++)
        {
            if (window[i].High > window[hiIdx].High) hiIdx = i;
            if (window[i].Low < window[loIdx].Low) loIdx = i;
        }
        var hi = window[hiIdx].High;
        var lo = window[loIdx].Low;
        var range = hi - lo;
        if (range < atr * MinRangeAtr)
            return Neutral($"range {range / atr:F1}xATR too small for fib structure");

        // The more recent extreme ends the impulse; the other one starts it.
        var impulseUp = hiIdx > loIdx;
        var close = candles[^1].Close;
        var retrace = impulseUp
            ? (hi - close) / range      // pullback down from the high
            : (close - lo) / range;     // pullback up from the low
        retrace = Math.Clamp(retrace, 0m, 1.5m);

        var (ext1272, ext1618) = impulseUp
            ? (lo + range * 1.272m, lo + range * 1.618m)
            : (hi - range * 1.272m, hi - range * 1.618m);

        var (bias, zone) = ZoneBias(retrace);
        // The zone supports the impulse direction: bullish for an up-impulse pullback,
        // bearish for a down-impulse pullback (price rallying into fib resistance).
        var score = Math.Clamp(50m + (impulseUp ? bias : -bias), 0m, 100m);

        var dir = impulseUp ? "up" : "down";
        var summary =
            $"{dir}-impulse {(impulseUp ? lo : hi):F0}->{(impulseUp ? hi : lo):F0}, " +
            $"retrace {retrace:F2} ({zone}); ext 1.272 {ext1272:F0} / 1.618 {ext1618:F0}";

        return new FibSignals(score, impulseUp, Math.Round(retrace, 3),
            Math.Round(ext1272, 2), Math.Round(ext1618, 2), summary);
    }

    // Bias magnitude by retracement zone (applied toward the impulse direction).
    // Golden pocket carries the most weight; a retracement past ~0.9 invalidates the
    // impulse and votes against it. Shallow pullbacks (< 0.30) have no fib edge yet.
    internal static (decimal Bias, string Zone) ZoneBias(decimal retrace) => retrace switch
    {
        >= 0.90m => (-6m, "impulse invalidated"),
        >= 0.74m and <= 0.82m => (6m, "0.786 zone"),
        >= 0.585m and <= 0.68m => (12m, "golden pocket 0.618-0.65"),
        >= 0.45m and <= 0.55m => (8m, "0.5 zone"),
        >= 0.34m and <= 0.42m => (6m, "0.382 zone"),
        < 0.30m => (0m, "shallow pullback"),
        _ => (0m, "between zones")
    };

    private static FibSignals Neutral(string reason)
        => new(50m, false, 0m, 0m, 0m, reason);
}
