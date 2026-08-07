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
    // 1m is fetched for the scalper's timing gate, which needs to see whether a reversal bar
    // has actually closed rather than inferring it from a 5m average. Intraday ignores it:
    // its vote weights never name 1m, so the extra series changes nothing on that path.
    private static readonly string[] Intervals = ["1m", "5m", "15m", "1h", "4h", "1d"];
    private readonly BinanceOptions _options = options.Value;

    public async Task<IReadOnlyList<TimeframeData>> GetTimeframesAsync(string symbol, CancellationToken cancellationToken)
    {
        symbol = symbol.ToUpperInvariant();
        var baseUrl = _options.RestBaseUrl.TrimEnd('/');
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        var tasks = Intervals.Select(async interval =>
        {
            // limit=251 so ~250 candles remain after the still-forming one is dropped.
            var url = $"{baseUrl}/fapi/v1/klines?symbol={symbol}&interval={interval}&limit=251";
            var body = await client.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(body);
            return new TimeframeData(interval, ParseClosedCandles(doc.RootElement, DateTimeOffset.UtcNow));
        });

        return await Task.WhenAll(tasks);
    }

    // Binance klines include the still-forming candle as the last element. Feeding it to the
    // indicators repaints signals (a liquidity sweep or impulse candle can vanish before close)
    // and makes live behavior diverge from any closed-candle backtest, so only candles whose
    // close time (kline field 6) has passed are returned.
    internal static List<Candle> ParseClosedCandles(JsonElement root, DateTimeOffset nowUtc)
    {
        var candles = new List<Candle>();
        foreach (var item in root.EnumerateArray())
        {
            var closeTime = DateTimeOffset.FromUnixTimeMilliseconds(item[6].GetInt64());
            if (closeTime > nowUtc) continue;
            // Field 9 = taker buy base-asset volume; feeds cumulative volume delta (CVD).
            candles.Add(new Candle(
                DateTimeOffset.FromUnixTimeMilliseconds(item[0].GetInt64()),
                Parse(item[1]), Parse(item[2]), Parse(item[3]), Parse(item[4]), Parse(item[5]),
                item.GetArrayLength() > 9 ? Parse(item[9]) : 0m));
        }
        return candles;
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
