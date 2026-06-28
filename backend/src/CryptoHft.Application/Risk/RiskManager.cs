using CryptoHft.Application.DecisionEngine;

namespace CryptoHft.Application.Risk;

public sealed class RiskManager : IRiskManager
{
    public RiskDecision Evaluate(RiskState state, DecisionResult decision, RiskProfile profile)
    {
        if (state.DailyLoss >= state.Equity * profile.MaxDailyLoss)
            return new RiskDecision(false, "Maximum daily loss reached", 0, 0);

        if (state.ConsecutiveLosses >= profile.MaxConsecutiveLosses)
            return new RiskDecision(false, "Maximum consecutive loss reached", 0, 0);

        if (state.OpenPositions >= profile.MaxOpenPositions)
            return new RiskDecision(false, "Maximum open position limit reached", 0, 0);

        if (state.CurrentExposure >= state.Equity * profile.MaxExposure)
            return new RiskDecision(false, "Maximum exposure reached", 0, 0);

        if (decision.Confidence < profile.AutoTradeConfidenceThreshold)
            return new RiskDecision(false, "Confidence is below auto-trading threshold", 0, 0);

        if (decision.ExpectedRiskReward < profile.MinimumRiskReward)
            return new RiskDecision(false, "Risk reward is below required minimum", 0, 0);

        var stopDistance = Math.Max(Math.Abs(decision.SuggestedStopLoss - state.LastPrice), state.Atr);
        var riskBudget = state.Equity * profile.RiskPerTrade;
        var quantity = stopDistance <= 0 ? 0 : riskBudget / stopDistance;
        var leverage = decision.Confidence >= 85 ? 5 : 3;

        return new RiskDecision(true, "Risk checks passed", Decimal.Round(quantity, 4), leverage);
    }
}
