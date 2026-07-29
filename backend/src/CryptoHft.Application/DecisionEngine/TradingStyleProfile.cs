using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.DecisionEngine;

// How the engine looks at the market for a given trading style. The style never changes
// WHAT is analyzed (same factors, categories, and learning keys) — only the lens:
// which timeframe anchors geometry/regime/levels, how the per-timeframe votes are
// weighted, and the SL/TP baseline when the learned intraday tuning does not apply.
//
// Intraday (default): the original behavior — 1h anchor, targets 1-2%, holds hours.
// Scalper: 15m anchor with the vote mass shifted to 5m/15m; SL 1.5x / TP 3x ATR(15m)
// keeps RR at 2.0 with targets ~0.4-0.8% — comfortably above the ~0.1% round-trip
// taker fees, which is the floor that makes tighter scalps mathematically unviable.
// Scalper bypasses the learned execution tuning: those multipliers were learned from
// realized 1h-geometry exits and would mis-scale a 15m stop.
public sealed record TradingStyleProfile(
    string Name,
    string PrimaryInterval,          // anchors ATR, regime, fib/pattern/S&R/volume profile
    string StructureInterval,        // SMC entry timeframe (order blocks, FVG, BOS/CHoCH)
    IReadOnlyList<(string Interval, decimal Weight)> VoteWeights,
    decimal FallbackSlAtrMultiplier, // used when UseLearnedTuning is false
    decimal FallbackTpAtrMultiplier,
    bool UseLearnedTuning)
{
    public static readonly TradingStyleProfile Intraday = new(
        "intraday", "1h", "15m",
        new[] { ("5m", 0.10m), ("15m", 0.20m), ("1h", 0.30m), ("4h", 0.25m), ("1d", 0.15m) },
        FallbackSlAtrMultiplier: 2m,
        FallbackTpAtrMultiplier: 4m,
        UseLearnedTuning: true);

    public static readonly TradingStyleProfile Scalper = new(
        "scalper", "15m", "5m",
        new[] { ("5m", 0.30m), ("15m", 0.35m), ("1h", 0.25m), ("4h", 0.10m) },
        FallbackSlAtrMultiplier: 1.5m,
        FallbackTpAtrMultiplier: 3m,
        UseLearnedTuning: false);

    public static TradingStyleProfile For(TradingStyle style)
        => style == TradingStyle.Scalper ? Scalper : Intraday;
}
