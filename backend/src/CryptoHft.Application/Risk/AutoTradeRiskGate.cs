namespace CryptoHft.Application.Risk;

// Account-level risk gate consulted right before an auto order is placed. It never judges
// the signal itself — confidence remains the only gate that decides WHETHER to trade. This
// gate protects the account: it pauses trading after the daily-loss or consecutive-loss
// limit is hit, and trims the order so its margin fits the exposure limit.
public sealed record AutoTradeRiskVerdict(
    bool Allowed,
    string Reason,
    decimal? AdjustedQuantity = null);

// Read-only snapshot for dashboard observability. ResetsAt is populated only for
// account limits that reset with Binance's UTC realized-PnL day.
public sealed record AutoTradeRiskStatus(
    bool TradingAllowed,
    string Status,
    string Reason,
    decimal? Equity,
    decimal? DailyLoss,
    decimal? DailyLossLimit,
    decimal? DailyLossLimitPercent,
    int? ConsecutiveLosses,
    int MaxConsecutiveLosses,
    DateTimeOffset? ResetsAt,
    DateTimeOffset CheckedAt);

public interface IAutoTradeRiskGate
{
    Task<AutoTradeRiskStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<AutoTradeRiskVerdict> EvaluateAsync(
        string symbol,
        decimal quantity,
        decimal entryPrice,
        int leverage,
        CancellationToken cancellationToken);
}
