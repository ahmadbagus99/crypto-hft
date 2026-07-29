namespace CryptoHft.Application.DecisionEngine;

public sealed record SrLevel(decimal Price, int Touches);

public sealed record SrSignals(
    decimal Score,              // 0-100 (bullish > 50)
    SrLevel? NearestSupport,    // strongest-clustered level below price
    SrLevel? NearestResistance, // strongest-clustered level above price
    string Summary);

// Horizontal support/resistance from clustered swing pivots. Pivots within a tolerance
// band merge into one level whose strength is its touch count — a level tested three
// times is a real wall, a single pivot is noise. The directional read is classical:
// price holding just above a tested support leans long (defended floor), price pressing
// into a tested resistance leans short (supply overhead), and a decisive close THROUGH
// a strong level flips it (breakout). The nearest levels are also exposed so the engine
// can pull a take-profit that sits beyond a wall to the near side of it.
public static class SupportResistanceLevels
{
    private const decimal ClusterToleranceAtr = 0.35m; // pivots this close merge into one level
    private const decimal NearAtr = 0.5m;              // "at the level" distance for bounce/rejection
    private const decimal BreakBufferAtr = 0.2m;       // close beyond the level by this = break
    private const int MinTouches = 2;

    public static SrSignals Analyze(IReadOnlyList<Candle> candles, decimal atr)
    {
        if (candles.Count < 30 || atr <= 0)
            return new SrSignals(50m, null, null, "insufficient data");

        var levels = BuildLevels(SmartMoneyConcepts.DetectSwings(candles), atr);
        var close = candles[^1].Close;
        var prevClose = candles[^2].Close;

        var support = levels.Where(l => l.Price < close).OrderByDescending(l => l.Price).FirstOrDefault();
        var resistance = levels.Where(l => l.Price > close).OrderBy(l => l.Price).FirstOrDefault();

        var score = 50m;
        var notes = new List<string>();

        if (support is not null)
        {
            var distance = (close - support.Price) / atr;
            if (distance <= NearAtr)
            {
                score += 8m;
                notes.Add($"holding support {support.Price:F0} ({support.Touches} touches)");
                // Rejection wick: the last candle pierced the level but closed back above it.
                if (candles[^1].Low < support.Price && close > support.Price)
                {
                    score += 4m;
                    notes.Add("rejection wick off support");
                }
            }
        }
        if (resistance is not null)
        {
            var distance = (resistance.Price - close) / atr;
            if (distance <= NearAtr)
            {
                score -= 8m;
                notes.Add($"pressing resistance {resistance.Price:F0} ({resistance.Touches} touches)");
                if (candles[^1].High > resistance.Price && close < resistance.Price)
                {
                    score -= 4m;
                    notes.Add("rejection wick off resistance");
                }
            }
        }

        // A decisive close through a tested level on THIS candle is a breakout vote.
        foreach (var level in levels)
        {
            if (prevClose <= level.Price && close > level.Price + atr * BreakBufferAtr)
            {
                score += 10m;
                notes.Add($"broke above {level.Price:F0} ({level.Touches} touches)");
                break;
            }
            if (prevClose >= level.Price && close < level.Price - atr * BreakBufferAtr)
            {
                score -= 10m;
                notes.Add($"broke below {level.Price:F0} ({level.Touches} touches)");
                break;
            }
        }

        score = Math.Clamp(score, 0m, 100m);
        var context =
            $"S {support?.Price:F0}{(support is null ? "-" : $" ({support.Touches}t)")} / " +
            $"R {resistance?.Price:F0}{(resistance is null ? "-" : $" ({resistance.Touches}t)")}";
        var summary = notes.Count == 0 ? context : $"{context}; {string.Join(", ", notes)}";

        return new SrSignals(score, support, resistance, summary);
    }

    // Merge swing pivots into horizontal levels: sort by price, group neighbors within
    // the tolerance band, keep clusters tested at least MinTouches times.
    internal static List<SrLevel> BuildLevels(
        IReadOnlyList<SmartMoneyConcepts.SwingPoint> swings, decimal atr)
    {
        var prices = swings.Select(s => s.Price).OrderBy(p => p).ToList();
        var levels = new List<SrLevel>();
        var cluster = new List<decimal>();

        foreach (var price in prices)
        {
            if (cluster.Count > 0 && price - cluster[^1] > atr * ClusterToleranceAtr)
            {
                if (cluster.Count >= MinTouches)
                    levels.Add(new SrLevel(Math.Round(cluster.Average(), 2), cluster.Count));
                cluster.Clear();
            }
            cluster.Add(price);
        }
        if (cluster.Count >= MinTouches)
            levels.Add(new SrLevel(Math.Round(cluster.Average(), 2), cluster.Count));

        return levels;
    }

    // Pull a take-profit that sits beyond the nearest tested wall to just in front of it,
    // mirroring the volume-profile snap: only when the remaining reward keeps at least
    // 60% of the original, otherwise leave the TP and return a caution note instead.
    public static (decimal? SnappedTp, string? Note) SnapTakeProfit(
        SrSignals signals, bool isLong, decimal entry, decimal takeProfit, decimal atr)
    {
        var wall = isLong ? signals.NearestResistance : signals.NearestSupport;
        if (wall is null || atr <= 0) return (null, null);

        var beyondWall = isLong ? takeProfit > wall.Price : takeProfit < wall.Price;
        if (!beyondWall) return (null, null);

        var candidate = isLong ? wall.Price - atr * 0.25m : wall.Price + atr * 0.25m;
        var originalReward = Math.Abs(takeProfit - entry);
        var newReward = Math.Abs(candidate - entry);
        if (originalReward <= 0 || newReward <= 0)
            return (null, null);

        if (newReward / originalReward >= 0.6m)
            return (Math.Round(candidate, 2),
                $"TP snapped to {candidate:F0} in front of {(isLong ? "resistance" : "support")} {wall.Price:F0} ({wall.Touches} touches)");

        return (null,
            $"strong {(isLong ? "resistance" : "support")} {wall.Price:F0} ({wall.Touches} touches) before TP — target may be optimistic");
    }
}
