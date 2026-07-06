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

// Replace the outstanding protective stop of an open position with a new trigger price
// (trailing/breakeven ratchet). PositionSide is the side of the POSITION, not of the stop
// order — the executor derives the reduce-only close side itself.
public sealed record AmendStopLossRequest(
    string Symbol,
    TradeSide PositionSide,
    decimal Quantity,
    decimal NewStopPrice,
    string Reason);

public sealed record TradeOrderResult(
    string Symbol,
    string OrderId,
    OrderStatus Status,
    decimal Quantity,
    decimal? Price,
    bool IsPaper,
    string Message,
    DateTimeOffset Time);
