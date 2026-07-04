namespace CryptoHft.Application.DecisionEngine;

// Deterministic mapping from realized exit outcomes to execution baselines. The engine
// sizes quantity as riskBudget / stopDistance, so a wider learned SL automatically shrinks
// the position — dollar risk per trade stays constant while the geometry adapts.
//
// Stops: the observed TP-hit rate (vs SL hits) is compared against the geometry's own
// break-even rate. A low hit rate moves toward defensive geometry (wider stop, closer
// target → more trades resolve in our favor); a high hit rate stretches the target to
// capture more per win. Recomputed from full counters each time — no incremental drift.
//
// Leverage: realized win rate scales the confidence-tier baseline. 50% win rate keeps the
// baseline, sub-33% halves it, sustained 60%+ allows a modest 1.2x. Everything is clamped
// and nothing moves before a minimum sample count, so early noise cannot distort execution.
public static class ExecutionTuningPolicy
{
    public const decimal DefaultSlAtrMultiplier = 2m;
    public const decimal DefaultTpAtrMultiplier = 4m;
    public const decimal DefaultLeverageFactor = 1m;

    public const int MinExitSamples = 10;  // TP+SL hits before geometry moves
    public const int MinTradeSamples = 10; // wins+losses before leverage factor moves

    // Geometry anchors keyed by posterior TP-hit rate. Default 2/4 has break-even 33%.
    private const decimal LowRate = 0.20m;              // defensive anchor
    private static readonly decimal MidRate = 1m / 3m;  // default geometry break-even
    private const decimal HighRate = 0.45m;             // aggressive anchor

    public static (decimal SlAtrMultiplier, decimal TpAtrMultiplier) ComputeStops(int tpHits, int slHits)
    {
        var total = tpHits + slHits;
        if (total < MinExitSamples)
            return (DefaultSlAtrMultiplier, DefaultTpAtrMultiplier);

        // Beta(1,1) posterior mean keeps small samples near the default geometry.
        var tpRate = (tpHits + 1m) / (total + 2m);

        decimal sl, tp;
        if (tpRate <= MidRate)
        {
            // Stops die too often: widen SL, pull TP closer (defensive anchor 2.6 / 3.2).
            var t = (Math.Clamp(tpRate, LowRate, MidRate) - LowRate) / (MidRate - LowRate);
            sl = Lerp(2.6m, DefaultSlAtrMultiplier, t);
            tp = Lerp(3.2m, DefaultTpAtrMultiplier, t);
        }
        else
        {
            // Targets get hit with headroom: stretch TP for more reward (aggressive 1.8 / 5.0).
            var t = (Math.Clamp(tpRate, MidRate, HighRate) - MidRate) / (HighRate - MidRate);
            sl = Lerp(DefaultSlAtrMultiplier, 1.8m, t);
            tp = Lerp(DefaultTpAtrMultiplier, 5.0m, t);
        }
        return (Math.Round(sl, 2), Math.Round(tp, 2));
    }

    public static decimal ComputeLeverageFactor(int wins, int losses)
    {
        var total = wins + losses;
        if (total < MinTradeSamples)
            return DefaultLeverageFactor;

        var winRate = (wins + 1m) / (total + 2m);
        // 50% win rate → 1.0x baseline; scales linearly, clamped to [0.5x, 1.2x].
        return Math.Clamp(Math.Round(winRate * 2m, 2), 0.5m, 1.2m);
    }

    private static decimal Lerp(decimal from, decimal to, decimal t) => from + (to - from) * t;
}
