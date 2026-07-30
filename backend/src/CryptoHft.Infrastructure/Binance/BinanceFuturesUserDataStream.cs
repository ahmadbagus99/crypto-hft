using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CryptoHft.Application.Abstractions;
using CryptoHft.Application.MarketData;
using CryptoHft.Application.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Binance;

public sealed class BinanceFuturesUserDataStream(
    IOptions<BinanceOptions> options,
    IRuntimeTradingSettingsService settingsService,
    IHttpClientFactory httpClientFactory,
    IRealtimePublisher publisher,
    ILogger<BinanceFuturesUserDataStream> logger) : IUserDataStream
{
    private readonly BinanceOptions _options = options.Value;

    // The key the account actually trades with lives in the settings row the dashboard
    // writes, not in the environment. Reading only IOptions here meant a fully configured
    // account still logged "user data stream disabled because API key is empty" and fell
    // back to 30-second REST polling for fills — which is also what dated the mark price
    // the close classifier reads. The executor and the risk gate already resolve the key
    // this way; this stream was the odd one out.
    private string? ApiKey()
    {
        var runtime = settingsService.GetRuntimeSettings().ApiKey;
        return string.IsNullOrWhiteSpace(runtime) ? _options.ApiKey : runtime;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Re-checked every pass, not once at startup: settings are hydrated from the
            // database after the host starts, and the key can be entered at any time.
            if (string.IsNullOrWhiteSpace(ApiKey()))
            {
                logger.LogInformation(
                    "Binance user data stream idle: no API key configured yet. Rechecking in 60 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
                continue;
            }

            try
            {
                var listenKey = await CreateListenKeyAsync(cancellationToken);
                using var keepAlive = StartKeepAlive(cancellationToken);
                await ConnectAsync(listenKey, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Binance user data stream disconnected or unavailable. Reconnecting in 60 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            }
        }
    }

    private async Task ConnectAsync(string listenKey, CancellationToken cancellationToken)
    {
        var streamBaseUrl = _options.WebSocketBaseUrl.TrimEnd('/');
        var url = $"{streamBaseUrl}/{listenKey}";

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(url), cancellationToken);
        // The listenKey is part of the URL and is a bearer token for this account's private
        // stream until it expires — log the endpoint, never the key itself.
        logger.LogInformation("Connected Binance Futures user data stream {Endpoint}", streamBaseUrl);

        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) break;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (!await DispatchAsync(json, listenKey, cancellationToken))
            {
                break;
            }
        }
    }

    private async Task<bool> DispatchAsync(string json, string listenKey, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var eventType = root.TryGetProperty("e", out var e) ? e.GetString() : null;

        if (eventType == "MARGIN_CALL")
        {
            await publisher.PublishMarginCallAsync(ParseMarginCall(root), cancellationToken);
        }

        if (eventType == "ACCOUNT_UPDATE")
        {
            await publisher.PublishAccountUpdateAsync(ParseAccountUpdate(root), cancellationToken);
        }

        if (eventType == "ORDER_TRADE_UPDATE")
        {
            await publisher.PublishOrderUpdateAsync(ParseOrderUpdate(root), cancellationToken);
        }

        if (eventType == "listenKeyExpired")
        {
            var expired = ParseUserDataStreamExpired(root, listenKey);
            await publisher.PublishUserDataStreamExpiredAsync(expired, cancellationToken);
            logger.LogWarning("Binance user data stream listenKey expired at {EventTime}. Reconnecting.", expired.EventTime);
            return false;
        }

        return true;
    }

    private async Task<string> CreateListenKeyAsync(CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.RestBaseUrl.TrimEnd('/')}/fapi/v1/listenKey");
        request.Headers.Add("X-MBX-APIKEY", ApiKey());

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(body);
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("listenKey").GetString() ?? throw new InvalidOperationException("Binance listenKey response is empty.");
    }

    private PeriodicTimer StartKeepAlive(CancellationToken cancellationToken)
    {
        var timer = new PeriodicTimer(TimeSpan.FromMinutes(50));
        _ = Task.Run(async () =>
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await KeepAliveUserDataStreamAsync(cancellationToken);
            }
        }, cancellationToken);
        return timer;
    }

    private async Task KeepAliveUserDataStreamAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(_options.WebSocketApiBaseUrl), cancellationToken);

        var request = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("N"),
            method = "userDataStream.ping",
            @params = new
            {
                apiKey = ApiKey()
            }
        });

        var payload = Encoding.UTF8.GetBytes(request);
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);

        var response = await ReceiveTextAsync(socket, cancellationToken);
        using var document = JsonDocument.Parse(response);
        if (document.RootElement.TryGetProperty("status", out var status) && status.GetInt32() >= 400)
        {
            throw new InvalidOperationException(response);
        }

        logger.LogInformation("Binance user data stream keepalive ping accepted.");
    }

    private static MarginCallEvent ParseMarginCall(JsonElement root)
    {
        var eventTime = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("E").GetInt64());
        var positions = root.GetProperty("p").EnumerateArray()
            .Select(position => new MarginCallPosition(
                Symbol: position.GetProperty("s").GetString() ?? "",
                PositionSide: position.GetProperty("ps").GetString() ?? "",
                PositionAmount: ParseDecimal(position.GetProperty("pa")),
                MarginType: position.GetProperty("mt").GetString() ?? "",
                IsolatedWallet: ParseDecimal(position.GetProperty("iw")),
                MarkPrice: ParseDecimal(position.GetProperty("mp")),
                UnrealizedPnl: ParseDecimal(position.GetProperty("up")),
                MaintenanceMarginRequired: ParseDecimal(position.GetProperty("mm"))))
            .ToList();

        return new MarginCallEvent(
            Symbol: positions.FirstOrDefault()?.Symbol ?? "BTCUSDT",
            CrossWalletBalance: ParseDecimal(root.GetProperty("cw")),
            Positions: positions,
            EventTime: eventTime);
    }

    private UserDataStreamExpiredEvent ParseUserDataStreamExpired(JsonElement root, string listenKey)
    {
        return new UserDataStreamExpiredEvent(
            Symbol: _options.Symbol.ToUpperInvariant(),
            ListenKey: root.TryGetProperty("listenKey", out var payloadListenKey)
                ? payloadListenKey.GetString() ?? listenKey
                : listenKey,
            EventTime: DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("E").GetInt64()));
    }

    private AccountUpdateEvent ParseAccountUpdate(JsonElement root)
    {
        var account = root.GetProperty("a");
        var positions = account.GetProperty("P").EnumerateArray()
            .Select(position => new AccountPositionUpdate(
                Symbol: position.GetProperty("s").GetString() ?? "",
                PositionSide: position.GetProperty("ps").GetString() ?? "",
                PositionAmount: ParseDecimal(position.GetProperty("pa")),
                EntryPrice: ParseDecimal(position.GetProperty("ep")),
                BreakEvenPrice: TryParseDecimal(position, "bep"),
                AccumulatedRealized: ParseDecimal(position.GetProperty("cr")),
                UnrealizedProfit: ParseDecimal(position.GetProperty("up")),
                MarginType: position.GetProperty("mt").GetString() ?? "",
                IsolatedWallet: ParseDecimal(position.GetProperty("iw"))))
            .ToList();

        var balances = account.GetProperty("B").EnumerateArray()
            .Select(balance => new AccountBalanceUpdate(
                Asset: balance.GetProperty("a").GetString() ?? "",
                WalletBalance: ParseDecimal(balance.GetProperty("wb")),
                CrossWalletBalance: ParseDecimal(balance.GetProperty("cw")),
                BalanceChange: TryParseDecimal(balance, "bc")))
            .ToList();

        return new AccountUpdateEvent(
            Symbol: positions.FirstOrDefault()?.Symbol ?? _options.Symbol.ToUpperInvariant(),
            Reason: account.GetProperty("m").GetString() ?? "",
            Balances: balances,
            Positions: positions,
            EventTime: DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("E").GetInt64()),
            TransactionTime: DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("T").GetInt64()));
    }

    private OrderUpdateEvent ParseOrderUpdate(JsonElement root)
    {
        var order = root.GetProperty("o");

        return new OrderUpdateEvent(
            Symbol: order.GetProperty("s").GetString() ?? _options.Symbol.ToUpperInvariant(),
            OrderId: order.GetProperty("i").GetInt64(),
            ClientOrderId: order.GetProperty("c").GetString() ?? "",
            Side: order.GetProperty("S").GetString() ?? "",
            OrderType: order.GetProperty("o").GetString() ?? "",
            ExecutionType: order.GetProperty("x").GetString() ?? "",
            OrderStatus: order.GetProperty("X").GetString() ?? "",
            TimeInForce: order.GetProperty("f").GetString() ?? "",
            OriginalQuantity: ParseDecimal(order.GetProperty("q")),
            OriginalPrice: ParseDecimal(order.GetProperty("p")),
            AveragePrice: TryParseDecimal(order, "ap"),
            StopPrice: ParseDecimal(order.GetProperty("sp")),
            LastFilledQuantity: ParseDecimal(order.GetProperty("l")),
            AccumulatedFilledQuantity: ParseDecimal(order.GetProperty("z")),
            LastFilledPrice: ParseDecimal(order.GetProperty("L")),
            RealizedProfit: ParseDecimal(order.GetProperty("rp")),
            ReduceOnly: order.TryGetProperty("R", out var reduceOnly) && reduceOnly.GetBoolean(),
            PositionSide: order.GetProperty("ps").GetString() ?? "",
            WorkingType: order.TryGetProperty("wt", out var workingType) ? workingType.GetString() ?? "" : "",
            OrderTradeTime: DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("T").GetInt64()),
            EventTime: DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("E").GetInt64()));
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var output = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            output.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static decimal ParseDecimal(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : decimal.Parse(value.GetString() ?? "0", CultureInfo.InvariantCulture);
    }

    private static decimal TryParseDecimal(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) ? ParseDecimal(value) : 0;
    }
}
