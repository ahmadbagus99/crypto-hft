using System.Globalization;
using System.Text.Json;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Infrastructure.Binance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Ai;

// Pulls funding rate, open interest, long/short ratio, taker buy/sell volume, and
// order-book imbalance from Binance Futures public REST endpoints (no API key needed).
public sealed class BinanceDerivativesProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<BinanceOptions> options,
    ILogger<BinanceDerivativesProvider> logger) : IDerivativesDataProvider
{
    private readonly BinanceOptions _options = options.Value;

    public async Task<DerivativesSnapshot> GetSnapshotAsync(string symbol, CancellationToken cancellationToken)
    {
        symbol = symbol.ToUpperInvariant();
        var baseUrl = _options.RestBaseUrl.TrimEnd('/');
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);

        decimal funding = 0, oi = 0, oiChange = 0, longShort = 1m, takerRatio = 1m, imbalance = 0, spread = 0;

        try
        {
            var premium = await client.GetStringAsync($"{baseUrl}/fapi/v1/premiumIndex?symbol={symbol}", cancellationToken);
            using var pdoc = JsonDocument.Parse(premium);
            funding = Parse(pdoc.RootElement.GetProperty("lastFundingRate"));
        }
        catch (Exception ex) { logger.LogDebug(ex, "funding fetch failed"); }

        try
        {
            var oiJson = await client.GetStringAsync($"{baseUrl}/fapi/v1/openInterest?symbol={symbol}", cancellationToken);
            using var odoc = JsonDocument.Parse(oiJson);
            oi = Parse(odoc.RootElement.GetProperty("openInterest"));

            var oiHist = await client.GetStringAsync($"{baseUrl}/futures/data/openInterestHist?symbol={symbol}&period=5m&limit=2", cancellationToken);
            using var hdoc = JsonDocument.Parse(oiHist);
            var arr = hdoc.RootElement;
            if (arr.GetArrayLength() >= 2)
            {
                var prev = Parse(arr[0].GetProperty("sumOpenInterest"));
                var now = Parse(arr[1].GetProperty("sumOpenInterest"));
                oiChange = prev == 0 ? 0 : (now - prev) / prev * 100m;
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "OI fetch failed"); }

        try
        {
            var ls = await client.GetStringAsync($"{baseUrl}/futures/data/globalLongShortAccountRatio?symbol={symbol}&period=5m&limit=1", cancellationToken);
            using var ldoc = JsonDocument.Parse(ls);
            if (ldoc.RootElement.GetArrayLength() > 0)
                longShort = Parse(ldoc.RootElement[0].GetProperty("longShortRatio"));
        }
        catch (Exception ex) { logger.LogDebug(ex, "long/short fetch failed"); }

        try
        {
            var tv = await client.GetStringAsync($"{baseUrl}/futures/data/takerlongshortRatio?symbol={symbol}&period=5m&limit=1", cancellationToken);
            using var tdoc = JsonDocument.Parse(tv);
            if (tdoc.RootElement.GetArrayLength() > 0)
                takerRatio = Parse(tdoc.RootElement[0].GetProperty("buySellRatio"));
        }
        catch (Exception ex) { logger.LogDebug(ex, "taker ratio fetch failed"); }

        try
        {
            var depth = await client.GetStringAsync($"{baseUrl}/fapi/v1/depth?symbol={symbol}&limit=20", cancellationToken);
            using var ddoc = JsonDocument.Parse(depth);
            decimal bidVol = 0, askVol = 0;
            foreach (var b in ddoc.RootElement.GetProperty("bids").EnumerateArray()) bidVol += Parse(b[1]);
            foreach (var a in ddoc.RootElement.GetProperty("asks").EnumerateArray()) askVol += Parse(a[1]);
            var total = bidVol + askVol;
            imbalance = total == 0 ? 0 : (bidVol - askVol) / total;
            var bestBid = Parse(ddoc.RootElement.GetProperty("bids")[0][0]);
            var bestAsk = Parse(ddoc.RootElement.GetProperty("asks")[0][0]);
            spread = bestAsk - bestBid;
        }
        catch (Exception ex) { logger.LogDebug(ex, "depth fetch failed"); }

        return new DerivativesSnapshot(funding, oi, oiChange, longShort, takerRatio, imbalance, spread);
    }

    private static decimal Parse(JsonElement el)
        => el.ValueKind == JsonValueKind.Number
            ? el.GetDecimal()
            : decimal.Parse(el.GetString() ?? "0", CultureInfo.InvariantCulture);
}
