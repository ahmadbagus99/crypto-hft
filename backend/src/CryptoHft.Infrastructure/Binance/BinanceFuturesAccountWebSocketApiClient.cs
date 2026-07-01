using System.Globalization;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Account;
using CryptoHft.Application.MarketData;
using CryptoHft.Application.Trading;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Binance;

public sealed class BinanceFuturesAccountWebSocketApiClient(
    IOptions<BinanceOptions> options,
    IHttpClientFactory httpClientFactory,
    IRuntimeTradingSettingsService runtimeSettings) : IFuturesAccountClient
{
    private readonly BinanceOptions _options = options.Value;

    public async Task<IReadOnlyList<FuturesWalletBalance>> GetWalletBalancesAsync(CancellationToken cancellationToken)
    {
        // In Paper mode the dashboard reflects the simulated account, not the real wallet.
        if (runtimeSettings.GetRuntimeSettings().PaperTradingOnly || !HasCredentials()) return PaperWallet();

        using var document = await InvokeSignedAsync("v2/account.balance", new Dictionary<string, object?>(), cancellationToken);
        return document.RootElement.GetProperty("result").EnumerateArray()
            .Select(item => new FuturesWalletBalance(
                AccountAlias: item.GetProperty("accountAlias").GetString() ?? "",
                Asset: item.GetProperty("asset").GetString() ?? "",
                Balance: ParseDecimal(item.GetProperty("balance")),
                CrossWalletBalance: ParseDecimal(item.GetProperty("crossWalletBalance")),
                CrossUnrealizedPnl: ParseDecimal(item.GetProperty("crossUnPnl")),
                AvailableBalance: ParseDecimal(item.GetProperty("availableBalance")),
                MaxWithdrawAmount: ParseDecimal(item.GetProperty("maxWithdrawAmount")),
                IsMarginAvailable: item.TryGetProperty("marginAvailable", out var marginAvailable) && marginAvailable.GetBoolean(),
                UpdateTime: DateTimeOffset.FromUnixTimeMilliseconds(item.GetProperty("updateTime").GetInt64())))
            .ToList();
    }

    public async Task<IReadOnlyList<FuturesPositionInfo>> GetPositionsAsync(string? symbol, CancellationToken cancellationToken)
    {
        // Always expose exchange positions for monitoring when credentials exist. Paper mode
        // still controls execution elsewhere; it should not hide positions opened manually.
        if (!HasCredentials()) return Array.Empty<FuturesPositionInfo>();

        var parameters = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            parameters["symbol"] = symbol.ToUpperInvariant();
        }

        // Read position risk over REST (/fapi/v2/positionRisk) — the stable, documented endpoint.
        // The response is a bare JSON array. Field reads are defensive so a missing/renamed field
        // never throws a 500 and hides an open position.
        using var document = await InvokeSignedRestAsync(HttpMethod.Get, "/fapi/v2/positionRisk", parameters, cancellationToken);
        return document.RootElement.EnumerateArray()
            .Select(item => new FuturesPositionInfo(
                Symbol: TryGetString(item, "symbol") ?? "",
                PositionSide: TryGetString(item, "positionSide") ?? "",
                PositionAmount: TryParseDecimal(item, "positionAmt"),
                EntryPrice: TryParseDecimal(item, "entryPrice"),
                BreakEvenPrice: TryParseDecimal(item, "breakEvenPrice"),
                MarkPrice: TryParseDecimal(item, "markPrice"),
                UnrealizedProfit: TryParseDecimal(item, "unRealizedProfit"),
                LiquidationPrice: TryParseDecimal(item, "liquidationPrice"),
                Leverage: TryParseDecimal(item, "leverage"),
                MaxNotionalValue: TryParseDecimal(item, "maxNotionalValue"),
                MarginType: TryGetString(item, "marginType") ?? "",
                IsolatedMargin: TryParseDecimal(item, "isolatedMargin"),
                IsAutoAddMargin: string.Equals(TryGetString(item, "isAutoAddMargin"), "true", StringComparison.OrdinalIgnoreCase),
                UpdateTime: DateTimeOffset.FromUnixTimeMilliseconds(TryParseLong(item, "updateTime") ?? 0)))
            .ToList();
    }

    public async Task<IReadOnlyList<OrderUpdateEvent>> GetOrderUpdatesAsync(string? symbol, CancellationToken cancellationToken)
    {
        if (!HasCredentials()) return Array.Empty<OrderUpdateEvent>();

        var effectiveSymbol = string.IsNullOrWhiteSpace(symbol) ? _options.Symbol.ToUpperInvariant() : symbol.ToUpperInvariant();
        var parameters = new Dictionary<string, object?>
        {
            ["symbol"] = effectiveSymbol,
            ["limit"] = 20
        };

        using var document = await InvokeSignedRestAsync(HttpMethod.Get, "/fapi/v1/allOrders", parameters, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return document.RootElement.EnumerateArray()
            .OrderByDescending(item => TryParseLong(item, "updateTime"))
            .Select(item =>
            {
                var orderTime = DateTimeOffset.FromUnixTimeMilliseconds(TryParseLong(item, "updateTime") ?? TryParseLong(item, "time") ?? now.ToUnixTimeMilliseconds());
                return new OrderUpdateEvent(
                    Symbol: item.GetProperty("symbol").GetString() ?? effectiveSymbol,
                    OrderId: item.GetProperty("orderId").GetInt64(),
                    ClientOrderId: item.TryGetProperty("clientOrderId", out var clientOrderId) ? clientOrderId.GetString() ?? "" : "",
                    Side: item.GetProperty("side").GetString() ?? "",
                    OrderType: item.GetProperty("type").GetString() ?? "",
                    ExecutionType: "SNAPSHOT",
                    OrderStatus: item.GetProperty("status").GetString() ?? "",
                    TimeInForce: item.TryGetProperty("timeInForce", out var timeInForce) ? timeInForce.GetString() ?? "" : "",
                    OriginalQuantity: ParseDecimal(item.GetProperty("origQty")),
                    OriginalPrice: ParseDecimal(item.GetProperty("price")),
                    AveragePrice: TryParseDecimal(item, "avgPrice"),
                    StopPrice: TryParseDecimal(item, "stopPrice"),
                    LastFilledQuantity: 0,
                    AccumulatedFilledQuantity: TryParseDecimal(item, "executedQty"),
                    LastFilledPrice: 0,
                    RealizedProfit: 0,
                    ReduceOnly: item.TryGetProperty("reduceOnly", out var reduceOnly) && reduceOnly.GetBoolean(),
                    PositionSide: item.TryGetProperty("positionSide", out var positionSide) ? positionSide.GetString() ?? "" : "",
                    WorkingType: item.TryGetProperty("workingType", out var workingType) ? workingType.GetString() ?? "" : "",
                    OrderTradeTime: orderTime,
                    EventTime: now);
            })
            .ToList();
    }

    private async Task<JsonDocument> InvokeSignedAsync(string method, Dictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        var settings = runtimeSettings.GetRuntimeSettings();
        var apiKey = !string.IsNullOrWhiteSpace(settings.ApiKey) ? settings.ApiKey : _options.ApiKey;
        parameters["apiKey"] = apiKey!;
        parameters["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        parameters["recvWindow"] = 5000;
        parameters["signature"] = Sign(parameters, settings);

        var request = JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid().ToString("N"),
            method,
            @params = parameters
        });

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(_options.WebSocketApiBaseUrl), cancellationToken);
        var payload = Encoding.UTF8.GetBytes(request);
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);

        var response = await ReceiveTextAsync(socket, cancellationToken);
        var document = JsonDocument.Parse(response);
        if (document.RootElement.TryGetProperty("status", out var status) && status.GetInt32() >= 400)
        {
            throw new InvalidOperationException(response);
        }

        return document;
    }

    private async Task<JsonDocument> InvokeSignedRestAsync(
        HttpMethod method,
        string path,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var settings = runtimeSettings.GetRuntimeSettings();
        var apiKey = !string.IsNullOrWhiteSpace(settings.ApiKey) ? settings.ApiKey : _options.ApiKey;
        parameters["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        parameters["recvWindow"] = 5000;

        // Binance validates the signature against the EXACT query string received. Build the signed
        // payload and the sent query from the same ordered, unescaped key=value list so they match —
        // signing a differently-ordered/escaped string yields -1022 "Signature not valid".
        var signedParams = parameters
            .Where(pair => pair.Value is not null && pair.Key != "signature")
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={FormatValue(pair.Value!)}")
            .ToList();
        var payload = string.Join("&", signedParams);
        var secret = !string.IsNullOrWhiteSpace(settings.ApiSecret) ? settings.ApiSecret : _options.ApiSecret;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret!));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var query = $"{payload}&signature={signature}";

        using var request = new HttpRequestMessage(method, $"{_options.RestBaseUrl.TrimEnd('/')}{path}?{query}");
        request.Headers.Add("X-MBX-APIKEY", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(body);
        }

        return JsonDocument.Parse(body);
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

    private string Sign(Dictionary<string, object?> parameters, RuntimeTradingSettings settings)
    {
        var payload = string.Join("&", parameters
            .Where(pair => pair.Value is not null && pair.Key != "signature")
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={FormatValue(pair.Value!)}"));

        var secret = !string.IsNullOrWhiteSpace(settings.ApiSecret) ? settings.ApiSecret : _options.ApiSecret;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret!));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private bool HasCredentials()
    {
        var settings = runtimeSettings.GetRuntimeSettings();
        var key = !string.IsNullOrWhiteSpace(settings.ApiKey) ? settings.ApiKey : _options.ApiKey;
        var secret = !string.IsNullOrWhiteSpace(settings.ApiSecret) ? settings.ApiSecret : _options.ApiSecret;
        return !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(secret);
    }

    private static string FormatValue(object value)
    {
        return value switch
        {
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            double number => number.ToString(CultureInfo.InvariantCulture),
            float number => number.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
    }

    private static decimal TryParseDecimal(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) ? ParseDecimal(value) : 0;
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
    }

    private static long? TryParseLong(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    private static decimal ParseDecimal(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : decimal.Parse(value.GetString() ?? "0", CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<FuturesWalletBalance> PaperWallet()
    {
        return
        [
            new FuturesWalletBalance(
                AccountAlias: "PAPER",
                Asset: "USDT",
                Balance: 100000m,
                CrossWalletBalance: 100000m,
                CrossUnrealizedPnl: 0m,
                AvailableBalance: 100000m,
                MaxWithdrawAmount: 100000m,
                IsMarginAvailable: true,
                UpdateTime: DateTimeOffset.UtcNow)
        ];
    }
}
