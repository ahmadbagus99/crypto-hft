using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.Trading;

public sealed record TrailingStopVerdict(decimal? NewStopLoss, string Reason, decimal ProfitR = 0m);

// Ratcheting trailing stop, expressed in R (the entry->initial-SL distance, i.e. 2xATR at
// entry). Mirrors disciplined manual management: below +1R the stop is never touched (the
// noise buffer chosen at entry stays intact), at +1R it moves to breakeven+fees, beyond
// that it follows the mark at a full 1R so profit locks in without normal retraces
// knocking the position out. Near the take profit the stop stops moving — the TP order
// owns the exit from there. The stop only ever tightens, and only in MinStepR increments
// so the exchange is not spammed with amendments every tick.
public static class TrailingStopPolicy
{
    public const decimal ActivationR = 1.0m;      // profit needed before the stop first moves
    public const decimal TrailDistanceR = 1.0m;   // stop follows the mark at this distance (= 2xATR at entry)
    public const decimal MinStepR = 0.15m;        // smallest improvement worth an exchange amendment (~0.3xATR)
    public const decimal NearTpBandR = 0.25m;     // inside this of the TP, leave the exit to the TP order
    public const decimal BreakevenFeeFraction = 0.0012m; // covers round-trip taker fees + slip at breakeven

    public static TrailingStopVerdict Evaluate(
        TradeSide side,
        decimal entryPrice,
        decimal markPrice,
        decimal? initialStopLoss,
        decimal? currentStopLoss,
        decimal? takeProfit,
        decimal trailingDistanceR = TrailDistanceR)
    {
        if (entryPrice <= 0 || markPrice <= 0)
            return new TrailingStopVerdict(null, "price data unavailable");

        var risk = RiskUnit(side, entryPrice, initialStopLoss, takeProfit);
        if (risk <= 0)
            return new TrailingStopVerdict(null, "risk unit unresolvable (no initial SL or TP)");

        var distanceR = NormalizeTrailingDistance(trailingDistanceR);
        var isLong = side == TradeSide.Long;
        var profitR = (isLong ? markPrice - entryPrice : entryPrice - markPrice) / risk;
        if (profitR < ActivationR)
            return new TrailingStopVerdict(null, $"profit {profitR:F2}R below activation {ActivationR:F2}R");

        if (takeProfit is decimal tp && tp > 0)
        {
            var toTp = isLong ? tp - markPrice : markPrice - tp;
            if (toTp <= NearTpBandR * risk)
                return new TrailingStopVerdict(null, "near take profit — let the TP order finish the trade");
        }

        // Candidate: trail a full R behind the mark, but never below breakeven+fees.
        var breakeven = isLong
            ? entryPrice * (1m + BreakevenFeeFraction)
            : entryPrice * (1m - BreakevenFeeFraction);
        var trailed = isLong
            ? markPrice - distanceR * risk
            : markPrice + distanceR * risk;
        var candidate = Math.Round(isLong ? Math.Max(breakeven, trailed) : Math.Min(breakeven, trailed), 2);

        // Ratchet: only replace the stop for a meaningful tightening, never loosen it.
        if (currentStopLoss is decimal current && current > 0)
        {
            var improvement = isLong ? candidate - current : current - candidate;
            if (improvement < MinStepR * risk)
                return new TrailingStopVerdict(null, $"improvement below {MinStepR:F2}R step");
        }

        return new TrailingStopVerdict(
            candidate,
            $"profit {profitR:F2}R — stop ratcheted to {candidate} (trail {distanceR:F2}R, floor breakeven+fees)",
            Math.Round(profitR, 2));
    }

    // R = entry->initial-SL distance. When the initial stop is unknown or already sits on
    // the profit side (e.g. restart after a ratchet), fall back to |TP - entry| / 2: the
    // engine enforces a minimum 2:1 reward:risk, so half the TP distance is a conservative
    // (never smaller than actual) risk unit.
    private static decimal RiskUnit(TradeSide side, decimal entryPrice, decimal? initialStopLoss, decimal? takeProfit)
    {
        if (initialStopLoss is decimal sl && sl > 0)
        {
            var risk = side == TradeSide.Long ? entryPrice - sl : sl - entryPrice;
            if (risk > 0) return risk;
        }
        return takeProfit is decimal tp && tp > 0 ? Math.Abs(tp - entryPrice) / 2m : 0m;
    }

    private static decimal NormalizeTrailingDistance(decimal value)
    {
        return value switch
        {
            0.50m => 0.50m,
            0.75m => 0.75m,
            1.00m => 1.00m,
            1.25m => 1.25m,
            _ => TrailDistanceR
        };
    }
}
