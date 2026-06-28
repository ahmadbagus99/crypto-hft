using System.Globalization;
using System.Text.Json;
using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Exchange;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Binance;

public sealed class BinanceFuturesExchangeInfoClient(
    IHttpClientFactory httpClientFactory,
    IOptions<BinanceOptions> options) : IFuturesExchangeInfoClient
{
    private readonly BinanceOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, FuturesSymbolRules> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<FuturesSymbolRules> GetSymbolRulesAsync(string symbol, CancellationToken cancellationToken)
    {
        symbol = symbol.ToUpperInvariant();
        if (_cache.TryGetValue(symbol, out var cached) && _expiresAt > DateTimeOffset.UtcNow)
        {
            return cached;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(symbol, out cached) && _expiresAt > DateTimeOffset.UtcNow)
            {
                return cached;
            }

            await RefreshAsync(cancellationToken);
            return _cache.TryGetValue(symbol, out var rules)
                ? rules
                : throw new InvalidOperationException($"Binance exchangeInfo symbol {symbol} not found.");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var url = $"{_options.RestBaseUrl.TrimEnd('/')}/fapi/v1/exchangeInfo";
        using var response = await httpClientFactory.CreateClient().GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(body);
        }

        using var document = JsonDocument.Parse(body);
        var updatedAt = DateTimeOffset.UtcNow;
        var next = new Dictionary<string, FuturesSymbolRules>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in document.RootElement.GetProperty("symbols").EnumerateArray())
        {
            var rules = ParseSymbolRules(item, updatedAt);
            next[rules.Symbol] = rules;
        }

        _cache.Clear();
        foreach (var pair in next)
        {
            _cache[pair.Key] = pair.Value;
        }

        _expiresAt = updatedAt.AddMinutes(60);
    }

    private static FuturesSymbolRules ParseSymbolRules(JsonElement symbol, DateTimeOffset updatedAt)
    {
        var filters = symbol.GetProperty("filters").EnumerateArray().ToDictionary(
            filter => filter.GetProperty("filterType").GetString() ?? "",
            StringComparer.OrdinalIgnoreCase);

        var priceFilter = filters.GetValueOrDefault("PRICE_FILTER");
        var lotSize = filters.GetValueOrDefault("LOT_SIZE");
        var marketLotSize = filters.GetValueOrDefault("MARKET_LOT_SIZE");
        var minNotional = filters.GetValueOrDefault("MIN_NOTIONAL");

        return new FuturesSymbolRules(
            Symbol: symbol.GetProperty("symbol").GetString() ?? "",
            Status: symbol.GetProperty("status").GetString() ?? "",
            MinPrice: TryParseDecimal(priceFilter, "minPrice"),
            MaxPrice: TryParseDecimal(priceFilter, "maxPrice"),
            TickSize: TryParseDecimal(priceFilter, "tickSize"),
            MinQuantity: TryParseDecimal(lotSize, "minQty"),
            MaxQuantity: TryParseDecimal(lotSize, "maxQty"),
            StepSize: TryParseDecimal(lotSize, "stepSize"),
            MarketMinQuantity: TryParseDecimal(marketLotSize, "minQty"),
            MarketMaxQuantity: TryParseDecimal(marketLotSize, "maxQty"),
            MarketStepSize: TryParseDecimal(marketLotSize, "stepSize"),
            MinNotional: TryParseDecimal(minNotional, "notional"),
            PricePrecision: symbol.TryGetProperty("pricePrecision", out var pricePrecision) ? pricePrecision.GetInt32() : 0,
            QuantityPrecision: symbol.TryGetProperty("quantityPrecision", out var quantityPrecision) ? quantityPrecision.GetInt32() : 0,
            UpdatedAt: updatedAt);
    }

    private static decimal TryParseDecimal(JsonElement element, string propertyName)
    {
        return element.ValueKind != JsonValueKind.Undefined && element.TryGetProperty(propertyName, out var value)
            ? ParseDecimal(value)
            : 0;
    }

    private static decimal ParseDecimal(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : decimal.Parse(value.GetString() ?? "0", CultureInfo.InvariantCulture);
    }
}

