using System.Net.WebSockets;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CryptoHft.Application.Abstractions;
using CryptoHft.Application.MarketData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Binance;

public sealed class BinanceFuturesWebSocketStream(
    IOptions<BinanceOptions> options,
    IRealtimePublisher publisher,
    CryptoHft.Application.DecisionEngine.ILiquidationFeed liquidationFeed,
    ILogger<BinanceFuturesWebSocketStream> logger) : IMarketDataStream
{
    private readonly BinanceOptions _options = options.Value;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var streams = BuildStreams();
        var url = BuildWebSocketUrl(streams);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(url), cancellationToken);
                logger.LogInformation("Connected Binance Futures WebSocket {Url}", url);

                var buffer = new byte[64 * 1024];
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await DispatchAsync(json, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Binance WebSocket disconnected. Reconnecting in 5 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private IReadOnlyList<string> BuildStreams()
    {
        var symbol = _options.Symbol.ToLowerInvariant();
        var klineStreams = _options.KlineIntervals.Select(interval => $"{symbol}@kline_{interval}");
        var streams = new[]
        {
            $"{symbol}@trade",
            $"{symbol}@aggTrade",
            $"{symbol}@markPrice@1s",
            $"{symbol}@depth20@100ms",
            $"{symbol}@forceOrder"
        }.Concat(klineStreams);

        return streams.Distinct().ToArray();
    }

    private string BuildWebSocketUrl(IReadOnlyList<string> streams)
    {
        var baseUrl = _options.WebSocketBaseUrl.TrimEnd('/');
        if (streams.Count == 1)
        {
            return $"{baseUrl}/{streams[0]}";
        }

        var streamBaseUrl = baseUrl.EndsWith("/ws", StringComparison.OrdinalIgnoreCase)
            ? baseUrl[..^3]
            : baseUrl;

        return $"{streamBaseUrl}/stream?streams={string.Join("/", streams)}";
    }

    private async Task DispatchAsync(string json, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("data", out var data))
        {
            root = data;
        }

        var eventType = root.TryGetProperty("e", out var e) ? e.GetString() : null;

        switch (eventType)
        {
            case "aggTrade":
                await publisher.PublishAggTradeAsync(ParseAggTrade(root), cancellationToken);
                break;
            case "markPriceUpdate":
                var markPrice = ParseMarkPrice(root);
                await publisher.PublishMarkPriceAsync(markPrice, cancellationToken);
                await publisher.PublishPriceAsync(new PriceTick(markPrice.Symbol, markPrice.MarkPrice, markPrice.Time), cancellationToken);
                break;
            case "depthUpdate":
                await publisher.PublishOrderBookAsync(ParseOrderBook(root), cancellationToken);
                break;
            case "kline":
                await publisher.PublishKlineAsync(ParseKline(root), cancellationToken);
                break;
            case "forceOrder":
                RecordLiquidation(root);
                break;
        }
    }

    // forceOrder = a position was force-closed. Order side SELL means a LONG was liquidated
    // (its exit is a forced sell); side BUY means a SHORT was liquidated. Notional in USD
    // from qty x average fill price (falls back to order price when unfilled).
    private void RecordLiquidation(JsonElement root)
    {
        var order = root.GetProperty("o");
        var side = order.GetProperty("S").GetString() ?? "";
        var qty = ParseDecimal(order.GetProperty("q"));
        var price = TryParseDecimal(order, "ap");
        if (price == 0) price = ParseDecimal(order.GetProperty("p"));
        var time = order.TryGetProperty("T", out var t)
            ? DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64())
            : DateTimeOffset.UtcNow;

        liquidationFeed.Record(
            longLiquidated: side.Equals("SELL", StringComparison.OrdinalIgnoreCase),
            notionalUsd: qty * price,
            time: time);
    }

    private static AggTradeTick ParseAggTrade(JsonElement root)
    {
        return new AggTradeTick(
            root.GetProperty("s").GetString() ?? "BTCUSDT",
            ParseDecimal(root.GetProperty("p")),
            ParseDecimal(root.GetProperty("q")),
            root.GetProperty("m").GetBoolean(),
            DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("T").GetInt64()));
    }

    private static MarkPriceTick ParseMarkPrice(JsonElement root)
    {
        return new MarkPriceTick(
            root.GetProperty("s").GetString() ?? "BTCUSDT",
            ParseDecimal(root.GetProperty("p")),
            TryParseDecimal(root, "ap"),
            ParseDecimal(root.GetProperty("i")),
            TryParseDecimal(root, "P"),
            ParseDecimal(root.GetProperty("r")),
            DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("T").GetInt64()),
            DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("E").GetInt64()));
    }

    private static OrderBookSnapshot ParseOrderBook(JsonElement root)
    {
        var bids = ParseLevels(root.GetProperty("b"));
        var asks = ParseLevels(root.GetProperty("a"));
        var bestBid = bids.FirstOrDefault()?.Price ?? 0;
        var bestAsk = asks.FirstOrDefault()?.Price ?? 0;
        var bidQty = bids.Sum(x => x.Quantity);
        var askQty = asks.Sum(x => x.Quantity);
        var imbalance = bidQty + askQty == 0 ? 0 : (bidQty - askQty) / (bidQty + askQty);

        return new OrderBookSnapshot(
            root.GetProperty("s").GetString() ?? "BTCUSDT",
            bids,
            asks,
            bestAsk - bestBid,
            imbalance,
            DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<OrderBookLevel> ParseLevels(JsonElement levels)
    {
        return levels.EnumerateArray()
            .Select(level => new OrderBookLevel(
                ParseDecimal(level[0]),
                ParseDecimal(level[1])))
            .ToList();
    }

    private static KlineTick ParseKline(JsonElement root)
    {
        var kline = root.GetProperty("k");
        return new KlineTick(
            root.GetProperty("s").GetString() ?? "BTCUSDT",
            kline.GetProperty("i").GetString() ?? "1m",
            DateTimeOffset.FromUnixTimeMilliseconds(kline.GetProperty("t").GetInt64()),
            DateTimeOffset.FromUnixTimeMilliseconds(kline.GetProperty("T").GetInt64()),
            ParseDecimal(kline.GetProperty("o")),
            ParseDecimal(kline.GetProperty("h")),
            ParseDecimal(kline.GetProperty("l")),
            ParseDecimal(kline.GetProperty("c")),
            ParseDecimal(kline.GetProperty("v")),
            ParseDecimal(kline.GetProperty("q")),
            kline.GetProperty("n").GetInt64(),
            ParseDecimal(kline.GetProperty("V")),
            ParseDecimal(kline.GetProperty("Q")),
            kline.GetProperty("x").GetBoolean(),
            DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("E").GetInt64()));
    }

    private static decimal TryParseDecimal(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) ? ParseDecimal(value) : 0;
    }

    private static decimal ParseDecimal(JsonElement value)
    {
        return decimal.Parse(value.GetString() ?? "0", CultureInfo.InvariantCulture);
    }
}
