using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.DecisionEngine;

public sealed record BrrSignals(
    bool Detected,
    TradeSide? Side,
    decimal Level,
    int BarsSinceBreak,
    decimal Displacement,     // breakout body in ATR — how decisive the break was
    decimal PullbackDecay,    // pullback body vs breakout body; below 1 means momentum faded
    bool RejectionConfirmed,  // a bar has interacted with the level and closed back the right side
    decimal RejectionWick,    // share of that bar's range spent as wick through the level
    string Summary);

// Break → Retest → Rejection, as a sequence rather than three unrelated observations.
//
// The engine already saw each piece: SupportResistanceLevels reports "broke above 64200" on
// the candle that breaks, and "rejection wick off support" on a candle that pierces and closes
// back. What it never had was memory connecting them — that the level being rejected now is
// the one that broke eight bars ago. Grepping the whole DecisionEngine for "retest" returned
// nothing.
//
// WHAT THE SOURCE MATERIAL ACTUALLY SPECIFIES (owner's walkthrough of the BRR video):
//   - the break must carry real momentum, shown as large-bodied candles, not a graze
//   - the return leg should be WEAKER than the break — that fading is the tell
//   - price must interact with the level, and a wick through it is normal and expected
//   - the entry trigger is a STRONG rejection, not the touch; a bare wick is not enough
//   - the close should come back on the rejection's side
//
// WHAT IT DOES NOT SPECIFY, AND IS THEREFORE OURS TO CHOOSE AND TO BACKTEST:
//   - how many bars a retest may take
//   - how wide the zone around the level is
//   - the numeric thresholds for "strong" and "weak"
// Those four constants are named below so they read as engineering parameters, not as
// received wisdom. Every one of them is a candidate for fitting once enough trades carry the
// outcome.
public static class BreakRetestRejection
{
    // OURS: a break older than this is just a level with history, not a fresh retest.
    public const int MaxBarsSinceBreak = 20;

    // OURS: the zone counts as touched within this much ATR of the level. A line is a fiction
    // at any real spread; the video shows an area being interacted with.
    public const decimal ZoneAtr = 0.25m;

    // OURS: the breakout body must be at least this many ATR to count as displacement rather
    // than drift.
    public const decimal MinDisplacementAtr = 0.6m;

    // OURS: the return leg qualifies as weak when its average body is at most this share of
    // the breakout body.
    public const decimal MaxPullbackDecay = 0.75m;

    // OURS: the rejection bar must spend at least this share of its range as wick on the level
    // side. This is the "not a bare wick" rule made numeric — a doji that grazes the level and
    // closes mid-range does not qualify.
    public const decimal MinRejectionWick = 0.4m;

    public static BrrSignals None { get; } =
        new(false, null, 0m, 0, 0m, 0m, false, 0m, "no break-retest-rejection in range");

    public static BrrSignals Detect(IReadOnlyList<Candle> candles, decimal atr)
    {
        if (candles.Count < 12 || atr <= 0) return None;

        var last = candles[^1];
        var swings = SmartMoneyConcepts.DetectSwings(candles);
        if (swings.Count == 0) return None;

        BrrSignals? best = null;

        // Walk back over recent bars looking for the breakout that started the sequence.
        var oldest = Math.Max(1, candles.Count - 1 - MaxBarsSinceBreak);
        for (var i = candles.Count - 2; i >= oldest; i--)
        {
            var bar = candles[i];
            var body = Math.Abs(bar.Close - bar.Open);
            if (body < MinDisplacementAtr * atr) continue;   // not displacement

            var brokeUp = bar.Close > bar.Open;

            // The level is the swing this bar closed decisively through.
            var level = NearestBrokenSwing(swings, i, bar, brokeUp);
            if (level is not decimal levelPrice) continue;

            var barsSince = candles.Count - 1 - i;
            if (barsSince < 2) continue;   // needs a return leg to exist at all

            // The return leg: everything between the break and now.
            var leg = candles.Skip(i + 1).Take(barsSince - 1).ToList();
            if (leg.Count == 0) continue;
            var legBody = leg.Average(c => Math.Abs(c.Close - c.Open));
            var decay = body <= 0 ? 1m : legBody / body;
            if (decay > MaxPullbackDecay) continue;   // came back just as hard — not a fade

            // Price has to be back at the level now.
            var zone = ZoneAtr * atr;
            var touched = last.Low <= levelPrice + zone && last.High >= levelPrice - zone;
            if (!touched) continue;

            // Rejection: the bar closes back on the breakout's side, and spends real range as
            // wick through the level. A close back through the level is not disqualifying —
            // the video shows the wick piercing it — but the CLOSE must return.
            var closedRightSide = brokeUp ? last.Close > levelPrice : last.Close < levelPrice;
            var range = last.High - last.Low;
            var wick = range <= 0 ? 0m
                : brokeUp
                    ? (Math.Min(last.Open, last.Close) - last.Low) / range
                    : (last.High - Math.Max(last.Open, last.Close)) / range;
            var rejected = closedRightSide && wick >= MinRejectionWick;

            var side = brokeUp ? TradeSide.Long : TradeSide.Short;
            var candidate = new BrrSignals(
                true, side, levelPrice, barsSince, body / atr, decay, rejected, wick,
                $"{(brokeUp ? "broke above" : "broke below")} {levelPrice:F0} {barsSince} bars ago "
                + $"(displacement {body / atr:F2}xATR), pullback decayed to {decay:F2}x, "
                + (rejected
                    ? $"REJECTED with {wick:P0} wick"
                    : $"at the level but no rejection yet (wick {wick:P0}, close {(closedRightSide ? "right" : "wrong")} side)"));

            // Prefer a confirmed rejection; otherwise keep the freshest break.
            if (best is null
                || (candidate.RejectionConfirmed && !best.RejectionConfirmed)
                || (candidate.RejectionConfirmed == best.RejectionConfirmed && candidate.BarsSinceBreak < best.BarsSinceBreak))
                best = candidate;
        }

        return best ?? None;
    }

    // The swing level this bar closed through: it sat on one side before the bar and the bar
    // closed clear of it. Only swings formed BEFORE the break can have been broken by it.
    private static decimal? NearestBrokenSwing(
        IReadOnlyList<SmartMoneyConcepts.SwingPoint> swings, int barIndex, Candle bar, bool up)
    {
        decimal? best = null;
        foreach (var s in swings)
        {
            if (s.Index >= barIndex) continue;
            if (up != s.IsHigh) continue;                       // up-breaks clear swing highs
            if (up ? bar.Close <= s.Price : bar.Close >= s.Price) continue;
            if (up ? bar.Open > s.Price : bar.Open < s.Price) continue;  // already through it

            // Closest level to the bar's open — the one it actually cleared on this move.
            if (best is null || Math.Abs(s.Price - bar.Open) < Math.Abs(best.Value - bar.Open))
                best = s.Price;
        }
        return best;
    }
}
