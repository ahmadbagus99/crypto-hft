using CryptoHft.Application.DecisionEngine;
using Xunit;

namespace CryptoHft.Tests;

// The geometry/leverage learner must stay at defaults on thin data, move in the right
// direction with evidence, and never escape its clamps.
public sealed class ExecutionTuningPolicyTests
{
    [Fact]
    public void Stops_BelowMinSamples_ReturnDefaults()
    {
        var (sl, tp) = ExecutionTuningPolicy.ComputeStops(tpHits: 3, slHits: 5);
        Assert.Equal(ExecutionTuningPolicy.DefaultSlAtrMultiplier, sl);
        Assert.Equal(ExecutionTuningPolicy.DefaultTpAtrMultiplier, tp);
    }

    [Fact]
    public void Stops_LowTpRate_WidensStopAndPullsTarget()
    {
        // 2 TP vs 18 SL — stops die far too often: defensive geometry.
        var (sl, tp) = ExecutionTuningPolicy.ComputeStops(tpHits: 2, slHits: 18);
        Assert.True(sl > ExecutionTuningPolicy.DefaultSlAtrMultiplier);
        Assert.True(tp < ExecutionTuningPolicy.DefaultTpAtrMultiplier);
        Assert.InRange(sl, 2m, 2.6m);
        Assert.InRange(tp, 3.2m, 4m);
    }

    [Fact]
    public void Stops_HighTpRate_StretchesTarget()
    {
        // 10 TP vs 10 SL (rate 50%) — targets get hit with headroom: reach further.
        var (sl, tp) = ExecutionTuningPolicy.ComputeStops(tpHits: 10, slHits: 10);
        Assert.True(tp > ExecutionTuningPolicy.DefaultTpAtrMultiplier);
        Assert.True(sl <= ExecutionTuningPolicy.DefaultSlAtrMultiplier);
        Assert.InRange(tp, 4m, 5m);
        Assert.InRange(sl, 1.8m, 2m);
    }

    [Fact]
    public void Stops_ExtremeRates_StayClamped()
    {
        var (slLow, tpLow) = ExecutionTuningPolicy.ComputeStops(tpHits: 0, slHits: 100);
        Assert.Equal(2.6m, slLow);
        Assert.Equal(3.2m, tpLow);

        var (slHigh, tpHigh) = ExecutionTuningPolicy.ComputeStops(tpHits: 100, slHits: 0);
        Assert.Equal(1.8m, slHigh);
        Assert.Equal(5.0m, tpHigh);
    }

    [Fact]
    public void LeverageFactor_BelowMinSamples_IsDefault()
    {
        Assert.Equal(1m, ExecutionTuningPolicy.ComputeLeverageFactor(wins: 4, losses: 4));
    }

    [Fact]
    public void LeverageFactor_ScalesWithWinRate_AndClamps()
    {
        // ~50% winrate → ~1.0x baseline
        Assert.InRange(ExecutionTuningPolicy.ComputeLeverageFactor(wins: 10, losses: 10), 0.95m, 1.05m);
        // Poor winrate → clamps at 0.5x
        Assert.Equal(0.5m, ExecutionTuningPolicy.ComputeLeverageFactor(wins: 1, losses: 20));
        // Excellent winrate → clamps at 1.2x
        Assert.Equal(1.2m, ExecutionTuningPolicy.ComputeLeverageFactor(wins: 20, losses: 2));
    }

    // ---- Regime-pooled fallback -----------------------------------------------------------

    [Fact]
    public void ResolveStops_YoungRegime_BorrowsPooledEvidence()
    {
        // Own regime: 1 exit (thin). Pool: 2 TP vs 18 SL — defensive geometry from the pool.
        var (sl, tp) = ExecutionTuningPolicy.ResolveStops(
            ownTpHits: 1, ownSlHits: 0, pooledTpHits: 2, pooledSlHits: 18);
        Assert.True(sl > ExecutionTuningPolicy.DefaultSlAtrMultiplier);
        Assert.True(tp < ExecutionTuningPolicy.DefaultTpAtrMultiplier);
    }

    [Fact]
    public void ResolveStops_MatureRegime_UsesOwnCountersOverPool()
    {
        // Own regime mature at a 50% TP rate (stretch the target) while the pool is defensive.
        var (sl, tp) = ExecutionTuningPolicy.ResolveStops(
            ownTpHits: 10, ownSlHits: 10, pooledTpHits: 12, pooledSlHits: 100);
        Assert.True(tp > ExecutionTuningPolicy.DefaultTpAtrMultiplier);
        Assert.True(sl < ExecutionTuningPolicy.DefaultSlAtrMultiplier);
    }

    [Fact]
    public void ResolveStops_ThinEverywhere_StaysDefault()
    {
        var (sl, tp) = ExecutionTuningPolicy.ResolveStops(
            ownTpHits: 1, ownSlHits: 1, pooledTpHits: 3, pooledSlHits: 5);
        Assert.Equal(ExecutionTuningPolicy.DefaultSlAtrMultiplier, sl);
        Assert.Equal(ExecutionTuningPolicy.DefaultTpAtrMultiplier, tp);
    }

    [Fact]
    public void ResolveLeverageFactor_YoungRegime_UsesPool()
    {
        // Own: 1 trade. Pool: 2W/10L → (3/14)*2 = 0.43 → clamps at 0.5x.
        Assert.Equal(0.5m, ExecutionTuningPolicy.ResolveLeverageFactor(
            ownWins: 0, ownLosses: 1, pooledWins: 2, pooledLosses: 10));
    }

    [Fact]
    public void ResolveLeverageFactor_MatureRegime_UsesOwnCounters()
    {
        // Own: 8W/4L → (9/14)*2 = 1.29 → clamps at 1.2x, regardless of the losing pool.
        Assert.Equal(1.2m, ExecutionTuningPolicy.ResolveLeverageFactor(
            ownWins: 8, ownLosses: 4, pooledWins: 10, pooledLosses: 30));
    }
}
