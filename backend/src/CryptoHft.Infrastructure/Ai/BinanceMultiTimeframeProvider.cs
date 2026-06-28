using System.Globalization;
using System.Text.Json;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Infrastructure.Binance;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Ai;

// Fetches candles across multiple timeframes for confluence analysis.
public sealed class BinanceMultiTimeframeProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<BinanceOptions> options) : IMultiTimeframeProvider
{
    private static readonly string[] Intervals = ["5m", "15m", "1h", "4h", "1d"];
    private readonly BinanceOptions _options = options.Value;

    public async Task<IReadOnlyList<TimeframeData>> GetTimeframesAsync(string symbol, CancellationToken cancellationToken)
    {
        symbol = symbol.ToUpperInvariant();
        var baseUrl = _options.RestBaseUrl.TrimEnd('/');
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        var tasks = Intervals.Select(async interval =>
        {
            var url = $"{baseUrl}/fapi/v1/klines?symbol={symbol}&interval={interval}&limit=250";
            var body = await client.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(body);
            var candles = doc.RootElement.EnumerateArray().Select(item => new Candle(
                DateTimeOffset.FromUnixTimeMilliseconds(item[0].GetInt64()),
                Parse(item[1]), Parse(item[2]), Parse(item[3]), Parse(item[4]), Parse(item[5]))).ToList();
            return new TimeframeData(interval, candles);
        });

        return await Task.WhenAll(tasks);
    }

    public async Task<decimal> GetLastPriceAsync(string symbol, CancellationToken cancellationToken)
    {
        symbol = symbol.ToUpperInvariant();
        var baseUrl = _options.RestBaseUrl.TrimEnd('/');
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        var body = await client.GetStringAsync($"{baseUrl}/fapi/v1/ticker/price?symbol={symbol}", cancellationToken);
        using var doc = JsonDocument.Parse(body);
        return Parse(doc.RootElement.GetProperty("price"));
    }

    private static decimal Parse(JsonElement el)
        => el.ValueKind == JsonValueKind.Number
            ? el.GetDecimal()
            : decimal.Parse(el.GetString() ?? "0", CultureInfo.InvariantCulture);
}
