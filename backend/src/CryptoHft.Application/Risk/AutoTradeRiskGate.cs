namespace CryptoHft.Application.Risk;

// Account-level risk gate consulted right before an auto order is placed. It never judges
// the signal itself — confidence remains the only gate that decides WHETHER to trade. This
// gate protects the account: it pauses trading after the daily-loss or consecutive-loss
// limit is hit, and trims the order so its margin fits the exposure limit.
public sealed record AutoTradeRiskVerdict(
    bool Allowed,
    string Reason,
    decimal? AdjustedQuantity = null);

public interface IAutoTradeRiskGate
{
    Task<AutoTradeRiskVerdict> EvaluateAsync(
        string symbol,
        decimal quantity,
        decimal entryPrice,
        int leverage,
        CancellationToken cancellationToken);
}
