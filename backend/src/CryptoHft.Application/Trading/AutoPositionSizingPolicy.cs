namespace CryptoHft.Application.Trading;

public sealed record AutoPositionSizingVerdict(decimal Quantity, int Leverage, string Reason);

public static class AutoPositionSizingPolicy
{
    public const int RiskBasedMode = 0;
    public const int TargetMarginLeverageMode = 1;
    public const int MaxTargetLeverage = 20;

    public static AutoPositionSizingVerdict Resolve(
        int mode,
        decimal decisionQuantity,
        int decisionLeverage,
        decimal entryPrice,
        decimal targetMarginUsdt,
        int targetLeverage,
        decimal? defensiveSizeMultiplier = null)
    {
        if (mode != TargetMarginLeverageMode)
        {
            return new AutoPositionSizingVerdict(
                decisionQuantity,
                Math.Clamp(decisionLeverage <= 0 ? 1 : decisionLeverage, 1, MaxTargetLeverage),
                "risk-based decision sizing");
        }

        var leverage = Math.Clamp(targetLeverage <= 0 ? MaxTargetLeverage : targetLeverage, 1, MaxTargetLeverage);
        if (entryPrice <= 0 || targetMarginUsdt <= 0)
        {
            return new AutoPositionSizingVerdict(
                decisionQuantity,
                leverage,
                "target margin sizing unavailable — fallback to decision quantity");
        }

        var targetNotional = targetMarginUsdt * leverage;
        var cap = defensiveSizeMultiplier is null
            ? 1m
            : Math.Min(1m, Math.Clamp(defensiveSizeMultiplier.Value, 0.1m, 1.5m));
        var quantity = Math.Round(targetNotional / entryPrice * cap, 6);
        var defensiveNote = cap < 1m ? $"; Claude defensive cap {cap:P0}" : "";
        return new AutoPositionSizingVerdict(
            quantity,
            leverage,
            $"target margin sizing: {targetMarginUsdt:F2} USDT x {leverage}x = {targetNotional:F2} USDT notional{defensiveNote}");
    }
}
