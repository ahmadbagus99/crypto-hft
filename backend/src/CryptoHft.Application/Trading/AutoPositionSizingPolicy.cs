namespace CryptoHft.Application.Trading;

public sealed record AutoPositionSizingVerdict(decimal Quantity, int Leverage, string Reason);

public static class AutoPositionSizingPolicy
{
    public const int RiskBasedMode = 0;
    public const int TargetMarginLeverageMode = 1;
    public const int MaxTargetLeverage = 20;

    // Target-margin mode intentionally ignores Claude's defensive size multiplier.
    // Production evidence (2026-07-30): target 6 USDT x 20x = qty 0.00185, Claude's
    // near-constant 0.20-0.25x cap cut it to ~0.0004, and the exchange validator then
    // raised it back to the 0.001 BTC minimum — so the owner's explicit margin target
    // was silently replaced by the venue floor (~3 USDT) on every trade. In this mode
    // the owner has fixed the notional; Claude still cannot veto, and the account-level
    // exposure cap remains the safety net.
    public static AutoPositionSizingVerdict Resolve(
        int mode,
        decimal decisionQuantity,
        int decisionLeverage,
        decimal entryPrice,
        decimal targetMarginUsdt,
        int targetLeverage,
        decimal quantityStep = 0m)
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
        var quantity = Math.Round(targetNotional / entryPrice, 6);

        // Snap to the venue's quantity step by NEAREST, not floor. The downstream
        // exchange validator floors, which turned 0.00185 (margin ~6) into 0.001
        // (margin ~3.2) — half a step below the owner's target. Nearest keeps the
        // realized margin as close to the configured target as the venue allows.
        if (quantityStep > 0)
        {
            var snapped = Math.Round(quantity / quantityStep, 0, MidpointRounding.AwayFromZero) * quantityStep;
            if (snapped > 0) quantity = snapped;
        }

        return new AutoPositionSizingVerdict(
            quantity,
            leverage,
            $"target margin sizing: {targetMarginUsdt:F2} USDT x {leverage}x = {targetNotional:F2} USDT notional");
    }
}
