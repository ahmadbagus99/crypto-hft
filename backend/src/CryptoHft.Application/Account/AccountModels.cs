namespace CryptoHft.Application.Account;

public sealed record FuturesWalletBalance(
    string AccountAlias,
    string Asset,
    decimal Balance,
    decimal CrossWalletBalance,
    decimal CrossUnrealizedPnl,
    decimal AvailableBalance,
    decimal MaxWithdrawAmount,
    bool IsMarginAvailable,
    DateTimeOffset UpdateTime);

public sealed record FuturesPositionInfo(
    string Symbol,
    string PositionSide,
    decimal PositionAmount,
    decimal EntryPrice,
    decimal BreakEvenPrice,
    decimal MarkPrice,
    decimal UnrealizedProfit,
    decimal LiquidationPrice,
    decimal Leverage,
    decimal MaxNotionalValue,
    string MarginType,
    decimal IsolatedMargin,
    bool IsAutoAddMargin,
    DateTimeOffset UpdateTime);
