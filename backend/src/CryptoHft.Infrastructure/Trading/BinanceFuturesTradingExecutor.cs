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
using Microsoft.EntityFrameworkCore;
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

        // Set leverage on Binance before placing an ENTRY order only. Reduce-only orders
        // (closes) must never touch the symbol leverage: the open position already carries
        // one, and resetting it mid-close to DefaultLeverage serves nothing.
        if (!request.ReduceOnly)
        {
            var leverage = request.Leverage > 0 ? request.Leverage : settings.DefaultLeverage;
            leverage = await ResolveAffordableLeverageAsync(request, leverage, cancellationToken);
            await SetLeverageAsync(request.Symbol, leverage, settings, cancellationToken);
        }

        var parameters = BuildOrderParameters(request);
        using var document = await InvokeSignedAsync("order.place", parameters, cancellationToken);
        var result = ParseOrderResult(request, document.RootElement.GetProperty("result"), isPaper: false);

        await SaveOrderAsync(request, result, cancellationToken);
        await publisher.PublishOrderAsync(result, cancellationToken);

        var protectiveMessage = await PlaceProtectiveOrdersAsync(request, isPaper: false, cancellationToken);
        return result with { Message = AppendMessage(result.Message, protectiveMessage) };
    }

    public async Task<TradeOrderResult> ClosePositionAsync(ClosePositionRequest request, CancellationToken cancellationToken)
    {
        var closeSide = request.Side == TradeSide.Long ? TradeSide.Short : TradeSide.Long;
        var result = await PlaceAsync(new TradeOrderRequest(
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

        var cleanupMessage = await CancelOutstandingProtectiveOrdersAsync(request.Symbol, cancellationToken);
        return result with { Message = AppendMessage(result.Message, cleanupMessage) };
    }

    // Trailing/breakeven ratchet: replace the outstanding protective stop with a tighter one.
    // Binance rejects two closePosition stop orders with the same trigger direction (-4130),
    // so live amendments cancel the old SL first, place the new one, and restore the old SL if
    // the replacement fails. Paper mode follows the same single-active-stop lifecycle.
    public async Task<TradeOrderResult> AmendStopLossAsync(AmendStopLossRequest request, CancellationToken cancellationToken)
    {
        var closeSide = request.PositionSide == TradeSide.Long ? TradeSide.Short : TradeSide.Long;
        var normalizedSymbol = request.Symbol.ToUpperInvariant();

        var previous = await dbContext.Orders
            .Where(o => o.Symbol == normalizedSymbol
                        && o.ReduceOnly
                        && o.Side == closeSide
                        && o.Kind == OrderKind.StopMarket
                        && o.Status == OrderStatus.New
                        && o.ExchangeOrderId != null)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var stopOrder = new TradeOrderRequest(
            request.Symbol,
            closeSide,
            OrderKind.StopMarket,
            request.Quantity,
            Price: null,
            StopPrice: request.NewStopPrice,
            TakeProfit: null,
            StopLoss: null,
            Leverage: 0,
            ReduceOnly: true,
            TradingMode.Auto,
            request.Reason);

        var settings = runtimeSettings.GetRuntimeSettings();
        var paper = settings.PaperTradingOnly || !HasCredentials(settings) || (previous?.IsPaper ?? false);

        if (previous is not null)
        {
            try
            {
                if (!paper)
                    await CancelAlgoOrderAsync(request.Symbol, previous.ExchangeOrderId!, cancellationToken);
                previous.Status = OrderStatus.Cancelled;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Trailing stop amend aborted: old SL {previous.ExchangeOrderId} cancel failed; existing protection remains active. {ex.Message}",
                    ex);
            }
        }

        try
        {
            var result = paper
                ? await PlacePaperProtectiveOrderAsync(stopOrder, cancellationToken)
                : await PlaceExchangeOrderAsync(stopOrder, cancellationToken);

            return result with { Message = AppendMessage(result.Message, previous is null ? "no previous SL found" : $"old SL {previous.ExchangeOrderId} cancelled first") };
        }
        catch (Exception placeEx) when (placeEx is not OperationCanceledException)
        {
            if (previous is null || previous.StopPrice is null)
                throw new InvalidOperationException(
                    $"Trailing stop amend failed after no previous SL was found: {placeEx.Message}",
                    placeEx);

            var restoreOrder = new TradeOrderRequest(
                request.Symbol,
                closeSide,
                OrderKind.StopMarket,
                previous.Quantity,
                Price: null,
                StopPrice: previous.StopPrice,
                TakeProfit: null,
                StopLoss: null,
                Leverage: 0,
                ReduceOnly: true,
                TradingMode.Auto,
                $"Restore previous trailing SL after amend failure: {placeEx.Message}");

            TradeOrderResult restoreResult;
            try
            {
                restoreResult = paper
                    ? await PlacePaperProtectiveOrderAsync(restoreOrder, cancellationToken)
                    : await PlaceExchangeOrderAsync(restoreOrder, cancellationToken);
            }
            catch (Exception restoreEx) when (restoreEx is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"CRITICAL: trailing stop amend failed after old SL {previous.ExchangeOrderId} was cancelled, and restore at {previous.StopPrice} also failed. New SL target {request.NewStopPrice}. Amend failure: {placeEx.Message}. Restore failure: {restoreEx.Message}",
                    restoreEx);
            }

            throw new InvalidOperationException(
                $"Trailing stop amend failed after old SL {previous.ExchangeOrderId} was cancelled; restored SL {restoreResult.OrderId} at {previous.StopPrice}. Original failure: {placeEx.Message}",
                placeEx);
        }
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
        // Owner switch: the account-level guard (loss/exposure blocks) can be disabled.
        if (!settings.AccountRiskGuardEnabled) return null;

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

    // The exchange forces a minimum order (~0.001 BTC ≈ $60 notional), so on a small account we
    // raise leverage until that order's margin lands near the configured target
    // (TradingSettings.TargetMarginUsdt) and fits the available balance. Capped for safety.
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

            // Aim for the configured target margin, but never leave the order unaffordable
            // (95% of balance as buffer).
            var targetMargin = runtimeSettings.GetRuntimeSettings().TargetMarginUsdt;
            if (targetMargin <= 0) targetMargin = 3m;
            var byTarget = (int)Math.Ceiling(notional / targetMargin);
            var byBalance = available > 0 ? (int)Math.Ceiling(notional / (available * 0.95m)) : byTarget;
            var needed = Math.Max(chosenLeverage, Math.Max(byTarget, byBalance));
            return Math.Clamp(needed, 1, MaxAffordableLeverage);
        }
        catch
        {
            return chosenLeverage; // never block the order over a sizing lookup
        }
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
        parameters["quantity"] = request.Quantity;

        if (request.ReduceOnly)
        {
            parameters["reduceOnly"] = true;
        }

        if (request.Price is not null)
        {
            parameters["price"] = PriceParam(request.Price.Value);
        }

        if (request.StopPrice is not null)
        {
            parameters["stopPrice"] = PriceParam(request.StopPrice.Value);
        }

        if (request.Kind is OrderKind.Limit or OrderKind.StopLimit)
        {
            // A non-reduce-only limit is an ENTRY posted for the maker fee, so it goes out
            // post-only: Binance rejects it outright rather than letting it cross and charge
            // taker. A rejection costs nothing — the signal simply is not taken at that price,
            // which beats silently paying the fee the mode exists to avoid. Reduce-only limits
            // (protective exits) stay GTC, because an exit must be allowed to fill.
            parameters["timeInForce"] = request.ReduceOnly ? "GTC" : "GTX";
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

    private async Task<string> CancelOutstandingProtectiveOrdersAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalizedSymbol = symbol.ToUpperInvariant();
        var protectiveOrders = await dbContext.Orders
            .Where(o => o.Symbol == normalizedSymbol
                        && o.ReduceOnly
                        && (o.Kind == OrderKind.TakeProfit || o.Kind == OrderKind.StopMarket)
                        && o.Status == OrderStatus.New
                        && o.ExchangeOrderId != null)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

        if (protectiveOrders.Count == 0)
            return string.Empty;

        var settings = runtimeSettings.GetRuntimeSettings();
        var messages = new List<string>();
        foreach (var order in protectiveOrders)
        {
            try
            {
                TradeOrderResult result;
                if (order.IsPaper || settings.PaperTradingOnly || !HasCredentials(settings))
                {
                    result = new TradeOrderResult(
                        symbol,
                        order.ExchangeOrderId!,
                        OrderStatus.Cancelled,
                        order.Quantity,
                        order.StopPrice,
                        true,
                        "Protective order cancelled after position close",
                        DateTimeOffset.UtcNow);
                }
                else
                {
                    result = await CancelAlgoOrderAsync(symbol, order.ExchangeOrderId!, cancellationToken);
                }

                order.Status = OrderStatus.Cancelled;
                await publisher.PublishOrderAsync(result, cancellationToken);
                messages.Add($"{order.Kind} {order.ExchangeOrderId} cancelled");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                messages.Add($"{order.Kind} {order.ExchangeOrderId} cancel failed: {ex.Message}");
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return messages.Count == 0 ? string.Empty : $"Protective cleanup: {string.Join(", ", messages)}";
    }

    // Places a protective SL/TP order. Stop-type orders are rejected by the plain order.place endpoint
    // (-4120) on this account, so they go through the Algo Order API: method "algoOrder.place" with
    // algoType=CONDITIONAL and triggerPrice (NOT stopPrice). triggerPrice is a string so Binance uses
    // it verbatim for signature verification (a JSON number drops tick trailing zeros -> -1022).
    private async Task<TradeOrderResult> PlaceExchangeOrderAsync(TradeOrderRequest request, CancellationToken cancellationToken)
    {
        request = (await exchangeRuleValidator.NormalizeAndValidateAsync(request, cancellationToken)).Request;
        var parameters = new Dictionary<string, object?>
        {
            ["algoType"] = "CONDITIONAL",
            ["symbol"] = request.Symbol.ToUpperInvariant(),
            ["side"] = ToBinanceSide(request.Side),
            ["type"] = ToBinanceOrderType(request.Kind),
            ["closePosition"] = "true",
            ["triggerPrice"] = PriceParam(request.StopPrice!.Value),
            ["workingType"] = "MARK_PRICE"
        };
        using var document = await InvokeSignedAsync("algoOrder.place", parameters, cancellationToken);
        var result = ParseAlgoOrderResult(request, document.RootElement.GetProperty("result"));

        await SaveOrderAsync(request, result, cancellationToken);
        await publisher.PublishOrderAsync(result, cancellationToken);
        return result;
    }

    private async Task<TradeOrderResult> CancelAlgoOrderAsync(
        string symbol,
        string algoId,
        CancellationToken cancellationToken)
    {
        using var document = await InvokeSignedAsync("algoOrder.cancel", new Dictionary<string, object?>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["algoId"] = algoId
        }, cancellationToken);

        var result = document.RootElement.GetProperty("result");
        return new TradeOrderResult(
            Symbol: result.TryGetProperty("symbol", out var resultSymbol) ? resultSymbol.GetString() ?? symbol : symbol,
            OrderId: result.TryGetProperty("algoId", out var resultAlgoId) ? FormatJsonValue(resultAlgoId) : algoId,
            Status: result.TryGetProperty("algoStatus", out var status) ? ParseStatus(status.GetString()) : OrderStatus.Cancelled,
            Quantity: 0m,
            Price: TryParseNullableDecimal(result, "triggerPrice"),
            IsPaper: false,
            Message: "Protective algo order cancelled after position close",
            Time: DateTimeOffset.UtcNow);
    }

    private static TradeOrderResult ParseAlgoOrderResult(TradeOrderRequest request, JsonElement result)
    {
        return new TradeOrderResult(
            Symbol: result.TryGetProperty("symbol", out var symbol) ? symbol.GetString() ?? request.Symbol : request.Symbol,
            OrderId: result.TryGetProperty("algoId", out var algoId) ? FormatJsonValue(algoId) : Guid.NewGuid().ToString("N"),
            Status: result.TryGetProperty("algoStatus", out var status) ? ParseStatus(status.GetString()) : OrderStatus.New,
            Quantity: request.Quantity,
            Price: TryParseNullableDecimal(result, "triggerPrice") ?? request.StopPrice,
            IsPaper: false,
            Message: request.Reason,
            Time: DateTimeOffset.UtcNow);
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

    // Prices are sent as strings so Binance uses them verbatim for signature verification instead of
    // re-serializing the JSON number (which drops tick trailing zeros and breaks the signature, -1022).
    private static string PriceParam(decimal value) => value.ToString(CultureInfo.InvariantCulture);

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
