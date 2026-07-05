namespace CryptoHft.Application.DecisionEngine;

// Composite volume profile approximated from OHLCV (each candle's volume spread evenly
// across the bins its range covers) over the recent window (~10 days of 1h candles).
//
// SCOPE (deliberate): the profile is NOT a directional signal and never touches the
// score/confidence path. It refines the TP/SL geometry — a target just beyond a
// high-volume wall gets pulled in front of it, a stop parked in a thin LVN gets tucked
// behind the nearest HVN shelf — and provides level context for the dashboard and the
// LLM payload. Whether the snaps earn their keep is judged by the realized TP-hit-rate
// already tracked in ExecutionStats; no new learning parameters are introduced.
public sealed record VolumeProfileLevels(
    decimal Poc,                        // point of control: price with the most traded volume
    decimal Vah,                        // value area high (70% volume around POC)
    decimal Val,                        // value area low
    IReadOnlyList<decimal> HvnPrices,   // high-volume nodes (>= 1.5x mean bin volume)
    IReadOnlyList<decimal> LvnPrices,   // low-volume nodes  (<= 0.5x mean bin volume)
    decimal BinWidth,
    string Summary);

public static class VolumeProfile
{
    private const int DefaultBins = 50;
    private const int WindowCandles = 250;          // ~10 days on the 1h feed
    private const decimal ValueAreaFraction = 0.70m;
    private const decimal HvnFactor = 1.5m;
    private const decimal LvnFactor = 0.5m;
    private const decimal SnapBufferAtr = 0.25m;    // land just in front of / behind a node
    private const decimal EntryGraceAtr = 0.5m;     // walls hugging the entry are not obstacles
    private const decimal MinRewardRetention = 0.6m; // snap only if >= 60% of the reward survives
    private const decimal MaxSlWidening = 1.3m;      // dollar risk stays constant; qty shrinks

    public static VolumeProfileLevels? Build(IReadOnlyList<Candle> candles, int bins = DefaultBins)
    {
        if (candles.Count < 30 || bins < 10) return null;
        var window = candles.Skip(Math.Max(0, candles.Count - WindowCandles)).ToList();
        var high = window.Max(c => c.High);
        var low = window.Min(c => c.Low);
        if (high <= low) return null;

        var binWidth = (high - low) / bins;
        var volumes = new decimal[bins];
        foreach (var candle in window)
        {
            var startBin = Math.Clamp((int)((candle.Low - low) / binWidth), 0, bins - 1);
            var endBin = Math.Clamp((int)((candle.High - low) / binWidth), 0, bins - 1);
            var perBin = candle.Volume / (endBin - startBin + 1);
            for (var b = startBin; b <= endBin; b++) volumes[b] += perBin;
        }

        var total = volumes.Sum();
        if (total <= 0) return null;

        var pocBin = Array.IndexOf(volumes, volumes.Max());

        // Value area: expand greedily from the POC toward the heavier neighbor until 70%
        // of the traded volume is covered.
        var target = total * ValueAreaFraction;
        var covered = volumes[pocBin];
        int lowBin = pocBin, highBin = pocBin;
        while (covered < target && (lowBin > 0 || highBin < bins - 1))
        {
            var below = lowBin > 0 ? volumes[lowBin - 1] : -1m;
            var above = highBin < bins - 1 ? volumes[highBin + 1] : -1m;
            if (above >= below) { highBin++; covered += volumes[highBin]; }
            else { lowBin--; covered += volumes[lowBin]; }
        }

        decimal Center(int bin) => low + binWidth * (bin + 0.5m);

        var mean = total / bins;
        var hvn = new List<decimal>();
        var lvn = new List<decimal>();
        for (var b = 0; b < bins; b++)
        {
            if (volumes[b] >= mean * HvnFactor) hvn.Add(Center(b));
            else if (volumes[b] <= mean * LvnFactor) lvn.Add(Center(b));
        }

        var poc = Center(pocBin);
        var vah = Center(highBin);
        var val = Center(lowBin);
        return new VolumeProfileLevels(
            poc, vah, val, hvn, lvn, binWidth,
            $"POC {poc:F0}, VA {val:F0}–{vah:F0}, {hvn.Count} HVN / {lvn.Count} LVN nodes");
    }

    // A take-profit beyond a high-volume wall is optimistic: price tends to stall where
    // heavy inventory sits. Pull the target just in front of the FIRST wall on the path —
    // but only when most of the reward survives; a wall right out of the gate is reported
    // as a caution instead of degrading the trade into a scalp.
    internal static (decimal? SnappedTp, string? Note) SnapTakeProfit(
        VolumeProfileLevels profile, bool isBuy, decimal entry, decimal takeProfit, decimal atr)
    {
        var walls = profile.HvnPrices
            .Where(h => isBuy
                ? h > entry + atr * EntryGraceAtr && h < takeProfit
                : h < entry - atr * EntryGraceAtr && h > takeProfit)
            .ToList();
        if (walls.Count == 0) return (null, null);

        var wall = isBuy ? walls.Min() : walls.Max(); // first wall in the TP's way
        var snapped = isBuy ? wall - atr * SnapBufferAtr : wall + atr * SnapBufferAtr;
        var originalReward = Math.Abs(takeProfit - entry);
        var newReward = isBuy ? snapped - entry : entry - snapped;

        if (newReward <= 0 || newReward < originalReward * MinRewardRetention)
            return (null, $"TP path crosses a major HVN wall at {wall:F0} early — target likely optimistic");
        if (newReward >= originalReward) return (null, null); // buffer lands past the original TP

        return (snapped, $"TP snapped to {snapped:F0}, in front of the HVN wall at {wall:F0}");
    }

    // A stop resting in a thin LVN is where sweeps go to feed: barely any volume defends
    // it. Tuck the stop behind the nearest HVN shelf instead, capped at 1.3x the original
    // stop distance — the engine sizes qty as riskBudget/stopDistance, so the dollar risk
    // is unchanged and only the position shrinks.
    internal static (decimal? AdjustedSl, string? Note) AdjustStopLoss(
        VolumeProfileLevels profile, bool isBuy, decimal entry, decimal stopLoss, decimal atr)
    {
        var inThinZone = profile.LvnPrices.Any(l => Math.Abs(l - stopLoss) <= profile.BinWidth / 2m);
        if (!inThinZone) return (null, null);

        var originalRisk = Math.Abs(entry - stopLoss);
        var sweepNote = $"SL rests in a thin LVN near {stopLoss:F0} (sweep risk)";

        var shelters = profile.HvnPrices.Where(h => isBuy ? h < stopLoss : h > stopLoss).ToList();
        if (shelters.Count == 0) return (null, sweepNote);

        var shelter = isBuy ? shelters.Max() : shelters.Min(); // nearest shelf beyond the stop
        var adjusted = isBuy ? shelter - atr * SnapBufferAtr : shelter + atr * SnapBufferAtr;
        var newRisk = Math.Abs(entry - adjusted);

        if (newRisk <= originalRisk || newRisk > originalRisk * MaxSlWidening)
            return (null, sweepNote);

        return (adjusted, $"SL tucked behind the HVN shelf at {shelter:F0} (was in a thin LVN)");
    }

    // Entry landing on the value-area edge that supports the trade is worth surfacing as
    // confluence context (dashboard + LLM payload). Informational only — no score change.
    internal static string? ConfluenceNote(
        VolumeProfileLevels profile, bool isBuy, decimal entry, decimal atr)
    {
        if (isBuy && Math.Abs(entry - profile.Val) <= atr)
            return $"entry near VAL {profile.Val:F0} (value-area support)";
        if (!isBuy && Math.Abs(entry - profile.Vah) <= atr)
            return $"entry near VAH {profile.Vah:F0} (value-area resistance)";
        return null;
    }
}
