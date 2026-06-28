using CryptoHft.Application.Trading;

namespace CryptoHft.Application.Exchange;

public sealed record FuturesSymbolRules(
    string Symbol,
    string Status,
    decimal MinPrice,
    decimal MaxPrice,
    decimal TickSize,
    decimal MinQuantity,
    decimal MaxQuantity,
    decimal StepSize,
    decimal MarketMinQuantity,
    decimal MarketMaxQuantity,
    decimal MarketStepSize,
    decimal MinNotional,
    int PricePrecision,
    int QuantityPrecision,
    DateTimeOffset UpdatedAt);

public sealed record NormalizedOrder(
    TradeOrderRequest Request,
    IReadOnlyList<string> Adjustments);

