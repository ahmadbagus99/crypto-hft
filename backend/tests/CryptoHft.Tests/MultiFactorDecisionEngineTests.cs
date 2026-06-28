using CryptoHft.Application.DecisionEngine;
using CryptoHft.Domain.Enums;
using Xunit;

namespace CryptoHft.Tests;

public sealed class MultiFactorDecisionEngineTests
{
    [Fact]
    public void Evaluate_ReturnsBuy_WhenTrendAndMomentumAreBullish()
    {
        var engine = new MultiFactorDecisionEngine();
        var result = engine.Evaluate(new DecisionInput(
            Symbol: "BTCUSDT",
            LastPrice: 60000,
            Ema9: 61000,
            Ema20: 60500,
            Ema50: 59000,
            Ema200: 56000,
            Rsi: 62,
            Macd: 120,
            MacdSignal: 80,
            Atr: 500,
            Vwap: 59500,
            OrderBookImbalance: 0.2m,
            FundingRate: 0.0001m,
            OpenInterestChange: 1,
            NewsScore: 70,
            VolatilityScore: 50));

        Assert.True(result.Confidence >= 70);
        Assert.Contains(result.Action, new[] { DecisionAction.Buy, DecisionAction.StrongBuy });
        Assert.True(result.ExpectedRiskReward >= 2);
    }
}
