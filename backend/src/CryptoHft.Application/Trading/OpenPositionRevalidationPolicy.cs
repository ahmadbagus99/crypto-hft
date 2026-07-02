using CryptoHft.Application.DecisionEngine;
using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.Trading;

public enum OpenPositionRevalidationAction
{
    Hold = 1,
    Warning = 2,
    Close = 3
}

public sealed record OpenPositionRevalidationVerdict(
    OpenPositionRevalidationAction Action,
    decimal OppositeConfidence,
    int WarningCount,
    string Reason);

public static class OpenPositionRevalidationPolicy
{
    public static OpenPositionRevalidationVerdict Evaluate(
        TradeSide openSide,
        AdvancedDecision decision,
        decimal confidenceThreshold,
        int previousWarningCount)
    {
        var threshold = Math.Clamp(confidenceThreshold, 1m, 100m);
        var immediateCloseThreshold = Math.Clamp(Math.Max(threshold + 10m, 85m), 1m, 100m);
        var oppositeConfidence = openSide == TradeSide.Long
            ? decision.ConfidenceSell
            : decision.ConfidenceBuy;
        var oppositeLabel = openSide == TradeSide.Long ? "SELL" : "BUY";

        if (oppositeConfidence >= immediateCloseThreshold)
        {
            return new OpenPositionRevalidationVerdict(
                OpenPositionRevalidationAction.Close,
                oppositeConfidence,
                0,
                $"{oppositeLabel} confidence {oppositeConfidence:F0} >= immediate close threshold {immediateCloseThreshold:F0}");
        }

        if (oppositeConfidence >= threshold)
        {
            var warnings = previousWarningCount + 1;
            if (warnings >= 2)
            {
                return new OpenPositionRevalidationVerdict(
                    OpenPositionRevalidationAction.Close,
                    oppositeConfidence,
                    0,
                    $"{oppositeLabel} confidence {oppositeConfidence:F0} confirmed twice >= threshold {threshold:F0}");
            }

            return new OpenPositionRevalidationVerdict(
                OpenPositionRevalidationAction.Warning,
                oppositeConfidence,
                warnings,
                $"{oppositeLabel} confidence {oppositeConfidence:F0} >= threshold {threshold:F0}; waiting for second confirmation");
        }

        return new OpenPositionRevalidationVerdict(
            OpenPositionRevalidationAction.Hold,
            oppositeConfidence,
            0,
            $"{oppositeLabel} confidence {oppositeConfidence:F0} below threshold {threshold:F0}");
    }
}
