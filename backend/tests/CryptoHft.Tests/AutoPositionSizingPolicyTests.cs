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
}
