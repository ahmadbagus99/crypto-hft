using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;
using Xunit;

namespace CryptoHft.Tests;

// Exit classification feeds the SL/TP geometry learner — ambiguous cases must stay Unknown.
public sealed class PositionCloseClassifierTests
{
    [Fact]
    public void LongPosition_MarkAtTakeProfit_IsTakeProfit()
    {
        var reason = PositionCloseClassifier.Classify(
            TradeSide.Long, lastMarkPrice: 103_900m, stopLoss: 98_000m, takeProfit: 104_000m,
            closeOrderReason: null);
        Assert.Equal(PositionCloseReason.TakeProfit, reason);
    }

    [Fact]
    public void LongPosition_MarkAtStopLoss_IsStopLoss()
    {
        var reason = PositionCloseClassifier.Classify(
            TradeSide.Long, lastMarkPrice: 98_100m, stopLoss: 98_000m, takeProfit: 104_000m,
            closeOrderReason: null);
        Assert.Equal(PositionCloseReason.StopLoss, reason);
    }

    [Fact]
    public void ShortPosition_MarkBelowTakeProfit_IsTakeProfit()
    {
        // Short: TP sits below entry, SL above.
        var reason = PositionCloseClassifier.Classify(
            TradeSide.Short, lastMarkPrice: 96_100m, stopLoss: 102_000m, takeProfit: 96_000m,
            closeOrderReason: null);
        Assert.Equal(PositionCloseReason.TakeProfit, reason);
    }

    [Fact]
    public void ShortPosition_MarkAtStopLoss_IsStopLoss()
    {
        var reason = PositionCloseClassifier.Classify(
            TradeSide.Short, lastMarkPrice: 101_950m, stopLoss: 102_000m, takeProfit: 96_000m,
            closeOrderReason: null);
        Assert.Equal(PositionCloseReason.StopLoss, reason);
    }

    [Fact]
    public void CloseOrder_WithAutoCloseReason_IsAutoClose()
    {
        var reason = PositionCloseClassifier.Classify(
            TradeSide.Long, lastMarkPrice: 100_500m, stopLoss: 98_000m, takeProfit: 104_000m,
            closeOrderReason: "Auto close: rule-based revalidation invalidated Long position");
        Assert.Equal(PositionCloseReason.AutoClose, reason);
    }

    [Fact]
    public void CloseOrder_WithOtherReason_IsManualClose()
    {
        var reason = PositionCloseClassifier.Classify(
            TradeSide.Long, lastMarkPrice: 100_500m, stopLoss: 98_000m, takeProfit: 104_000m,
            closeOrderReason: "Closed from dashboard");
        Assert.Equal(PositionCloseReason.ManualClose, reason);
    }

    [Fact]
    public void MarkBetweenLevels_IsUnknown()
    {
        // Mid-range close with no app order: cannot attribute — must not teach the learner.
        var reason = PositionCloseClassifier.Classify(
            TradeSide.Long, lastMarkPrice: 100_800m, stopLoss: 98_000m, takeProfit: 104_000m,
            closeOrderReason: null);
        Assert.Equal(PositionCloseReason.Unknown, reason);
    }

    [Fact]
    public void MissingLevels_IsUnknown()
    {
        var reason = PositionCloseClassifier.Classify(
            TradeSide.Long, lastMarkPrice: 100_800m, stopLoss: null, takeProfit: null,
            closeOrderReason: null);
        Assert.Equal(PositionCloseReason.Unknown, reason);
    }
}
