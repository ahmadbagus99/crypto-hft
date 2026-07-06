using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.Trading;

// Classifies why a position closed. App-initiated exits are identified by the reduce-only
// close order the executor persisted; exchange-side exits (SL/TP algo fills) are inferred
// from the last observed mark price vs the protective levels. Conservative by design:
// anything ambiguous stays Unknown so it never teaches the geometry learner a wrong lesson.
public static class PositionCloseClassifier
{
    // The last mark price can be up to one worker tick (~30s) stale, so a level counts as
    // "hit" when the mark is at/beyond it, or within this fraction of the level.
    private const decimal ProximityBand = 0.005m; // 0.5%

    public static PositionCloseReason Classify(
        TradeSide side,
        decimal entryPrice,
        decimal lastMarkPrice,
        decimal? stopLoss,
        decimal? takeProfit,
        string? closeOrderReason)
    {
        // A reduce-only market order just before the close means the app closed it.
        if (!string.IsNullOrWhiteSpace(closeOrderReason))
            return closeOrderReason.Contains("Auto close", StringComparison.OrdinalIgnoreCase)
                ? PositionCloseReason.AutoClose
                : PositionCloseReason.ManualClose;

        if (lastMarkPrice <= 0) return PositionCloseReason.Unknown;

        if (takeProfit is decimal tp && tp > 0 && ReachedFavorable(side, lastMarkPrice, tp))
            return PositionCloseReason.TakeProfit;
        if (stopLoss is decimal sl && sl > 0 && ReachedAdverse(side, lastMarkPrice, sl))
        {
            // A stop resting at/beyond entry on the PROFIT side is a ratcheted trailing stop,
            // not the original invalidation level — kept distinct so the geometry learner's
            // SL-hit counter never counts a protected winner as a failed stop.
            var ratcheted = entryPrice > 0 && (side == TradeSide.Long ? sl >= entryPrice : sl <= entryPrice);
            return ratcheted ? PositionCloseReason.TrailingStop : PositionCloseReason.StopLoss;
        }
        return PositionCloseReason.Unknown;
    }

    // TP direction: above entry for longs, below for shorts.
    private static bool ReachedFavorable(TradeSide side, decimal mark, decimal level)
        => side == TradeSide.Long
            ? mark >= level * (1m - ProximityBand)
            : mark <= level * (1m + ProximityBand);

    // SL direction: below entry for longs, above for shorts.
    private static bool ReachedAdverse(TradeSide side, decimal mark, decimal level)
        => side == TradeSide.Long
            ? mark <= level * (1m + ProximityBand)
            : mark >= level * (1m - ProximityBand);
}
