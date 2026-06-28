using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.Trading;

public sealed record TradeOrderRequest(
    string Symbol,
    TradeSide Side,
    OrderKind Kind,
    decimal Quantity,
    decimal? Price,
    decimal? StopPrice,
    decimal? TakeProfit,
    decimal? StopLoss,
    int Leverage,
    bool ReduceOnly,
    TradingMode Mode,
    string Reason);

public sealed record ClosePositionRequest(string Symbol, TradeSide Side, decimal? Quantity, string Reason);

public sealed record TradeOrderResult(
    string Symbol,
    string OrderId,
    OrderStatus Status,
    decimal Quantity,
    decimal? Price,
    bool IsPaper,
    string Message,
    DateTimeOffset Time);
