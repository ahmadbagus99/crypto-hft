using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.DecisionEngine;

public sealed record ScalperGateVerdict(
    bool Allowed,
    TradeSide? Side,
    string Stage,     // which gate decided: "direction" | "location" | "timing" | "pass"
    string Reason);

// Scalper only. Intraday never calls this and is unaffected.
//
// The weighted blend the engine uses everywhere else collapses three different questions into
// one number: which way, where, and when. Averaging them is what produced the failure measured
// on 4-7 August — 20 entries, all long, 11 stopped out, and in 11 of 11 cases price returned
// above entry within four hours. The direction was right every time; the entry was early every
// time, and a single score has no way to say "right way, wrong moment".
//
// So the scalper asks them in order, the way the desk actually does it:
//
//   DIRECTION (4h + 1h)  locks the side, or stands aside when they disagree
//   LOCATION  (15m)      is price somewhere worth trading from
//   TIMING    (1m/5m)    has a bar actually turned, or is price still falling into the level
//
// A gate that fails does not subtract points — it stops the sequence. "Uptrend, but the
// pullback is still going" resolves to WAIT, which the averaged score could only express as
// a muddled 46.8 that reads identically to "no idea".
public static class ScalperSequentialGate
{
    // A timeframe vote is directional past these; between them it abstains.
    public const decimal BullishVote = 55m;
    public const decimal BearishVote = 45m;

    // Where in the dealing range an entry is acceptable. Buying the top of the range is how a
    // pullback trade becomes a breakout chase.
    public const decimal DiscountCeiling = 0.55m;
    public const decimal PremiumFloor = 0.45m;

    // GATE 1 — the higher timeframes agree on a side, or there is no trade. Deliberately a
    // veto rather than a weight: 4h at 89 should not be able to drag a falling 5m into a buy,
    // which is exactly what a 60% weighting would have done.
    public static ScalperGateVerdict Direction(decimal vote4h, decimal vote1h)
    {
        var bull = vote4h >= BullishVote && vote1h >= BullishVote;
        var bear = vote4h <= BearishVote && vote1h <= BearishVote;

        if (bull) return new(true, TradeSide.Long, "direction", $"4h {vote4h:F0} and 1h {vote1h:F0} both bullish");
        if (bear) return new(true, TradeSide.Short, "direction", $"4h {vote4h:F0} and 1h {vote1h:F0} both bearish");

        return new(false, null, "direction",
            $"higher timeframes disagree (4h {vote4h:F0}, 1h {vote1h:F0}) — no side to take");
    }

    // GATE 2 — price must be somewhere a trade makes sense from: the discount half of the
    // range for a long, the premium half for a short, or at a named level (unfilled gap,
    // fibonacci zone, support/resistance band) regardless of range position.
    public static ScalperGateVerdict Location(
        TradeSide side, decimal rangePosition, bool atNamedLevel)
    {
        var long_ = side == TradeSide.Long;
        var positioned = long_ ? rangePosition <= DiscountCeiling : rangePosition >= PremiumFloor;

        if (positioned || atNamedLevel)
        {
            var why = positioned
                ? $"range position {rangePosition:F2} is in {(long_ ? "discount" : "premium")}"
                : "price is at a named level";
            return new(true, side, "location", why);
        }

        return new(false, side, "location",
            $"range position {rangePosition:F2} is the wrong half for a {(long_ ? "long" : "short")} and no level is in reach");
    }

    // GATE 3 — a bar has to have turned. This is the gate the averaged score could not
    // express, and the one every stopped-out entry on 4-7 August would have failed.
    //
    // A qualifying reversal bar closes in the trade's direction, closes in the stronger half
    // of its own range (a doji that merely ticks the right way is not a turn), and follows
    // either a bar that went the other way or a sweep of the previous bar's extreme — so it
    // marks a change, not the continuation of a move already under way.
    public static ScalperGateVerdict Timing(TradeSide side, IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 2)
            return new(false, side, "timing", "not enough closed bars to judge a turn");

        var bar = candles[^1];
        var prior = candles[^2];
        var long_ = side == TradeSide.Long;

        var closedWithSide = long_ ? bar.Close > bar.Open : bar.Close < bar.Open;
        if (!closedWithSide)
            return new(false, side, "timing",
                $"last bar closed {(bar.Close < bar.Open ? "down" : "up")}, against a {(long_ ? "long" : "short")}");

        var midpoint = (bar.High + bar.Low) / 2m;
        var strongClose = long_ ? bar.Close > midpoint : bar.Close < midpoint;
        if (!strongClose)
            return new(false, side, "timing", "bar closed inside its own range — no conviction in the turn");

        var priorWentOtherWay = long_ ? prior.Close < prior.Open : prior.Close > prior.Open;
        var sweptPriorExtreme = long_ ? bar.Low <= prior.Low : bar.High >= prior.High;
        if (!priorWentOtherWay && !sweptPriorExtreme)
            return new(false, side, "timing", "bar continues a move already under way — not a turn");

        return new(true, side, "timing",
            $"reversal bar closed {bar.Close:F1} ({(sweptPriorExtreme ? "swept the prior extreme" : "turned after an opposing bar")})");
    }

    // Runs the three in order and reports the first failure, so the log says which question
    // stopped the trade rather than only that nothing happened.
    public static ScalperGateVerdict Evaluate(
        decimal vote4h,
        decimal vote1h,
        decimal rangePosition,
        bool atNamedLevel,
        IReadOnlyList<Candle> entryCandles,
        BrrSignals? brr = null)
    {
        var direction = Direction(vote4h, vote1h);
        if (!direction.Allowed || direction.Side is not TradeSide side) return direction;

        // A live break-retest-rejection running the same way answers location and timing at
        // once, and answers them better: "price is back at the level it broke eight bars ago
        // and has just rejected it" is a sharper statement than "price is in the discount half"
        // followed by "a bar closed our way". The generic gates stay as the path for setups
        // that are not a retest — most of them.
        if (brr is { Detected: true, RejectionConfirmed: true } confirmed && confirmed.Side == side)
        {
            return new(true, side, "pass", $"{direction.Reason}; BRR: {confirmed.Summary}");
        }

        // A break that has come back to its level but has NOT rejected yet is the one shape
        // the source material is explicit about refusing: the touch is not the trigger. Say so
        // plainly instead of letting the generic timing gate give a vaguer reason.
        if (brr is { Detected: true, RejectionConfirmed: false } pending && pending.Side == side)
        {
            return new(false, side, "timing", $"BRR retest without rejection — {pending.Summary}");
        }

        var location = Location(side, rangePosition, atNamedLevel);
        if (!location.Allowed) return location;

        var timing = Timing(side, entryCandles);
        if (!timing.Allowed) return timing;

        return new(true, side, "pass",
            $"{direction.Reason}; {location.Reason}; {timing.Reason}");
    }
}
