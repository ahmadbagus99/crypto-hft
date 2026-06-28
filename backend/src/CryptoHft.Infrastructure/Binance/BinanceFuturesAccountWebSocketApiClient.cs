using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Account;
using CryptoHft.Application.Trading;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Binance;

public sealed class BinanceFuturesAccountWebSocketApiClient(
    IOptions<BinanceOptions> options,
    IRuntimeTradingSettingsService runtimeSettings) : IFuturesAccountClient
{
    private readonly BinanceOptions _options = options.Value;

    public async Task<IReadOnlyList<FuturesWalletBalance>> GetWalletBalancesAsync(CancellationToken cancellationToken)
    {
        if (!HasCredentials()) return PaperWallet();

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
        if (!HasCredentials()) return Array.Empty<FuturesPositionInfo>();

        var parameters = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            parameters["symbol"] = symbol.ToUpperInvariant();
        }

        using var document = await InvokeSignedAsync("v2/account.position", parameters, cancellationToken);
        return document.RootElement.GetProperty("result").EnumerateArray()
            .Select(item => new FuturesPositionInfo(
                Symbol: item.GetProperty("symbol").GetString() ?? "",
                PositionSide: item.GetProperty("positionSide").GetString() ?? "",
                PositionAmount: ParseDecimal(item.GetProperty("positionAmt")),
                EntryPrice: ParseDecimal(item.GetProperty("entryPrice")),
                BreakEvenPrice: TryParseDecimal(item, "breakEvenPrice"),
                MarkPrice: TryParseDecimal(item, "markPrice"),
                UnrealizedProfit: ParseDecimal(item.GetProperty("unRealizedProfit")),
                LiquidationPrice: ParseDecimal(item.GetProperty("liquidationPrice")),
                Leverage: ParseDecimal(item.GetProperty("leverage")),
                MaxNotionalValue: ParseDecimal(item.GetProperty("maxNotionalValue")),
                MarginType: item.GetProperty("marginType").GetString() ?? "",
                IsolatedMargin: ParseDecimal(item.GetProperty("isolatedMargin")),
                IsAutoAddMargin: item.GetProperty("isAutoAddMargin").GetString() == "true",
                UpdateTime: DateTimeOffset.FromUnixTimeMilliseconds(item.GetProperty("updateTime").GetInt64())))
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
