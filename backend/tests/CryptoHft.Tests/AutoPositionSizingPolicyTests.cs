using CryptoHft.Application.Trading;
using Xunit;

namespace CryptoHft.Tests;

public sealed class AutoPositionSizingPolicyTests
{
    [Fact]
    public void RiskBasedMode_KeepsDecisionSizing()
    {
        var verdict = AutoPositionSizingPolicy.Resolve(
            AutoPositionSizingPolicy.RiskBasedMode,
            decisionQuantity: 0.00021m,
            decisionLeverage: 3,
            entryPrice: 62_897m,
            targetMarginUsdt: 7m,
            targetLeverage: 20);

        Assert.Equal(0.00021m, verdict.Quantity);
        Assert.Equal(3, verdict.Leverage);
    }

    [Fact]
    public void TargetMarginLeverageMode_UsesTargetNotional()
    {
        var verdict = AutoPositionSizingPolicy.Resolve(
            AutoPositionSizingPolicy.TargetMarginLeverageMode,
            decisionQuantity: 0.00021m,
            decisionLeverage: 3,
            entryPrice: 62_897m,
            targetMarginUsdt: 7m,
            targetLeverage: 20);

        Assert.Equal(0.002226m, verdict.Quantity);
        Assert.Equal(20, verdict.Leverage);
    }

    [Fact]
    public void TargetMarginLeverageMode_ClampsLeverageToTwenty()
    {
        var verdict = AutoPositionSizingPolicy.Resolve(
            AutoPositionSizingPolicy.TargetMarginLeverageMode,
            decisionQuantity: 0.001m,
            decisionLeverage: 3,
            entryPrice: 50_000m,
            targetMarginUsdt: 7m,
            targetLeverage: 125);

        Assert.Equal(20, verdict.Leverage);
        Assert.Equal(0.0028m, verdict.Quantity);
    }

    // Regression for the production bug found 2026-07-30: target 6 USDT x 20x at BTC
    // ~64,800 gives a raw qty of 0.00185, which the exchange validator FLOORS to 0.001
    // (realized margin ~3.2 — half the owner's target). Nearest-step snapping must
    // round it to 0.002 so the realized margin lands next to the configured target.
    [Fact]
    public void TargetMarginLeverageMode_SnapsQuantityToNearestStep()
    {
        var verdict = AutoPositionSizingPolicy.Resolve(
            AutoPositionSizingPolicy.TargetMarginLeverageMode,
            decisionQuantity: 0.007m,
            decisionLeverage: 2,
            entryPrice: 64_800m,
            targetMarginUsdt: 6m,
            targetLeverage: 20,
            quantityStep: 0.001m);

        Assert.Equal(0.002m, verdict.Quantity);
        Assert.Equal(20, verdict.Leverage);
    }

    [Fact]
    public void TargetMarginLeverageMode_SnapDownWhenNearestIsBelow()
    {
        // 7 x 20 = 140 notional at 100k -> 0.0014 -> nearest step is 0.001.
        var verdict = AutoPositionSizingPolicy.Resolve(
            AutoPositionSizingPolicy.TargetMarginLeverageMode,
            decisionQuantity: 0.007m,
            decisionLeverage: 2,
            entryPrice: 100_000m,
            targetMarginUsdt: 7m,
            targetLeverage: 20,
            quantityStep: 0.001m);

        Assert.Equal(0.001m, verdict.Quantity);
    }

    // The defensive Claude multiplier no longer shrinks target-margin sizing: production
    // evidence showed the near-constant 0.25x cap pushed every order below the exchange
    // minimum, silently replacing the owner's target with the venue floor.
    [Fact]
    public void TargetMarginLeverageMode_IgnoresClaudeMultiplier()
    {
        var verdict = AutoPositionSizingPolicy.Resolve(
            AutoPositionSizingPolicy.TargetMarginLeverageMode,
            decisionQuantity: 0.00021m,
            decisionLeverage: 2,
            entryPrice: 62_897m,
            targetMarginUsdt: 7m,
            targetLeverage: 20);

        Assert.Equal(0.002226m, verdict.Quantity);
        Assert.DoesNotContain("defensive cap", verdict.Reason);
    }
}
