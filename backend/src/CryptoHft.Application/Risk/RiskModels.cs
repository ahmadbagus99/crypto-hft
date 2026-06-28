using CryptoHft.Application.DecisionEngine;

namespace CryptoHft.Application.Risk;

public sealed record RiskState(
    decimal Equity,
    decimal AvailableBalance,
    decimal DailyLoss,
    int ConsecutiveLosses,
    int OpenPositions,
    decimal CurrentExposure,
    decimal Atr,
    decimal LastPrice);

public sealed record RiskDecision(bool Allowed, string Reason, decimal Quantity, int Leverage);

public interface IRiskManager
{
    RiskDecision Evaluate(RiskState state, DecisionResult decision, RiskProfile profile);
}

public sealed record RiskProfile(
    decimal MaxDailyLoss,
    int MaxConsecutiveLosses,
    int MaxOpenPositions,
    decimal MaxExposure,
    decimal RiskPerTrade,
    decimal MinimumRiskReward,
    decimal AutoTradeConfidenceThreshold);
