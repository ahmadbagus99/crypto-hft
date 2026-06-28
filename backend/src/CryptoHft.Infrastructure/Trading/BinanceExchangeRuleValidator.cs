using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Exchange;
using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;

namespace CryptoHft.Infrastructure.Trading;

public sealed class BinanceExchangeRuleValidator(IFuturesExchangeInfoClient exchangeInfoClient) : IExchangeRuleValidator
{
    public async Task<NormalizedOrder> NormalizeAndValidateAsync(TradeOrderRequest request, CancellationToken cancellationToken)
    {
        var rules = await exchangeInfoClient.GetSymbolRulesAsync(request.Symbol, cancellationToken);
        if (!string.Equals(rules.Status, "TRADING", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{request.Symbol} is not tradable. Status: {rules.Status}");
        }

        var adjustments = new List<string>();
        var quantityStep = request.Kind == OrderKind.Market && rules.MarketStepSize > 0 ? rules.MarketStepSize : rules.StepSize;
        var minQuantity = request.Kind == OrderKind.Market && rules.MarketMinQuantity > 0 ? rules.MarketMinQuantity : rules.MinQuantity;
        var maxQuantity = request.Kind == OrderKind.Market && rules.MarketMaxQuantity > 0 ? rules.MarketMaxQuantity : rules.MaxQuantity;
        var quantity = RoundDown(request.Quantity, quantityStep);

        if (quantity != request.Quantity)
        {
            adjustments.Add($"quantity {request.Quantity} -> {quantity}");
        }

        if (quantity <= 0 || quantity < minQuantity)
        {
            throw new InvalidOperationException($"Quantity {quantity} below Binance minQty {minQuantity} for {request.Symbol}.");
        }

        if (maxQuantity > 0 && quantity > maxQuantity)
        {
            throw new InvalidOperationException($"Quantity {quantity} above Binance maxQty {maxQuantity} for {request.Symbol}.");
        }

        var price = NormalizePrice(request.Price, rules.TickSize, "price", adjustments);
        var stopPrice = NormalizePrice(request.StopPrice, rules.TickSize, "stopPrice", adjustments);
        var takeProfit = NormalizePrice(request.TakeProfit, rules.TickSize, "takeProfit", adjustments);
        var stopLoss = NormalizePrice(request.StopLoss, rules.TickSize, "stopLoss", adjustments);

        ValidatePriceRange(price, rules, "price");
        ValidatePriceRange(stopPrice, rules, "stopPrice");
        ValidatePriceRange(takeProfit, rules, "takeProfit");
        ValidatePriceRange(stopLoss, rules, "stopLoss");
        ValidateRequiredPrices(request.Kind, price, stopPrice);
        ValidateNotional(request.Kind, quantity, price ?? stopPrice, rules);

        return new NormalizedOrder(request with
        {
            Quantity = quantity,
            Price = price,
            StopPrice = stopPrice,
            TakeProfit = takeProfit,
            StopLoss = stopLoss
        }, adjustments);
    }

    private static decimal? NormalizePrice(decimal? value, decimal tickSize, string label, List<string> adjustments)
    {
        if (value is null) return null;

        var normalized = RoundDown(value.Value, tickSize);
        if (normalized != value.Value)
        {
            adjustments.Add($"{label} {value.Value} -> {normalized}");
        }

        return normalized;
    }

    private static void ValidatePriceRange(decimal? value, FuturesSymbolRules rules, string label)
    {
        if (value is null || value <= 0) return;

        if (rules.MinPrice > 0 && value < rules.MinPrice)
        {
            throw new InvalidOperationException($"{label} {value} below Binance minPrice {rules.MinPrice} for {rules.Symbol}.");
        }

        if (rules.MaxPrice > 0 && value > rules.MaxPrice)
        {
            throw new InvalidOperationException($"{label} {value} above Binance maxPrice {rules.MaxPrice} for {rules.Symbol}.");
        }
    }

    private static void ValidateRequiredPrices(OrderKind kind, decimal? price, decimal? stopPrice)
    {
        if (kind is OrderKind.Limit or OrderKind.StopLimit && price is null)
        {
            throw new InvalidOperationException($"{kind} order requires price.");
        }

        if (kind is OrderKind.StopMarket or OrderKind.StopLimit or OrderKind.TakeProfit && stopPrice is null)
        {
            throw new InvalidOperationException($"{kind} order requires stopPrice.");
        }
    }

    private static void ValidateNotional(OrderKind kind, decimal quantity, decimal? price, FuturesSymbolRules rules)
    {
        if (rules.MinNotional <= 0 || kind == OrderKind.Market || price is null || price <= 0) return;

        var notional = quantity * price.Value;
        if (notional < rules.MinNotional)
        {
            throw new InvalidOperationException($"Notional {notional} below Binance minNotional {rules.MinNotional} for {rules.Symbol}.");
        }
    }

    private static decimal RoundDown(decimal value, decimal step)
    {
        if (step <= 0) return value;
        return Math.Floor(value / step) * step;
    }
}

