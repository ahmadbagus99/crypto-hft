using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Account;
using CryptoHft.Application.Trading;
using CryptoHft.Domain.Entities;
using CryptoHft.Domain.Enums;
using CryptoHft.Infrastructure.Binance;
using CryptoHft.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Trading;

public sealed class BinanceFuturesTradingExecutor(
    TradingDbContext dbContext,
    IRealtimePublisher publisher,
    IExchangeRuleValidator exchangeRuleValidator,
    IRuntimeTradingSettingsService runtimeSettings,
    IFuturesAccountClient accountClient,
    IHttpClientFactory httpClientFactory,
    IOptions<BinanceOptions> options) : ITradingExecutor
{
    private readonly BinanceOptions _options = options.Value;

    public async Task<TradeOrderResult> PlaceAsync(TradeOrderRequest request, CancellationToken cancellationToken)
    {
        var normalized = await exchangeRuleValidator.NormalizeAndValidateAsync(request, cancellationToken);
        request = normalized.Request;

        var settings = runtimeSettings.GetRuntimeSettings();
        if (settings.PaperTradingOnly || !HasCredentials(settings))
        {
            return await PlacePaperAsync(request, cancellationToken);
        }

        // Risk enforcement: block if daily loss or exposure limits breached
        if (!request.ReduceOnly)
        {
            var riskBlock = await CheckRiskLimitsAsync(request, settings, cancellationToken);
            if (riskBlock is not null)
            {
                throw new InvalidOperationException(riskBlock);
            }
        }

        // Set leverage on Binance before placing order
        var leverage = request.Leverage > 0 ? request.Leverage : settings.DefaultLeverage;
        if (!request.ReduceOnly)
            leverage = await ResolveAffordableLeverageAsync(request, leverage, cancellationToken);
        await SetLeverageAsync(request.Symbol, leverage, settings, cancellationToken);

        var parameters = BuildOrderParameters(request);
        using var document = await InvokeSignedAsync("order.place", parameters, cancellationToken);
        var result = ParseOrderResult(request, document.RootElement.GetProperty("result"), isPaper: false);

        await SaveOrderAsync(request, result, cancellationToken);
        await publisher.PublishOrderAsync(result, cancellationToken);

        var protectiveMessage = await PlaceProtectiveOrdersAsync(request, isPaper: false, cancellationToken);
        return result with { Message = AppendMessage(result.Message, protectiveMessage) };
    }

    public Task<TradeOrderResult> ClosePositionAsync(ClosePositionRequest request, CancellationToken cancellationToken)
    {
        var closeSide = request.Side == TradeSide.Long ? TradeSide.Short : TradeSide.Long;
        return PlaceAsync(new TradeOrderRequest(
            request.Symbol,
            closeSide,
            OrderKind.Market,
            request.Quantity ?? 0,
            Price: null,
            StopPrice: null,
            TakeProfit: null,
            StopLoss: null,
            Leverage: 0,
            ReduceOnly: true,
            TradingMode.Manual,
            request.Reason), cancellationToken);
    }

    public async Task<TradeOrderResult> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken)
    {
        var settings = runtimeSettings.GetRuntimeSettings();
        if (settings.PaperTradingOnly || !HasCredentials(settings))
        {
            return new TradeOrderResult(symbol, orderId, OrderStatus.Cancelled, 0, null, true, "Paper order cancelled", DateTimeOffset.UtcNow);
        }

        using var document = await InvokeSignedAsync("order.cancel", new Dictionary<string, object?>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["orderId"] = orderId
        }, cancellationToken);

        var result = ParseOrderResult(
            new TradeOrderRequest(symbol, TradeSide.Long, OrderKind.Market, 0, null, null, null, null, 0, false, TradingMode.Manual, "Order cancelled"),
            document.RootElement.GetProperty("result"),
            isPaper: false);

        await publisher.PublishOrderAsync(result, cancellationToken);
        return result;
    }

    private async Task<string?> CheckRiskLimitsAsync(TradeOrderRequest request, RuntimeTradingSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var wallets = await accountClient.GetWalletBalancesAsync(cancellationToken);
            var usdt = wallets.FirstOrDefault(w => w.Asset == "USDT");
            if (usdt is null) return null;

            var equity = Math.Max(usdt.Balance + usdt.CrossUnrealizedPnl, 1m);
            var maxDailyLoss = equity * settings.MaxDailyLossPercent;
            var maxExposure = equity * settings.MaxExposurePercent;

            // Daily loss: approximate via sum of all unrealized losses
            var unrealizedLoss = usdt.CrossUnrealizedPnl < 0 ? Math.Abs(usdt.CrossUnrealizedPnl) : 0m;
            if (unrealizedLoss >= maxDailyLoss)
                return $"Risk block: daily loss {unrealizedLoss:F2} USDT >= limit {maxDailyLoss:F2} USDT ({settings.MaxDailyLossPercent * 100:F0}% of equity)";

            // Exposure: current open notional
            var positions = await accountClient.GetPositionsAsync(request.Symbol, cancellationToken);
            var openNotional = positions
                .Where(p => Math.Abs(p.PositionAmount) > 0)
                .Sum(p => Math.Abs(p.PositionAmount) * (p.MarkPrice > 0 ? p.MarkPrice : p.EntryPrice));

            if (openNotional >= maxExposure)
                return $"Risk block: open exposure {openNotional:F2} USDT >= limit {maxExposure:F2} USDT ({settings.MaxExposurePercent * 100:F0}% of equity)";
        }
        catch
        {
            // Jika account fetch gagal, biarkan order jalan — jangan block trading karena masalah koneksi sementara
        }

        return null;
    }

    private async Task SetLeverageAsync(string symbol, int leverage, RuntimeTradingSettings settings, CancellationToken cancellationToken)
    {
        var apiKey = settings.ApiKey!;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var queryParams = $"symbol={symbol.ToUpperInvariant()}&leverage={leverage}&timestamp={timestamp}&recvWindow=5000";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(settings.ApiSecret!));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(queryParams))).ToLowerInvariant();

        var url = $"{_options.RestBaseUrl.TrimEnd('/')}/fapi/v1/leverage?{queryParams}&signature={signature}";
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-MBX-APIKEY", apiKey);

        using var response = await client.PostAsync(url, null, cancellationToken);
        // Leverage change failure (e.g. sudah di leverage yang sama) tidak perlu throw — order tetap jalan
    }

    // Target margin per new position (USDT). The exchange forces a minimum order (~0.001 BTC ≈ $60
    // notional), so on a small account we raise leverage until that order's margin lands near this
    // target and fits the available balance. Capped for safety.
    private const decimal TargetMarginUsdt = 3m;
    private const int MaxAffordableLeverage = 20;

    // If the order's margin at the chosen leverage would not fit the wallet, bump leverage up so the
    // required margin ≈ TargetMarginUsdt (and always fits available balance), clamped to the cap.
    // Large accounts where the margin already fits are left untouched.
    private async Task<int> ResolveAffordableLeverageAsync(TradeOrderRequest request, int chosenLeverage, CancellationToken cancellationToken)
    {
        try
        {
            var markPrice = await GetMarkPriceAsync(request.Symbol, cancellationToken);
            if (markPrice <= 0) return chosenLeverage;

            var notional = request.Quantity * markPrice;
            if (notional <= 0) return chosenLeverage;

            var wallets = await accountClient.GetWalletBalancesAsync(cancellationToken);
            var available = wallets.UsdAvailableBalance();

            var marginAtChosen = notional / chosenLeverage;
            if (available > 0 && marginAtChosen <= available)
                return chosenLeverage; // already affordable — don't over-leverage

            // Aim for the target margin, but never leave the order unaffordable (95% of balance as buffer).
            var byTarget = (int)Math.Ceiling(notional / TargetMarginUsdt);
            var byBalance = available > 0 ? (int)Math.Ceiling(notional / (available * 0.95m)) : byTarget;
            var needed = Math.Max(chosenLeverage, Math.Max(byTarget, byBalance));
            return Math.Clamp(needed, 1, MaxAffordableLeverage);
        }
        catch
        {
            return chosenLeverage; // never block the order over a sizing lookup
        }
    }

    // Places an order via REST POST /fapi/v1/order. Signs the EXACT query string that is sent, so
    // the signature always matches (unlike the WS order.place path where JSON re-serialization of
    // decimals differs from the signed form). Used for protective SL/TP orders.
    private async Task<JsonDocument> InvokeSignedRestOrderAsync(Dictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        var settings = runtimeSettings.GetRuntimeSettings();
        parameters["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        parameters["recvWindow"] = 5000;

        var signedParams = parameters
            .Where(pair => pair.Value is not null && pair.Key != "signature")
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={FormatValue(pair.Value!)}")
            .ToList();
        var payload = string.Join("&", signedParams);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(settings.ApiSecret!));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var query = $"{payload}&signature={signature}";

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.RestBaseUrl.TrimEnd('/')}/fapi/v1/order?{query}");
        request.Headers.Add("X-MBX-APIKEY", settings.ApiKey!);
        using var client = httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(body);
        }

        return JsonDocument.Parse(body);
    }

    private async Task<decimal> GetMarkPriceAsync(string symbol, CancellationToken cancellationToken)
    {
        var url = $"{_options.RestBaseUrl.TrimEnd('/')}/fapi/v1/premiumIndex?symbol={symbol.ToUpperInvariant()}";
        using var client = httpClientFactory.CreateClient();
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return 0m;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        return TryParseNullableDecimal(document.RootElement, "markPrice") ?? 0m;
    }

    private Dictionary<string, object?> BuildOrderParameters(TradeOrderRequest request)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["symbol"] = request.Symbol.ToUpperInvariant(),
            ["side"] = ToBinanceSide(request.Side),
            ["type"] = ToBinanceOrderType(request.Kind)
        };

        // Protective SL/TP (*_MARKET) attach to the whole position via closePosition=true. This is
        // the robust way: it does not require the position to already exist at placement time (so it
        // is not rejected with -2022 when sent right after the entry market order), and Binance
        // forbids sending quantity/reduceOnly alongside closePosition.
        var isProtectiveMarket = request.Kind is OrderKind.StopMarket or OrderKind.TakeProfit;
        if (isProtectiveMarket)
        {
            // Boolean params MUST be real JSON booleans, not the string "true": the WS API signature
            // is computed over key=true (unquoted) while a string serializes to "true" (quoted),
            // which makes Binance reject the order with -1022 "Signature not valid".
            parameters["closePosition"] = true;
            parameters["stopPrice"] = request.StopPrice!.Value;
            parameters["workingType"] = "MARK_PRICE";
            return parameters;
        }

        parameters["quantity"] = request.Quantity;

        if (request.ReduceOnly)
        {
            parameters["reduceOnly"] = true;
        }

        if (request.Price is not null)
        {
            parameters["price"] = request.Price.Value;
        }

        if (request.StopPrice is not null)
        {
            parameters["stopPrice"] = request.StopPrice.Value;
        }

        if (request.Kind is OrderKind.Limit or OrderKind.StopLimit)
        {
            parameters["timeInForce"] = "GTC";
        }

        return parameters;
    }

    private static TradeOrderRequest CreateProtectiveOrder(TradeOrderRequest request, OrderKind kind, decimal triggerPrice)
    {
        var closeSide = request.Side == TradeSide.Long ? TradeSide.Short : TradeSide.Long;
        return new TradeOrderRequest(
            request.Symbol,
            closeSide,
            kind,
            request.Quantity,
            Price: null,
            StopPrice: triggerPrice,
            TakeProfit: null,
            StopLoss: null,
            request.Leverage,
            ReduceOnly: true,
            request.Mode,
            kind == OrderKind.TakeProfit ? "Protective take profit" : "Protective stop loss");
    }

    private async Task<TradeOrderResult> PlacePaperAsync(TradeOrderRequest request, CancellationToken cancellationToken)
    {
        var result = new TradeOrderResult(
            request.Symbol,
            $"PAPER-{Guid.NewGuid():N}",
            OrderStatus.Filled,
            request.Quantity,
            request.Price,
            true,
            request.Reason,
            DateTimeOffset.UtcNow);

        await SaveOrderAsync(request, result, cancellationToken);
        await publisher.PublishOrderAsync(result, cancellationToken);

        var protectiveMessage = await PlaceProtectiveOrdersAsync(request, isPaper: true, cancellationToken);
        return result with { Message = AppendMessage(result.Message, protectiveMessage) };
    }

    private async Task<string> PlaceProtectiveOrdersAsync(TradeOrderRequest request, bool isPaper, CancellationToken cancellationToken)
    {
        if (request.ReduceOnly || request.Quantity <= 0 || (request.TakeProfit is null && request.StopLoss is null))
        {
            return string.Empty;
        }

        var messages = new List<string>();
        if (request.TakeProfit is not null)
        {
            try
            {
                var takeProfit = CreateProtectiveOrder(request, OrderKind.TakeProfit, request.TakeProfit.Value);
                var result = isPaper
                    ? await PlacePaperProtectiveOrderAsync(takeProfit, cancellationToken)
                    : await PlaceExchangeOrderAsync(takeProfit, cancellationToken);
                messages.Add($"TP {result.OrderId}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                messages.Add($"TP rejected: {ex.Message}");
            }
        }

        if (request.StopLoss is not null)
        {
            try
            {
                var stopLoss = CreateProtectiveOrder(request, OrderKind.StopMarket, request.StopLoss.Value);
                var result = isPaper
                    ? await PlacePaperProtectiveOrderAsync(stopLoss, cancellationToken)
                    : await PlaceExchangeOrderAsync(stopLoss, cancellationToken);
                messages.Add($"SL {result.OrderId}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                messages.Add($"SL rejected: {ex.Message}");
            }
        }

        return messages.Count == 0 ? string.Empty : $"Protective orders submitted: {string.Join(", ", messages)}";
    }

    private async Task<TradeOrderResult> PlaceExchangeOrderAsync(TradeOrderRequest request, CancellationToken cancellationToken)
    {
        request = (await exchangeRuleValidator.NormalizeAndValidateAsync(request, cancellationToken)).Request;
        var parameters = BuildOrderParameters(request);
        // Protective SL/TP go over REST POST /fapi/v1/order. The REST signature is HMAC over the exact
        // query string sent, so there is no JSON serialization mismatch (the WS order.place path signs
        // decimals/booleans in a form System.Text.Json re-emits differently, which Binance rejects with
        // -1022 for stop orders).
        using var document = await InvokeSignedRestOrderAsync(parameters, cancellationToken);
        var result = ParseOrderResult(request, document.RootElement, isPaper: false);

        await SaveOrderAsync(request, result, cancellationToken);
        await publisher.PublishOrderAsync(result, cancellationToken);
        return result;
    }

    private async Task<TradeOrderResult> PlacePaperProtectiveOrderAsync(TradeOrderRequest request, CancellationToken cancellationToken)
    {
        request = (await exchangeRuleValidator.NormalizeAndValidateAsync(request, cancellationToken)).Request;
        var result = new TradeOrderResult(
            request.Symbol,
            $"PAPER-{request.Kind}-{Guid.NewGuid():N}",
            OrderStatus.New,
            request.Quantity,
            request.StopPrice,
            true,
            request.Reason,
            DateTimeOffset.UtcNow);

        await SaveOrderAsync(request, result, cancellationToken);
        await publisher.PublishOrderAsync(result, cancellationToken);
        return result;
    }

    private async Task SaveOrderAsync(TradeOrderRequest request, TradeOrderResult result, CancellationToken cancellationToken)
    {
        dbContext.Orders.Add(new Order
        {
            Symbol = request.Symbol,
            Side = request.Side,
            Kind = request.Kind,
            Status = result.Status,
            Quantity = request.Quantity,
            Price = result.Price ?? request.Price,
            StopPrice = request.StopPrice,
            TakeProfit = request.TakeProfit,
            StopLoss = request.StopLoss,
            ReduceOnly = request.ReduceOnly,
            IsPaper = result.IsPaper,
            ExchangeOrderId = result.OrderId,
            Reason = request.Reason
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<JsonDocument> InvokeSignedAsync(string method, Dictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        var settings = runtimeSettings.GetRuntimeSettings();
        parameters["apiKey"] = settings.ApiKey!;
        parameters["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        parameters["recvWindow"] = 5000;
        parameters["signature"] = Sign(parameters);

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

    private string Sign(Dictionary<string, object?> parameters)
    {
        var payload = string.Join("&", parameters
            .Where(pair => pair.Value is not null && pair.Key != "signature")
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={FormatValue(pair.Value!)}"));

        var settings = runtimeSettings.GetRuntimeSettings();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(settings.ApiSecret!));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static bool HasCredentials(RuntimeTradingSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.ApiKey) && !string.IsNullOrWhiteSpace(settings.ApiSecret);
    }

    private static TradeOrderResult ParseOrderResult(TradeOrderRequest request, JsonElement result, bool isPaper)
    {
        return new TradeOrderResult(
            Symbol: result.TryGetProperty("symbol", out var symbol) ? symbol.GetString() ?? request.Symbol : request.Symbol,
            OrderId: result.TryGetProperty("orderId", out var orderId) ? FormatJsonValue(orderId) : Guid.NewGuid().ToString("N"),
            Status: result.TryGetProperty("status", out var status) ? ParseStatus(status.GetString()) : OrderStatus.New,
            Quantity: result.TryGetProperty("origQty", out var quantity) ? ParseDecimal(quantity) : request.Quantity,
            Price: TryParseNullableDecimal(result, "avgPrice") ?? TryParseNullableDecimal(result, "price") ?? request.Price,
            IsPaper: isPaper,
            Message: request.Reason,
            Time: DateTimeOffset.UtcNow);
    }

    private static OrderStatus ParseStatus(string? status)
    {
        return status switch
        {
            "NEW" => OrderStatus.New,
            "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
            "FILLED" => OrderStatus.Filled,
            "CANCELED" => OrderStatus.Cancelled,
            "REJECTED" => OrderStatus.Rejected,
            "EXPIRED" => OrderStatus.Expired,
            _ => OrderStatus.New
        };
    }

    private static string ToBinanceSide(TradeSide side) => side == TradeSide.Long ? "BUY" : "SELL";

    private static string ToBinanceOrderType(OrderKind kind)
    {
        return kind switch
        {
            OrderKind.Market => "MARKET",
            OrderKind.Limit => "LIMIT",
            OrderKind.StopMarket => "STOP_MARKET",
            OrderKind.StopLimit => "STOP",
            OrderKind.TakeProfit => "TAKE_PROFIT_MARKET",
            OrderKind.TrailingStop => "TRAILING_STOP_MARKET",
            _ => "MARKET"
        };
    }

    private static string AppendMessage(string message, string addition)
    {
        if (string.IsNullOrWhiteSpace(addition)) return message;
        if (string.IsNullOrWhiteSpace(message)) return addition;
        return $"{message}. {addition}";
    }

    private static string FormatValue(object value)
    {
        return value switch
        {
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            double number => number.ToString(CultureInfo.InvariantCulture),
            float number => number.ToString(CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
    }

    private static string FormatJsonValue(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.GetRawText();
    }

    private static decimal? TryParseNullableDecimal(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) ? ParseDecimal(value) : null;
    }

    private static decimal ParseDecimal(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : decimal.Parse(value.GetString() ?? "0", CultureInfo.InvariantCulture);
    }
}
