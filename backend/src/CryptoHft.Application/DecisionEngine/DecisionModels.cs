using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.DecisionEngine;

public sealed record FactorScore(string Name, decimal Score, decimal Weight, string Reason);

public sealed record DecisionResult(
    string Symbol,
    DecisionAction Action,
    decimal Confidence,
    decimal ExpectedRiskReward,
    decimal SuggestedStopLoss,
    decimal SuggestedTakeProfit,
    decimal SuggestedQuantity,
    int SuggestedLeverage,
    IReadOnlyList<FactorScore> Factors,
    string Reason,
    DateTimeOffset Time);

public sealed record DecisionInput(
    string Symbol,
    decimal LastPrice,
    decimal Ema9,
    decimal Ema20,
    decimal Ema50,
    decimal Ema200,
    decimal Rsi,
    decimal Macd,
    decimal MacdSignal,
    decimal Atr,
    decimal Vwap,
    decimal OrderBookImbalance,
    decimal FundingRate,
    decimal OpenInterestChange,
    decimal NewsScore,
    decimal VolatilityScore);
