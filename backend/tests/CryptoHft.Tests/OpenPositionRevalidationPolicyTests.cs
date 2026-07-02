using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;
using Xunit;

namespace CryptoHft.Tests;

public sealed class OpenPositionRevalidationPolicyTests
{
    [Fact]
    public void Evaluate_HoldsLong_WhenSellConfidenceBelowThreshold()
    {
        var decision = Decision(buy: 62m, sell: 38m);

        var verdict = OpenPositionRevalidationPolicy.Evaluate(TradeSide.Long, decision, 75m, 1);

        Assert.Equal(OpenPositionRevalidationAction.Hold, verdict.Action);
        Assert.Equal(0, verdict.WarningCount);
    }

    [Fact]
    public void Evaluate_WarnsLong_WhenSellConfidenceCrossesThresholdOnce()
    {
        var decision = Decision(buy: 23m, sell: 77m);

        var verdict = OpenPositionRevalidationPolicy.Evaluate(TradeSide.Long, decision, 75m, 0);

        Assert.Equal(OpenPositionRevalidationAction.Warning, verdict.Action);
        Assert.Equal(1, verdict.WarningCount);
    }

    [Fact]
    public void Evaluate_ClosesLong_WhenSellConfidenceConfirmsTwice()
    {
        var decision = Decision(buy: 24m, sell: 76m);

        var verdict = OpenPositionRevalidationPolicy.Evaluate(TradeSide.Long, decision, 75m, 1);

        Assert.Equal(OpenPositionRevalidationAction.Close, verdict.Action);
        Assert.Equal(0, verdict.WarningCount);
    }

    [Fact]
    public void Evaluate_ClosesLongImmediately_WhenSellConfidenceIsExtreme()
    {
        var decision = Decision(buy: 12m, sell: 88m);

        var verdict = OpenPositionRevalidationPolicy.Evaluate(TradeSide.Long, decision, 75m, 0);

        Assert.Equal(OpenPositionRevalidationAction.Close, verdict.Action);
    }

    [Fact]
    public void Evaluate_ClosesShort_WhenBuyConfidenceConfirmsTwice()
    {
        var decision = Decision(buy: 81m, sell: 19m);

        var verdict = OpenPositionRevalidationPolicy.Evaluate(TradeSide.Short, decision, 80m, 1);

        Assert.Equal(OpenPositionRevalidationAction.Close, verdict.Action);
        Assert.Equal(81m, verdict.OppositeConfidence);
    }

    private static AdvancedDecision Decision(decimal buy, decimal sell)
    {
        return new AdvancedDecision(
            Symbol: "BTCUSDT",
            Action: buy >= sell ? DecisionAction.Buy : DecisionAction.Sell,
            Confidence: Math.Max(buy, sell),
            ConfidenceBuy: buy,
            ConfidenceSell: sell,
            ConfidenceHold: 0m,
            ProbabilityOfSuccess: 0m,
            Regime: MarketRegime.Trending,
            EntryPrice: 100m,
            StopLoss: 90m,
            TakeProfit: 120m,
            TrailingStopPercent: 1m,
            RiskReward: 2m,
            PositionSizeQuantity: 1m,
            Leverage: 1,
            ShouldTrade: true,
            NoTradeReason: "",
            Cautions: Array.Empty<string>(),
            Scores: new Dictionary<string, decimal>(),
            Weights: new Dictionary<string, decimal>(),
            Components: Array.Empty<ScoreComponent>(),
            Reasons: Array.Empty<string>(),
            Llm: new LlmValidation(true, Math.Max(buy, sell), "", Array.Empty<string>(), false),
            Time: DateTimeOffset.UtcNow);
    }
}
