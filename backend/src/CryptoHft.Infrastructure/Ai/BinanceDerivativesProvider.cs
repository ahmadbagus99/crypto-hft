using System.Globalization;
using System.Text.Json;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Infrastructure.Binance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Ai;

// Pulls funding rate (spot + cumulative 24h), open interest, long/short ratio, taker
// buy/sell volume, and order-book imbalance from Binance Futures public REST endpoints
// (no API key needed), plus the rolling liquidation window from the forceOrder stream.
public sealed class BinanceDerivativesProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<BinanceOptions> options,
    ILiquidationFeed liquidationFeed,
    ILogger<BinanceDerivativesProvider> logger) : IDerivativesDataProvider
{
    // Liquidation cascades resolve in minutes; a 5-minute window captures the burst
    // without letting an old flush linger into unrelated ticks.
    private static readonly TimeSpan LiquidationWindow = TimeSpan.FromMinutes(5);

    // Order-book levels aggregated for the imbalance read. Must stay one of Binance's
    // accepted limits (5/10/20/50/100/500/1000).
    private const int DepthLevels = 500;

    private readonly BinanceOptions _options = options.Value;

    public async Task<DerivativesSnapshot> GetSnapshotAsync(string symbol, CancellationToken cancellationToken)
    {
        symbol = symbol.ToUpperInvariant();
        var baseUrl = _options.RestBaseUrl.TrimEnd('/');
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);

        decimal funding = 0, cumFunding = 0, oi = 0, oiChange = 0, longShort = 1m, takerRatio = 1m, imbalance = 0, spread = 0;

        try
        {
            var premium = await client.GetStringAsync($"{baseUrl}/fapi/v1/premiumIndex?symbol={symbol}", cancellationToken);
            using var pdoc = JsonDocument.Parse(premium);
            funding = Parse(pdoc.RootElement.GetProperty("lastFundingRate"));
        }
        catch (Exception ex) { logger.LogDebug(ex, "funding fetch failed"); }

        try
        {
            // Last 3 settled funding periods (8h each) ≈ 24h of cumulative funding — separates
            // a persistently crowded market from a single stretched print.
            var hist = await client.GetStringAsync($"{baseUrl}/fapi/v1/fundingRate?symbol={symbol}&limit=3", cancellationToken);
            using var fdoc = JsonDocument.Parse(hist);
            foreach (var item in fdoc.RootElement.EnumerateArray())
                cumFunding += Parse(item.GetProperty("fundingRate"));
        }
        catch (Exception ex) { logger.LogDebug(ex, "funding history fetch failed"); }

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
            // Depth is read 500 levels deep, not 20. The top of the BTCUSDT book spans a few
            // dollars and is dominated by quotes that appear and vanish within seconds: sampled
            // live, the 20-level imbalance swung between scores of 20 and 89 over 20 seconds,
            // while the 500-level read of the same book stayed inside a 9-point band. The
            // shallow number is a microstructure tick, and feeding it to a category worth 17%
            // of a directional vote — for a position held hours — injected more noise than
            // signal. Deeper levels measure resting liquidity, which is what "which side is
            // stacked" is supposed to mean. Cost: request weight 2 -> 10, negligible at one
            // call per 30s.
            var depth = await client.GetStringAsync($"{baseUrl}/fapi/v1/depth?symbol={symbol}&limit={DepthLevels}", cancellationToken);
            using var ddoc = JsonDocument.Parse(depth);
            decimal bidVol = 0, askVol = 0;
            foreach (var b in ddoc.RootElement.GetProperty("bids").EnumerateArray()) bidVol += Parse(b[1]);
            foreach (var a in ddoc.RootElement.GetProperty("asks").EnumerateArray()) askVol += Parse(a[1]);
            var total = bidVol + askVol;
            imbalance = total == 0 ? 0 : (bidVol - askVol) / total;
            // Spread still comes from the best bid/ask — that one IS a top-of-book quantity.
            var bestBid = Parse(ddoc.RootElement.GetProperty("bids")[0][0]);
            var bestAsk = Parse(ddoc.RootElement.GetProperty("asks")[0][0]);
            spread = bestAsk - bestBid;
        }
        catch (Exception ex) { logger.LogDebug(ex, "depth fetch failed"); }

        var (longLiq, shortLiq) = liquidationFeed.GetWindowNotional(LiquidationWindow);

        return new DerivativesSnapshot(
            funding, oi, oiChange, longShort, takerRatio, imbalance, spread,
            cumFunding, longLiq, shortLiq);
    }

    private static decimal Parse(JsonElement el)
        => el.ValueKind == JsonValueKind.Number
            ? el.GetDecimal()
            : decimal.Parse(el.GetString() ?? "0", CultureInfo.InvariantCulture);
}
