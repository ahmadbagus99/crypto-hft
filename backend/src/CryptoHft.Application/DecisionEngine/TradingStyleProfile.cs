using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.DecisionEngine;

// How the engine looks at the market for a given trading style. The style never changes
// WHAT is analyzed (same factors, categories, and learning keys) — only the lens:
// which timeframe anchors geometry/regime/levels, how the per-timeframe votes are
// weighted, and the SL/TP baseline when the learned intraday tuning does not apply.
//
// Intraday (default): the original behavior — 1h anchor, targets 1-2%, holds hours.
// Scalper: 15m anchor with the vote mass shifted to 5m/15m.
// Scalper bypasses the learned execution tuning: those multipliers were learned from
// realized 1h-geometry exits and would mis-scale a 15m stop.
//
// Scalper geometry is set from fee arithmetic, not from a round number. A round trip
// costs ~0.1% of notional (taker in, taker out), which is charged whole regardless of
// how small the target is — so it shrinks a tight target far more than a wide one. At
// SL 1.5xATR(15m) the target needed for a NET 2:1 is 2*risk + 3*fee, which lands near
// 4.5xATR — not the 3x first shipped, which only cleared ~1.2:1 after fees and needed
// a 45% win rate just to break even. Anything tighter is rent paid to the exchange.
public sealed record TradingStyleProfile(
    string Name,
    string PrimaryInterval,          // anchors ATR, regime, fib/pattern/S&R/volume profile
    string StructureInterval,        // SMC entry timeframe (order blocks, FVG, BOS/CHoCH)
    IReadOnlyList<(string Interval, decimal Weight)> VoteWeights,
    decimal FallbackSlAtrMultiplier, // used when UseLearnedTuning is false
    decimal FallbackTpAtrMultiplier,
    bool UseLearnedTuning,
    // Scalper only: run the ordered direction -> location -> timing gates instead of letting
    // the blended score decide on its own. Intraday leaves this false and is untouched.
    bool UsesSequentialGate = false,
    // Which series the timing gate reads for the reversal bar. Ignored unless the gate runs.
    string TimingInterval = "1m",
    // The reward:risk the style is built around, net of fees. Per style because the fee is a
    // fixed 0.1% of notional however small the target: at intraday distances it is a rounding
    // error, at scalper distances it is most of the edge. Holding both to 2:1 is what forced
    // the scalper stop inside the noise band in the first place — the ratio looked healthy
    // only because the risk denominator was too small to survive. A caution that fires on
    // every single trade teaches the auditor to distrust everything, so the threshold has to
    // describe the style it is judging.
    decimal MinimumRiskReward = 2m)
{
    public static readonly TradingStyleProfile Intraday = new(
        "intraday", "1h", "15m",
        new[] { ("5m", 0.10m), ("15m", 0.20m), ("1h", 0.30m), ("4h", 0.25m), ("1d", 0.15m) },
        FallbackSlAtrMultiplier: 2m,
        FallbackTpAtrMultiplier: 4m,
        UseLearnedTuning: true);

    // The scalper's stop was measured losing on real entries (4-7 Aug: t = -2.62 over 20
    // trades, 15 stopped out and 1 target reached). 1.5xATR put the stop at 0.328% of entry
    // while the ordinary drawdown in the four hours after entry ran 0.450% — inside the noise,
    // so 65% of entries were stopped by a move that meant nothing. 2.5xATR clears that band.
    // The target comes down with it: at SL 2.5 a NET 2:1 after ~0.1% round-trip fees needs
    // roughly 3xATR, and widening both while keeping a 3:1 ratio would have pushed the median
    // hold past five hours, which is no longer scalping.
    public static readonly TradingStyleProfile Scalper = new(
        "scalper", "15m", "5m",
        new[] { ("5m", 0.30m), ("15m", 0.35m), ("1h", 0.25m), ("4h", 0.10m) },
        FallbackSlAtrMultiplier: 2.5m,
        FallbackTpAtrMultiplier: 3m,
        UseLearnedTuning: false,
        UsesSequentialGate: true,
        TimingInterval: "1m",
        // 2.5xATR risk against a 3xATR target nets 0.83:1 after fees, so this geometry needs
        // to win about 55% of the time. That is a demanding bar and it is stated rather than
        // hidden: the alternative was a 6.5xATR target, which is a 1.28% move that price
        // reached in roughly a fifth of the hours measured. Neither is comfortable — the fee
        // is simply large relative to the distances a scalper works in, and switching entries
        // to maker is the lever that moves this number, not the geometry.
        MinimumRiskReward: 0.8m);

    public static TradingStyleProfile For(TradingStyle style)
        => style == TradingStyle.Scalper ? Scalper : Intraday;
}
