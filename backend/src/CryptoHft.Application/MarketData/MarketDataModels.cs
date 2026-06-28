namespace CryptoHft.Application.MarketData;

public sealed record PriceTick(string Symbol, decimal Price, DateTimeOffset Time);

public sealed record MarkPriceTick(
    string Symbol,
    decimal MarkPrice,
    decimal MarkPriceMovingAverage,
    decimal IndexPrice,
    decimal EstimatedSettlePrice,
    decimal FundingRate,
    DateTimeOffset NextFundingTime,
    DateTimeOffset Time);

public sealed record OrderBookLevel(decimal Price, decimal Quantity);

public sealed record OrderBookSnapshot(
    string Symbol,
    IReadOnlyList<OrderBookLevel> Bids,
    IReadOnlyList<OrderBookLevel> Asks,
    decimal Spread,
    decimal Imbalance,
    DateTimeOffset Time);

public sealed record AggTradeTick(
    string Symbol,
    decimal Price,
    decimal Quantity,
    bool BuyerIsMaker,
    DateTimeOffset Time);

public sealed record KlineTick(
    string Symbol,
    string Interval,
    DateTimeOffset OpenTime,
    DateTimeOffset CloseTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal QuoteVolume,
    long NumberOfTrades,
    decimal TakerBuyBaseVolume,
    decimal TakerBuyQuoteVolume,
    bool IsClosed,
    DateTimeOffset EventTime);

public sealed record MarginCallPosition(
    string Symbol,
    string PositionSide,
    decimal PositionAmount,
    string MarginType,
    decimal IsolatedWallet,
    decimal MarkPrice,
    decimal UnrealizedPnl,
    decimal MaintenanceMarginRequired);

public sealed record MarginCallEvent(
    string Symbol,
    decimal CrossWalletBalance,
    IReadOnlyList<MarginCallPosition> Positions,
    DateTimeOffset EventTime);

public sealed record UserDataStreamExpiredEvent(
    string Symbol,
    string ListenKey,
    DateTimeOffset EventTime);

public sealed record AccountBalanceUpdate(
    string Asset,
    decimal WalletBalance,
    decimal CrossWalletBalance,
    decimal BalanceChange);

public sealed record AccountPositionUpdate(
    string Symbol,
    string PositionSide,
    decimal PositionAmount,
    decimal EntryPrice,
    decimal BreakEvenPrice,
    decimal AccumulatedRealized,
    decimal UnrealizedProfit,
    string MarginType,
    decimal IsolatedWallet);

public sealed record AccountUpdateEvent(
    string Symbol,
    string Reason,
    IReadOnlyList<AccountBalanceUpdate> Balances,
    IReadOnlyList<AccountPositionUpdate> Positions,
    DateTimeOffset EventTime,
    DateTimeOffset TransactionTime);

public sealed record OrderUpdateEvent(
    string Symbol,
    long OrderId,
    string ClientOrderId,
    string Side,
    string OrderType,
    string ExecutionType,
    string OrderStatus,
    string TimeInForce,
    decimal OriginalQuantity,
    decimal OriginalPrice,
    decimal AveragePrice,
    decimal StopPrice,
    decimal LastFilledQuantity,
    decimal AccumulatedFilledQuantity,
    decimal LastFilledPrice,
    decimal RealizedProfit,
    bool ReduceOnly,
    string PositionSide,
    string WorkingType,
    DateTimeOffset OrderTradeTime,
    DateTimeOffset EventTime);

public sealed record MarketSnapshot(
    PriceTick? LastPrice,
    MarkPriceTick? MarkPrice,
    OrderBookSnapshot? OrderBook,
    KlineTick? CurrentKline,
    decimal OpenInterest,
    decimal FundingRate);
