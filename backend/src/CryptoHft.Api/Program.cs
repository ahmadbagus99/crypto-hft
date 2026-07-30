using CryptoHft.Api.BackgroundServices;
using CryptoHft.Api.Hubs;
using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Account;
using CryptoHft.Application.Ai;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Risk;
using CryptoHft.Application.Notifications;
using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;
using CryptoHft.Infrastructure;
using CryptoHft.Infrastructure.Binance;
using CryptoHft.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using System.Globalization;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
{
    // Console writes go through a bounded async buffer that DROPS entries when full
    // instead of blocking. A synchronous console sink can stall its writer when the
    // container's stdout reader slows down, and every thread that logs would queue
    // behind it — including the trading and learning loops, which log each tick.
    // Losing log lines under backpressure is acceptable; stalling a loop is not.
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Async(sink => sink.Console(), blockWhenFull: false);
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .SetIsOriginAllowed(_ => true));
});

builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IRealtimePublisher, SignalRRealtimePublisher>();
builder.Services.AddHostedService<BinanceMarketDataWorker>();
builder.Services.AddHostedService<BinanceUserDataWorker>();
builder.Services.AddHostedService<KillSwitchHeartbeatWorker>();
builder.Services.AddHostedService<AutoTradingWorker>();
builder.Services.AddHostedService<AiLearningWorker>();

var app = builder.Build();

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();
app.MapHub<TradingHub>("/hubs/trading");

app.MapPost("/api/notifications/devices", async (
    RegisterPushDeviceRequest request,
    IPushNotificationService pushNotifications,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await pushNotifications.RegisterDeviceAsync(request, cancellationToken);
        return Results.Ok(result);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapDelete("/api/notifications/devices/{deviceToken}", async (
    string deviceToken,
    IPushNotificationService pushNotifications,
    CancellationToken cancellationToken) =>
{
    try
    {
        await pushNotifications.UnregisterDeviceAsync(deviceToken, cancellationToken);
        return Results.NoContent();
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "crypto-hft-api",
    time = DateTimeOffset.UtcNow
}));

app.MapGet("/api/overview", async (
    IFuturesAccountClient accountClient,
    IRuntimeTradingSettingsService settingsService,
    CancellationToken cancellationToken) =>
{
    var settings = settingsService.GetRuntimeSettings();
    var mode = settings.PaperTradingOnly ? "Mainnet Data / Paper Trading" : "Live Trading";

    decimal walletBalance = 100000m;
    decimal availableBalance = 100000m;
    try
    {
        var wallets = await accountClient.GetWalletBalancesAsync(cancellationToken);
        var equity = wallets.UsdEquity();
        if (equity > 0)
        {
            walletBalance = equity;
            availableBalance = wallets.UsdAvailableBalance();
        }
    }
    catch
    {
        // Fall back to the paper defaults if the private balance request fails.
    }

    return Results.Ok(new
    {
        symbol = "BTCUSDT",
        mode,
        walletBalance,
        availableBalance,
        dailyPnl = 0m,
        weeklyPnl = 0m,
        monthlyPnl = 0m,
        winRate = 0m,
        profitFactor = 0m,
        sharpeRatio = 0m,
        maxDrawdown = 0m,
        openPositions = Array.Empty<object>()
    });
});

app.MapGet("/api/settings/trading", (IRuntimeTradingSettingsService settingsService) =>
    Results.Ok(settingsService.GetPublicSettings()));

app.MapPut("/api/settings/trading", (
    UpdateTradingSettingsRequest request,
    IRuntimeTradingSettingsService settingsService) =>
{
    var settings = settingsService.Update(request);
    return Results.Ok(settings);
});

app.MapPost("/api/settings/test/binance", async (
    IConnectionTester tester, CancellationToken cancellationToken) =>
    Results.Ok(await tester.TestBinanceAsync(cancellationToken)));

app.MapPost("/api/settings/test/anthropic", async (
    IConnectionTester tester, CancellationToken cancellationToken) =>
    Results.Ok(await tester.TestAnthropicAsync(cancellationToken)));

app.MapGet("/api/ai/performance", async (
    IAdaptiveWeightService adaptive, CancellationToken cancellationToken) =>
    Results.Ok(await adaptive.GetPerformanceAsync(cancellationToken)));

// Realized trade outcomes grouped by Claude's verdict (confirmed / hesitant / no-validation) —
// answers empirically whether Claude's hesitancy and defensive sizing predict results.
app.MapGet("/api/ai/validation-performance", async (
    IAdaptiveWeightService adaptive, CancellationToken cancellationToken) =>
    Results.Ok(await adaptive.GetValidationPerformanceAsync(cancellationToken)));

// Confidence calibration curve: realized winrate per 5-point confidence bucket. Answers
// whether "confidence 70" actually wins ~70% of the time — the empirical basis for tuning
// the confidence threshold per regime once enough samples accumulate.
app.MapGet("/api/ai/confidence-calibration", async (
    IAdaptiveWeightService adaptive, CancellationToken cancellationToken) =>
    Results.Ok(await adaptive.GetConfidenceCalibrationAsync(cancellationToken)));

// Learned execution baselines per regime (SL/TP ATR multipliers + leverage factor) with the
// realized exit counters that produced them. Empty until the first realized trades close.
app.MapGet("/api/ai/execution-tuning", async (
    TradingDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await db.ExecutionStats.AsNoTracking()
        .OrderBy(s => s.Regime)
        .ToListAsync(cancellationToken)));

app.MapGet("/api/market/klines", async (
    string symbol,
    string interval,
    int limit,
    IHttpClientFactory httpClientFactory,
    IOptions<BinanceOptions> binanceOptions,
    CancellationToken cancellationToken) =>
{
    limit = Math.Clamp(limit, 1, 1500);
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();
    interval = string.IsNullOrWhiteSpace(interval) ? "1d" : interval;

    var baseUrl = binanceOptions.Value.RestBaseUrl.TrimEnd('/');
    var url = $"{baseUrl}/fapi/v1/klines?symbol={symbol}&interval={interval}&limit={limit}";
    using var response = await httpClientFactory.CreateClient().GetAsync(url, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return Results.Problem(body, statusCode: (int)response.StatusCode);
    }

    using var document = JsonDocument.Parse(body);
    var klines = document.RootElement.EnumerateArray().Select(item => new MarketKlineDto(
        Symbol: symbol,
        Interval: interval,
        OpenTime: DateTimeOffset.FromUnixTimeMilliseconds(item[0].GetInt64()),
        CloseTime: DateTimeOffset.FromUnixTimeMilliseconds(item[6].GetInt64()),
        Open: ProgramHelpers.ParseDecimal(item[1]),
        High: ProgramHelpers.ParseDecimal(item[2]),
        Low: ProgramHelpers.ParseDecimal(item[3]),
        Close: ProgramHelpers.ParseDecimal(item[4]),
        Volume: ProgramHelpers.ParseDecimal(item[5]),
        QuoteVolume: ProgramHelpers.ParseDecimal(item[7]),
        NumberOfTrades: item[8].GetInt64(),
        TakerBuyBaseVolume: ProgramHelpers.ParseDecimal(item[9]),
        TakerBuyQuoteVolume: ProgramHelpers.ParseDecimal(item[10]),
        IsClosed: true,
        EventTime: DateTimeOffset.UtcNow)).ToList();

    return Results.Ok(klines);
});

app.MapGet("/api/market/agg-trades", async (
    string symbol,
    int limit,
    IHttpClientFactory httpClientFactory,
    IOptions<BinanceOptions> binanceOptions,
    CancellationToken cancellationToken) =>
{
    limit = Math.Clamp(limit, 1, 1000);
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();

    var baseUrl = binanceOptions.Value.RestBaseUrl.TrimEnd('/');
    var url = $"{baseUrl}/fapi/v1/aggTrades?symbol={symbol}&limit={limit}";
    using var response = await httpClientFactory.CreateClient().GetAsync(url, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return Results.Problem(body, statusCode: (int)response.StatusCode);
    }

    using var document = JsonDocument.Parse(body);
    var trades = document.RootElement.EnumerateArray()
        .OrderByDescending(item => item.GetProperty("T").GetInt64())
        .Select(item => new MarketAggTradeDto(
            Symbol: symbol,
            Price: ProgramHelpers.ParseDecimal(item.GetProperty("p")),
            Quantity: ProgramHelpers.ParseDecimal(item.GetProperty("q")),
            BuyerIsMaker: item.GetProperty("m").GetBoolean(),
            Time: DateTimeOffset.FromUnixTimeMilliseconds(item.GetProperty("T").GetInt64())))
        .ToList();

    return Results.Ok(trades);
});

app.MapGet("/api/market/mark-price", async (
    string symbol,
    IHttpClientFactory httpClientFactory,
    IOptions<BinanceOptions> binanceOptions,
    CancellationToken cancellationToken) =>
{
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();

    var baseUrl = binanceOptions.Value.RestBaseUrl.TrimEnd('/');
    var url = $"{baseUrl}/fapi/v1/premiumIndex?symbol={symbol}";
    using var response = await httpClientFactory.CreateClient().GetAsync(url, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return Results.Problem(body, statusCode: (int)response.StatusCode);
    }

    using var document = JsonDocument.Parse(body);
    var root = document.RootElement;
    var markPrice = ProgramHelpers.ParseDecimal(root.GetProperty("markPrice"));
    var tick = new MarketMarkPriceDto(
        Symbol: root.GetProperty("symbol").GetString() ?? symbol,
        MarkPrice: markPrice,
        MarkPriceMovingAverage: markPrice,
        IndexPrice: ProgramHelpers.ParseDecimal(root.GetProperty("indexPrice")),
        EstimatedSettlePrice: ProgramHelpers.TryParseDecimal(root, "estimatedSettlePrice"),
        FundingRate: ProgramHelpers.ParseDecimal(root.GetProperty("lastFundingRate")),
        NextFundingTime: DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("nextFundingTime").GetInt64()),
        Time: DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("time").GetInt64()));

    return Results.Ok(tick);
});

app.MapGet("/api/account/wallet", async (
    IFuturesAccountClient accountClient,
    CancellationToken cancellationToken) =>
{
    try
    {
        var balances = await accountClient.GetWalletBalancesAsync(cancellationToken);
        return Results.Ok(balances);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            title: "Binance private account request rejected",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/account/positions", async (
    string? symbol,
    IFuturesAccountClient accountClient,
    CancellationToken cancellationToken) =>
{
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();
    try
    {
        var positions = await accountClient.GetPositionsAsync(symbol, cancellationToken);
        return Results.Ok(positions);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            title: "Binance private position request rejected",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/account/position-revalidations", (
    string? symbol,
    IOpenPositionRevalidationStore revalidationStore) =>
{
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();
    return Results.Ok(revalidationStore.Get(symbol));
});

// Trailing-stop ratchet history for the CURRENT open position (cleared on close).
app.MapGet("/api/account/trailing-stops", (
    string? symbol,
    ITrailingStopActivityStore trailingStore) =>
{
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();
    return Results.Ok(trailingStore.Get(symbol));
});

app.MapGet("/api/account/order-updates", async (
    string? symbol,
    IFuturesAccountClient accountClient,
    CancellationToken cancellationToken) =>
{
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();
    try
    {
        var orders = await accountClient.GetOrderUpdatesAsync(symbol, cancellationToken);
        return Results.Ok(orders);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            title: "Binance private order updates request rejected",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/exchange/rules", async (
    string? symbol,
    IFuturesExchangeInfoClient exchangeInfoClient,
    CancellationToken cancellationToken) =>
{
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();
    try
    {
        var rules = await exchangeInfoClient.GetSymbolRulesAsync(symbol, cancellationToken);
        return Results.Ok(rules);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            title: "Binance exchangeInfo request rejected",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/kill-switch", (IKillSwitchService killSwitchService) =>
    Results.Ok(killSwitchService.GetState()));

app.MapPost("/api/kill-switch/enable", async (
    KillSwitchDto request,
    IKillSwitchService killSwitchService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var state = await killSwitchService.EnableAsync(
            new KillSwitchRequest(
                string.IsNullOrWhiteSpace(request.Symbol) ? "BTCUSDT" : request.Symbol,
                request.CountdownTimeMs <= 0 ? 120_000 : request.CountdownTimeMs,
                request.HeartbeatIntervalMs <= 0 ? 30_000 : request.HeartbeatIntervalMs),
            cancellationToken);

        return Results.Ok(state);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            title: "Binance futures kill switch request rejected",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/kill-switch/disable", async (
    IKillSwitchService killSwitchService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var state = await killSwitchService.DisableAsync(cancellationToken);
        return Results.Ok(state);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            title: "Binance futures kill switch disable rejected",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/manual/order", async (
    ManualOrderDto request,
    ITradingExecutor executor,
    IRuntimeTradingSettingsService settingsService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var settings = settingsService.GetRuntimeSettings();
        var order = new TradeOrderRequest(
            "BTCUSDT",
            request.Side,
            request.Kind,
            request.Quantity,
            request.Price,
            request.StopPrice,
            request.TakeProfit,
            request.StopLoss,
            request.Leverage <= 0 ? settings.DefaultLeverage : request.Leverage,
            request.ReduceOnly,
            TradingMode.Manual,
            request.Reason ?? "Manual order");

        var result = await executor.PlaceAsync(order, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            title: "Binance new order request rejected",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/manual/close", async (
    ClosePositionDto request,
    ITradingExecutor executor,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await executor.ClosePositionAsync(
            new ClosePositionRequest("BTCUSDT", request.Side, request.Quantity, request.Reason ?? "Manual close"),
            cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            title: "Binance close position request rejected",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/journal/orders", async (
    string? symbol,
    int? limit,
    TradingDbContext db,
    CancellationToken cancellationToken) =>
{
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();
    var take = Math.Clamp(limit ?? 50, 1, 200);
    var orders = await db.Orders
        .AsNoTracking()
        .Where(order => order.Symbol == symbol)
        .OrderByDescending(order => order.CreatedAt)
        .Take(take)
        .Select(order => new JournalOrderDto(
            order.Id,
            order.Symbol,
            order.Side.ToString(),
            order.Kind.ToString(),
            order.Status.ToString(),
            order.Quantity,
            order.Price,
            order.StopPrice,
            order.TakeProfit,
            order.StopLoss,
            order.ReduceOnly,
            order.IsPaper,
            order.ExchangeOrderId,
            order.Reason,
            order.CreatedAt))
        .ToListAsync(cancellationToken);

    var summary = new JournalSummaryDto(
        TotalOrders: orders.Count,
        FilledOrders: orders.Count(order => order.Status == OrderStatus.Filled.ToString()),
        RejectedOrders: orders.Count(order => order.Status == OrderStatus.Rejected.ToString()),
        PaperOrders: orders.Count(order => order.IsPaper),
        ReduceOnlyOrders: orders.Count(order => order.ReduceOnly));

    return Results.Ok(new JournalResponseDto(summary, orders));
});

app.MapGet("/api/positions/history", async (
    string? symbol,
    int? limit,
    string? period,
    TradingDbContext db,
    CancellationToken cancellationToken) =>
{
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();
    var take = Math.Clamp(limit ?? 100, 1, 500);
    var query = db.Positions
        .AsNoTracking()
        .Where(position => position.Symbol == symbol && position.ClosedAt != null);

    var now = DateTimeOffset.UtcNow;
    var normalizedPeriod = string.IsNullOrWhiteSpace(period) ? "week" : period.Trim().ToLowerInvariant();
    var closedFrom = normalizedPeriod switch
    {
        "day" => new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero),
        "month" => new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero),
        "year" => new DateTimeOffset(now.UtcDateTime.Year, 1, 1, 0, 0, 0, TimeSpan.Zero),
        "week" => StartOfUtcWeek(now),
        "all" => null,
        _ => (DateTimeOffset?)null
    };
    if (closedFrom is not null)
        query = query.Where(position => position.ClosedAt!.Value >= closedFrom.Value);

    var positions = await query
        .OrderByDescending(position => position.ClosedAt)
        .Take(take)
        .ToListAsync(cancellationToken);

    static decimal Margin(decimal quantity, decimal entryPrice, int leverage)
        => leverage > 0 ? Math.Abs(quantity) * entryPrice / leverage : 0m;

    static DateTimeOffset StartOfUtcWeek(DateTimeOffset value)
    {
        var date = value.UtcDateTime.Date;
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return new DateTimeOffset(date.AddDays(-daysSinceMonday), TimeSpan.Zero);
    }

    var items = positions
        .Select(position => new PositionHistoryItemDto(
            position.Id,
            position.Symbol,
            position.Side.ToString(),
            position.Quantity,
            position.EntryPrice,
            Margin(position.Quantity, position.EntryPrice, position.Leverage),
            position.Leverage,
            position.TakeProfit,
            position.StopLoss,
            position.CloseReason.ToString(),
            position.RealizedPnl,
            position.Roi,
            position.OpenedAt,
            position.ClosedAt!.Value))
        .ToList();

    var summary = new PositionHistorySummaryDto(
        TotalRealizedPnl: positions.Sum(position => position.RealizedPnl),
        TotalTrades: positions.Count,
        WinRate: positions.Count == 0 ? 0m : positions.Count(position => position.RealizedPnl > 0) / (decimal)positions.Count,
        BestTrade: positions.Count == 0 ? 0m : positions.Max(position => position.RealizedPnl),
        WorstTrade: positions.Count == 0 ? 0m : positions.Min(position => position.RealizedPnl));

    var daily = positions
        .GroupBy(position => position.ClosedAt!.Value.UtcDateTime.Date)
        .OrderBy(group => group.Key)
        .TakeLast(30)
        .Select(group => new PositionPnlBucketDto(
            group.Key.ToString("MMM dd", CultureInfo.InvariantCulture),
            new DateTimeOffset(group.Key, TimeSpan.Zero),
            group.Sum(position => position.RealizedPnl),
            group.Count()))
        .ToList();

    var monthly = positions
        .GroupBy(position =>
        {
            var closed = position.ClosedAt!.Value.UtcDateTime;
            return new DateTime(closed.Year, closed.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        })
        .OrderBy(group => group.Key)
        .TakeLast(12)
        .Select(group => new PositionPnlBucketDto(
            group.Key.ToString("MMM yyyy", CultureInfo.InvariantCulture),
            new DateTimeOffset(group.Key, TimeSpan.Zero),
            group.Sum(position => position.RealizedPnl),
            group.Count()))
        .ToList();

    return Results.Ok(new PositionHistoryResponseDto(summary, daily, monthly, items));
});

app.MapGet("/api/risk/positions", async (
    string? symbol,
    IFuturesAccountClient accountClient,
    IRuntimeTradingSettingsService settingsService,
    CancellationToken cancellationToken) =>
{
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();
    var settings = settingsService.GetRuntimeSettings();
    var maxDailyLossPercent = settings.MaxDailyLossPercent;
    var defaultLeverage = (decimal)settings.DefaultLeverage;

    try
    {
        var positions = await accountClient.GetPositionsAsync(symbol, cancellationToken);
        var wallets = await accountClient.GetWalletBalancesAsync(cancellationToken);
        var usdEquity = wallets.UsdEquity();
        var equity = Math.Max(usdEquity > 0 ? usdEquity : 100000m, 1m);
        var maxDailyLoss = equity * maxDailyLossPercent;

        var active = positions
            .Where(position => Math.Abs(position.PositionAmount) > 0)
            .Select(position =>
            {
                var mark = position.MarkPrice > 0 ? position.MarkPrice : position.EntryPrice;
                var notional = Math.Abs(position.PositionAmount) * mark;
                var leverage = position.Leverage > 0 ? position.Leverage : defaultLeverage;
                var initialMargin = leverage > 0 ? notional / leverage : 0m;
                var marginRatio = initialMargin > 0 ? Math.Max(0m, -position.UnrealizedProfit) / initialMargin : 0m;
                var liquidationBuffer = position.LiquidationPrice > 0 && mark > 0
                    ? Math.Abs(mark - position.LiquidationPrice) / mark
                    : 1m;
                var pnlPercent = initialMargin > 0 ? position.UnrealizedProfit / initialMargin : 0m;
                var riskLevel = marginRatio >= 0.80m || liquidationBuffer <= 0.03m
                    ? "critical"
                    : marginRatio >= 0.50m || liquidationBuffer <= 0.08m
                        ? "warning"
                        : "normal";

                return new PositionRiskDto(
                    position.Symbol,
                    position.PositionSide,
                    position.MarginType,
                    position.PositionAmount,
                    position.EntryPrice,
                    mark,
                    position.LiquidationPrice,
                    notional,
                    initialMargin,
                    position.UnrealizedProfit,
                    pnlPercent,
                    leverage,
                    marginRatio,
                    liquidationBuffer,
                    riskLevel);
            })
            .ToList();

        var totalNotional = active.Sum(position => position.Notional);
        var totalUnrealized = active.Sum(position => position.UnrealizedProfit);
        var exposureRatio = totalNotional / equity;
        var dailyLossUsed = totalUnrealized < 0 ? Math.Abs(totalUnrealized) / maxDailyLoss : 0m;
        var portfolioRisk = active.Any(position => position.RiskLevel == "critical") || dailyLossUsed >= 1m
            ? "critical"
            : active.Any(position => position.RiskLevel == "warning") || dailyLossUsed >= 0.70m
                ? "warning"
                : "normal";

        return Results.Ok(new RiskDetailResponseDto(
            Symbol: symbol,
            Equity: equity,
            AvailableBalance: wallets.UsdAvailableBalance(),
            MaxDailyLossPercent: maxDailyLossPercent,
            MaxDailyLoss: maxDailyLoss,
            DailyLossUsedPercent: dailyLossUsed,
            TotalNotional: totalNotional,
            ExposureRatio: exposureRatio,
            TotalUnrealizedProfit: totalUnrealized,
            DefaultLeverage: defaultLeverage,
            PortfolioRiskLevel: portfolioRisk,
            Positions: active));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            title: "Binance private risk request rejected",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/risk/auto-trading-status", async (
    IAutoTradeRiskGate riskGate,
    CancellationToken cancellationToken) =>
{
    var status = await riskGate.GetStatusAsync(cancellationToken);
    return Results.Ok(status);
});

app.MapGet("/api/backtest/run", async (
    string? symbol,
    string? interval,
    int? limit,
    decimal? initialEquity,
    decimal? leverage,
    IHttpClientFactory httpClientFactory,
    IOptions<BinanceOptions> binanceOptions,
    CancellationToken cancellationToken) =>
{
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();
    interval = string.IsNullOrWhiteSpace(interval) ? "1h" : interval;
    var take = Math.Clamp(limit ?? 1000, 100, 1500);
    var equity = initialEquity is > 0 ? initialEquity.Value : 10000m;
    var lev = leverage is > 0 ? leverage.Value : 5m;

    var baseUrl = binanceOptions.Value.RestBaseUrl.TrimEnd('/');
    var url = $"{baseUrl}/fapi/v1/klines?symbol={symbol}&interval={interval}&limit={take}";
    using var response = await httpClientFactory.CreateClient().GetAsync(url, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    if (!response.IsSuccessStatusCode)
    {
        return Results.Problem(body, statusCode: (int)response.StatusCode);
    }

    using var document = JsonDocument.Parse(body);
    var candles = document.RootElement.EnumerateArray().Select(item => new BacktestCandle(
        OpenTime: DateTimeOffset.FromUnixTimeMilliseconds(item[0].GetInt64()),
        Open: ProgramHelpers.ParseDecimal(item[1]),
        High: ProgramHelpers.ParseDecimal(item[2]),
        Low: ProgramHelpers.ParseDecimal(item[3]),
        Close: ProgramHelpers.ParseDecimal(item[4]),
        Volume: ProgramHelpers.ParseDecimal(item[5]))).ToList();

    return Results.Ok(ProgramHelpers.RunMultiIndicatorBacktest(symbol, interval, candles, equity, lev));
});

app.MapPost("/api/decision/evaluate", (
    DecisionInput input,
    IMultiFactorDecisionEngine engine,
    IRiskManager riskManager,
    IRuntimeTradingSettingsService settingsService) =>
{
    var settings = settingsService.GetRuntimeSettings();
    var decision = engine.Evaluate(input);
    var risk = riskManager.Evaluate(
        new RiskState(
            Equity: 100000m,
            AvailableBalance: 100000m,
            DailyLoss: 0m,
            ConsecutiveLosses: 0,
            OpenPositions: 0,
            CurrentExposure: 0m,
            Atr: input.Atr,
            LastPrice: input.LastPrice),
        decision,
        new RiskProfile(
            MaxDailyLoss: settings.MaxDailyLossPercent,
            MaxConsecutiveLosses: 3,
            MaxOpenPositions: 1,
            MaxExposure: settings.MaxExposurePercent,
            RiskPerTrade: settings.RiskPerTradePercent,
            MinimumRiskReward: 2m,
            AutoTradeConfidenceThreshold: settings.ConfidenceThreshold));

    return Results.Ok(new { decision, risk });
});

app.MapGet("/api/ai/analyze", async (
    string? symbol,
    IAiDecisionService aiService,
    CancellationToken cancellationToken) =>
{
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();
    try
    {
        var decision = await aiService.AnalyzeAsync(symbol, cancellationToken);
        return Results.Ok(decision);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "AI analysis failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

// Read-only: returns the latest cached decision produced by the analysis loop.
// Does NOT trigger a new analysis, so the dashboard never incurs Claude cost.
app.MapGet("/api/ai/decision", (
    string? symbol,
    ILatestDecisionStore store) =>
{
    symbol = string.IsNullOrWhiteSpace(symbol) ? "BTCUSDT" : symbol.ToUpperInvariant();
    var decision = store.Get(symbol);
    return decision is null ? Results.NoContent() : Results.Ok(decision);
});

app.MapGet("/api/ai/usage", async (
    IAiUsageTracker usageTracker,
    CancellationToken cancellationToken) =>
{
    var summary = await usageTracker.GetSummaryAsync(cancellationToken);
    return Results.Ok(summary);
});

app.MapGet("/api/risk/profile", (IRuntimeTradingSettingsService settingsService) =>
{
    var settings = settingsService.GetRuntimeSettings();
    return Results.Ok(new RiskProfile(
        MaxDailyLoss: settings.MaxDailyLossPercent,
        MaxConsecutiveLosses: 3,
        MaxOpenPositions: 1,
        MaxExposure: settings.MaxExposurePercent,
        RiskPerTrade: settings.RiskPerTradePercent,
        MinimumRiskReward: 2m,
        AutoTradeConfidenceThreshold: settings.ConfidenceThreshold));
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
    await db.Database.EnsureCreatedAsync();

    // EnsureCreated does not add tables to an already-existing schema; create the
    // AI-learning tables idempotently so upgrades don't require a DB reset.
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS trading."AiDecisionLogs" (
            "Id" uuid PRIMARY KEY,
            "Symbol" text NOT NULL,
            "Regime" integer NOT NULL,
            "Action" integer NOT NULL,
            "Confidence" numeric NOT NULL,
            "EntryPrice" numeric NOT NULL,
            "ScoresJson" text NOT NULL,
            "Evaluated" boolean NOT NULL,
            "Win" boolean NULL,
            "PriceMovePercent" numeric NOT NULL,
            "MatchedPositionId" uuid NULL,
            "LlmConfirmed" boolean NULL,
            "LlmSizeMultiplier" numeric NULL,
            "LlmLeverage" integer NULL,
            "LlmStopsApplied" boolean NULL,
            "CreatedAt" timestamptz NOT NULL,
            "EvaluatedAt" timestamptz NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_AiDecisionLogs_Evaluated_CreatedAt"
            ON trading."AiDecisionLogs" ("Evaluated", "CreatedAt");
        ALTER TABLE trading."AiDecisionLogs" ADD COLUMN IF NOT EXISTS "MatchedPositionId" uuid NULL;
        ALTER TABLE trading."AiDecisionLogs" ADD COLUMN IF NOT EXISTS "LlmConfirmed" boolean NULL;
        ALTER TABLE trading."AiDecisionLogs" ADD COLUMN IF NOT EXISTS "LlmSizeMultiplier" numeric NULL;
        ALTER TABLE trading."AiDecisionLogs" ADD COLUMN IF NOT EXISTS "LlmLeverage" integer NULL;
        ALTER TABLE trading."AiDecisionLogs" ADD COLUMN IF NOT EXISTS "LlmStopsApplied" boolean NULL;
        CREATE INDEX IF NOT EXISTS "IX_AiDecisionLogs_MatchedPositionId"
            ON trading."AiDecisionLogs" ("MatchedPositionId");
        CREATE TABLE IF NOT EXISTS trading."FactorStats" (
            "Id" uuid PRIMARY KEY,
            "Regime" integer NOT NULL,
            "Factor" text NOT NULL,
            "Alpha" numeric NOT NULL,
            "Beta" numeric NOT NULL,
            "UpdatedAt" timestamptz NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_FactorStats_Regime_Factor"
            ON trading."FactorStats" ("Regime", "Factor");
        CREATE TABLE IF NOT EXISTS trading."TradingSettings" (
            "Id" integer PRIMARY KEY,
            "PaperTradingOnly" boolean NOT NULL,
            "AutoTradingEnabled" boolean NOT NULL,
            "MaxDailyLossPercent" numeric NOT NULL,
            "RiskPerTradePercent" numeric NOT NULL,
            "MaxExposurePercent" numeric NOT NULL,
            "DefaultLeverage" integer NOT NULL,
            "ApiKey" text NULL,
            "ApiSecret" text NULL,
            "AnthropicApiKey" text NULL,
            "AiModel" text NULL,
            "ConfidenceThreshold" numeric NOT NULL
        );
        ALTER TABLE trading."TradingSettings" ADD COLUMN IF NOT EXISTS "LunarCrushApiKey" text NULL;
        ALTER TABLE trading."TradingSettings" ADD COLUMN IF NOT EXISTS "TargetMarginUsdt" numeric NOT NULL DEFAULT 3;
        ALTER TABLE trading."TradingSettings" ADD COLUMN IF NOT EXISTS "PositionCheckIntervalMinutes" integer NOT NULL DEFAULT 30;
        ALTER TABLE trading."TradingSettings" ADD COLUMN IF NOT EXISTS "TrailingStopDistanceR" numeric NOT NULL DEFAULT 1.0;
        ALTER TABLE trading."TradingSettings" ADD COLUMN IF NOT EXISTS "AutoSizingMode" integer NOT NULL DEFAULT 0;
        ALTER TABLE trading."TradingSettings" ADD COLUMN IF NOT EXISTS "TargetLeverage" integer NOT NULL DEFAULT 20;
        ALTER TABLE trading."TradingSettings" ADD COLUMN IF NOT EXISTS "TradingStyle" integer NOT NULL DEFAULT 0;
        ALTER TABLE trading."TradingSettings" ADD COLUMN IF NOT EXISTS "AccountRiskGuardEnabled" boolean NOT NULL DEFAULT TRUE;
        CREATE TABLE IF NOT EXISTS trading."AiUsage" (
            "Id" uuid PRIMARY KEY,
            "Model" text NOT NULL,
            "InputTokens" integer NOT NULL,
            "OutputTokens" integer NOT NULL,
            "CostUsd" numeric NOT NULL,
            "CreatedAt" timestamptz NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_AiUsage_CreatedAt" ON trading."AiUsage" ("CreatedAt");
        CREATE TABLE IF NOT EXISTS trading."Positions" (
            "Id" uuid PRIMARY KEY,
            "Symbol" text NOT NULL,
            "Side" integer NOT NULL,
            "Quantity" numeric NOT NULL,
            "EntryPrice" numeric NOT NULL,
            "MarkPrice" numeric NOT NULL,
            "UnrealizedPnl" numeric NOT NULL,
            "RealizedPnl" numeric NOT NULL,
            "Roi" numeric NOT NULL,
            "Leverage" integer NOT NULL,
            "StopLoss" numeric NULL,
            "TakeProfit" numeric NULL,
            "CloseReason" integer NOT NULL DEFAULT 0,
            "OpenedAt" timestamptz NOT NULL,
            "ClosedAt" timestamptz NULL
        );
        ALTER TABLE trading."Positions" ADD COLUMN IF NOT EXISTS "CloseReason" integer NOT NULL DEFAULT 0;
        ALTER TABLE trading."Positions" ADD COLUMN IF NOT EXISTS "Fees" numeric NOT NULL DEFAULT 0;
        CREATE INDEX IF NOT EXISTS "IX_Positions_Symbol_OpenedAt"
            ON trading."Positions" ("Symbol", "OpenedAt");
        CREATE INDEX IF NOT EXISTS "IX_Positions_Symbol_ClosedAt"
            ON trading."Positions" ("Symbol", "ClosedAt");
        CREATE TABLE IF NOT EXISTS trading."ExecutionStats" (
            "Id" uuid PRIMARY KEY,
            "Regime" integer NOT NULL,
            "TakeProfitHits" integer NOT NULL,
            "StopLossHits" integer NOT NULL,
            "Wins" integer NOT NULL,
            "Losses" integer NOT NULL,
            "SlAtrMultiplier" numeric NOT NULL,
            "TpAtrMultiplier" numeric NOT NULL,
            "LeverageFactor" numeric NOT NULL,
            "UpdatedAt" timestamptz NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExecutionStats_Regime"
            ON trading."ExecutionStats" ("Regime");
        CREATE TABLE IF NOT EXISTS trading."PushDevices" (
            "Id" uuid PRIMARY KEY,
            "DeviceToken" text NOT NULL,
            "Platform" text NOT NULL,
            "Environment" text NOT NULL,
            "Enabled" boolean NOT NULL,
            "CreatedAt" timestamptz NOT NULL,
            "UpdatedAt" timestamptz NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_PushDevices_DeviceToken"
            ON trading."PushDevices" ("DeviceToken");
        """);

    // Hydrate the in-memory runtime settings (incl. API keys) from the DB so they
    // survive container restarts.
    var settingsService = scope.ServiceProvider.GetRequiredService<IRuntimeTradingSettingsService>();
    await settingsService.LoadAsync(CancellationToken.None);
}

app.Run();

public sealed record ManualOrderDto(
    TradeSide Side,
    OrderKind Kind,
    decimal Quantity,
    decimal? Price,
    decimal? StopPrice,
    decimal? TakeProfit,
    decimal? StopLoss,
    int Leverage,
    bool ReduceOnly,
    string? Reason);

public sealed record ClosePositionDto(TradeSide Side, decimal? Quantity, string? Reason);

public sealed record KillSwitchDto(string? Symbol, long CountdownTimeMs, long HeartbeatIntervalMs);

public sealed record JournalOrderDto(
    Guid Id,
    string Symbol,
    string Side,
    string Kind,
    string Status,
    decimal Quantity,
    decimal? Price,
    decimal? StopPrice,
    decimal? TakeProfit,
    decimal? StopLoss,
    bool ReduceOnly,
    bool IsPaper,
    string? ExchangeOrderId,
    string Reason,
    DateTimeOffset CreatedAt);

public sealed record JournalSummaryDto(int TotalOrders, int FilledOrders, int RejectedOrders, int PaperOrders, int ReduceOnlyOrders);

public sealed record JournalResponseDto(JournalSummaryDto Summary, IReadOnlyList<JournalOrderDto> Orders);

public sealed record PositionHistoryItemDto(
    Guid Id,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal EntryPrice,
    decimal Margin,
    int Leverage,
    decimal? TakeProfit,
    decimal? StopLoss,
    string CloseReason,
    decimal RealizedPnl,
    decimal Roi,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt);

public sealed record PositionPnlBucketDto(string Label, DateTimeOffset PeriodStart, decimal RealizedPnl, int Trades);

public sealed record PositionHistorySummaryDto(
    decimal TotalRealizedPnl,
    int TotalTrades,
    decimal WinRate,
    decimal BestTrade,
    decimal WorstTrade);

public sealed record PositionHistoryResponseDto(
    PositionHistorySummaryDto Summary,
    IReadOnlyList<PositionPnlBucketDto> Daily,
    IReadOnlyList<PositionPnlBucketDto> Monthly,
    IReadOnlyList<PositionHistoryItemDto> Positions);

public sealed record PositionRiskDto(
    string Symbol,
    string PositionSide,
    string MarginType,
    decimal PositionAmount,
    decimal EntryPrice,
    decimal MarkPrice,
    decimal LiquidationPrice,
    decimal Notional,
    decimal InitialMargin,
    decimal UnrealizedProfit,
    decimal UnrealizedProfitPercent,
    decimal Leverage,
    decimal MarginRatio,
    decimal LiquidationBufferPercent,
    string RiskLevel);

public sealed record RiskDetailResponseDto(
    string Symbol,
    decimal Equity,
    decimal AvailableBalance,
    decimal MaxDailyLossPercent,
    decimal MaxDailyLoss,
    decimal DailyLossUsedPercent,
    decimal TotalNotional,
    decimal ExposureRatio,
    decimal TotalUnrealizedProfit,
    decimal DefaultLeverage,
    string PortfolioRiskLevel,
    IReadOnlyList<PositionRiskDto> Positions);

public sealed record BacktestCandle(DateTimeOffset OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

public sealed record BacktestTradeDto(
    string Side,
    DateTimeOffset EntryTime,
    DateTimeOffset ExitTime,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal Quantity,
    decimal Pnl,
    decimal PnlPercent,
    string ExitReason);

public sealed record BacktestResultDto(
    string Symbol,
    string Interval,
    int Candles,
    decimal InitialEquity,
    decimal FinalEquity,
    decimal NetPnl,
    decimal NetPnlPercent,
    decimal MaxDrawdownPercent,
    decimal WinRate,
    decimal ProfitFactor,
    int TotalTrades,
    decimal FeeRate,
    decimal SlippageRate,
    decimal Leverage,
    IReadOnlyList<string> Indicators,
    IReadOnlyList<BacktestTradeDto> Trades);

public sealed record MarketKlineDto(
    string Symbol,
    string Interval,
    DateTimeOffset OpenTime,
    DateTimeOffset CloseTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal QuoteVolume,
    long NumberOfTrades,
    decimal TakerBuyBaseVolume,
    decimal TakerBuyQuoteVolume,
    bool IsClosed,
    DateTimeOffset EventTime);

public sealed record MarketAggTradeDto(
    string Symbol,
    decimal Price,
    decimal Quantity,
    bool BuyerIsMaker,
    DateTimeOffset Time);

public sealed record MarketMarkPriceDto(
    string Symbol,
    decimal MarkPrice,
    decimal MarkPriceMovingAverage,
    decimal IndexPrice,
    decimal EstimatedSettlePrice,
    decimal FundingRate,
    DateTimeOffset NextFundingTime,
    DateTimeOffset Time);

public static class ProgramHelpers
{
    public static decimal ParseDecimal(JsonElement element)
    {
        return Decimal.Parse(element.GetString() ?? "0", CultureInfo.InvariantCulture);
    }

    public static decimal TryParseDecimal(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) ? ParseDecimal(value) : 0;
    }

    public static BacktestResultDto RunMultiIndicatorBacktest(
        string symbol,
        string interval,
        IReadOnlyList<BacktestCandle> candles,
        decimal initialEquity,
        decimal leverage)
    {
        const decimal feeRate = 0.0004m;
        const decimal slippageRate = 0.0002m;
        const decimal riskFraction = 0.10m;
        var indicators = new[]
        {
            "EMA 9/21/55",
            "RSI 14",
            "MACD 12/26/9",
            "Bollinger 20/2",
            "Stochastic 14",
            "ATR 14",
            "Volume SMA 20"
        };

        if (candles.Count < 80)
        {
            return new BacktestResultDto(symbol, interval, candles.Count, initialEquity, initialEquity, 0, 0, 0, 0, 0, 0, feeRate, slippageRate, leverage, indicators, []);
        }

        var closes = candles.Select(candle => candle.Close).ToList();
        var volumes = candles.Select(candle => candle.Volume).ToList();
        var ema9 = Ema(closes, 9);
        var ema21 = Ema(closes, 21);
        var ema55 = Ema(closes, 55);
        var rsi = Rsi(closes, 14);
        var macdFast = Ema(closes, 12);
        var macdSlow = Ema(closes, 26);
        var macd = macdFast.Zip(macdSlow, (fast, slow) => fast - slow).ToList();
        var macdSignal = Ema(macd, 9);
        var atr = Atr(candles, 14);
        var volumeSma = Sma(volumes, 20);

        var equity = initialEquity;
        var peakEquity = initialEquity;
        var maxDrawdown = 0m;
        var trades = new List<BacktestTradeDto>();
        string? side = null;
        decimal entryPrice = 0;
        decimal quantity = 0;
        decimal stopLoss = 0;
        decimal takeProfit = 0;
        DateTimeOffset entryTime = DateTimeOffset.MinValue;

        for (var index = 60; index < candles.Count; index++)
        {
            var candle = candles[index];
            var score = SignalScore(index, candles, ema9, ema21, ema55, rsi, macd, macdSignal, volumeSma);

            if (side is not null)
            {
                var exitPrice = 0m;
                var exitReason = "";
                if (side == "LONG")
                {
                    if (candle.Low <= stopLoss)
                    {
                        exitPrice = stopLoss * (1 - slippageRate);
                        exitReason = "SL";
                    }
                    else if (candle.High >= takeProfit)
                    {
                        exitPrice = takeProfit * (1 - slippageRate);
                        exitReason = "TP";
                    }
                    else if (score <= -2)
                    {
                        exitPrice = candle.Close * (1 - slippageRate);
                        exitReason = "Signal flip";
                    }
                }
                else
                {
                    if (candle.High >= stopLoss)
                    {
                        exitPrice = stopLoss * (1 + slippageRate);
                        exitReason = "SL";
                    }
                    else if (candle.Low <= takeProfit)
                    {
                        exitPrice = takeProfit * (1 + slippageRate);
                        exitReason = "TP";
                    }
                    else if (score >= 2)
                    {
                        exitPrice = candle.Close * (1 + slippageRate);
                        exitReason = "Signal flip";
                    }
                }

                if (exitPrice > 0)
                {
                    var grossPnl = side == "LONG"
                        ? (exitPrice - entryPrice) * quantity
                        : (entryPrice - exitPrice) * quantity;
                    var fees = (entryPrice * quantity * feeRate) + (exitPrice * quantity * feeRate);
                    var pnl = grossPnl - fees;
                    equity += pnl;
                    peakEquity = Math.Max(peakEquity, equity);
                    maxDrawdown = Math.Max(maxDrawdown, peakEquity > 0 ? (peakEquity - equity) / peakEquity : 0);
                    trades.Add(new BacktestTradeDto(side, entryTime, candle.OpenTime, entryPrice, exitPrice, quantity, pnl, pnl / initialEquity, exitReason));
                    side = null;
                }
            }

            if (side is null && equity > 0 && atr[index] > 0)
            {
                if (score >= 3)
                {
                    side = "LONG";
                    entryPrice = candle.Close * (1 + slippageRate);
                    quantity = (equity * riskFraction * leverage) / entryPrice;
                    stopLoss = entryPrice - (atr[index] * 1.5m);
                    takeProfit = entryPrice + (atr[index] * 3m);
                    entryTime = candle.OpenTime;
                }
                else if (score <= -3)
                {
                    side = "SHORT";
                    entryPrice = candle.Close * (1 - slippageRate);
                    quantity = (equity * riskFraction * leverage) / entryPrice;
                    stopLoss = entryPrice + (atr[index] * 1.5m);
                    takeProfit = entryPrice - (atr[index] * 3m);
                    entryTime = candle.OpenTime;
                }
            }
        }

        var wins = trades.Count(trade => trade.Pnl > 0);
        var grossProfit = trades.Where(trade => trade.Pnl > 0).Sum(trade => trade.Pnl);
        var grossLoss = Math.Abs(trades.Where(trade => trade.Pnl < 0).Sum(trade => trade.Pnl));
        var netPnl = equity - initialEquity;

        return new BacktestResultDto(
            symbol,
            interval,
            candles.Count,
            initialEquity,
            equity,
            netPnl,
            initialEquity > 0 ? netPnl / initialEquity : 0,
            maxDrawdown,
            trades.Count > 0 ? wins / (decimal)trades.Count : 0,
            grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? 999m : 0m,
            trades.Count,
            feeRate,
            slippageRate,
            leverage,
            indicators,
            trades.TakeLast(50).Reverse().ToList());
    }

    private static int SignalScore(
        int index,
        IReadOnlyList<BacktestCandle> candles,
        IReadOnlyList<decimal> ema9,
        IReadOnlyList<decimal> ema21,
        IReadOnlyList<decimal> ema55,
        IReadOnlyList<decimal> rsi,
        IReadOnlyList<decimal> macd,
        IReadOnlyList<decimal> macdSignal,
        IReadOnlyList<decimal> volumeSma)
    {
        var close = candles[index].Close;
        var window = candles.Skip(index - 19).Take(20).Select(candle => candle.Close).ToList();
        var mean = window.Average();
        var stdDev = Sqrt(window.Sum(value => (value - mean) * (value - mean)) / window.Count);
        var upperBand = mean + (2m * stdDev);
        var lowerBand = mean - (2m * stdDev);
        var high14 = candles.Skip(index - 13).Take(14).Max(candle => candle.High);
        var low14 = candles.Skip(index - 13).Take(14).Min(candle => candle.Low);
        var stoch = high14 > low14 ? ((close - low14) / (high14 - low14)) * 100m : 50m;
        var score = 0;

        if (ema9[index] > ema21[index] && ema21[index] > ema55[index]) score += 2;
        if (ema9[index] < ema21[index] && ema21[index] < ema55[index]) score -= 2;
        if (rsi[index] > 52m && rsi[index] < 72m) score++;
        if (rsi[index] < 48m && rsi[index] > 28m) score--;
        if (macd[index] > macdSignal[index]) score++;
        if (macd[index] < macdSignal[index]) score--;
        if (close <= lowerBand) score++;
        if (close >= upperBand) score--;
        if (stoch > 55m && stoch < 85m) score++;
        if (stoch < 45m && stoch > 15m) score--;
        if (candles[index].Volume > volumeSma[index]) score += Math.Sign(score);

        return score;
    }

    private static List<decimal> Ema(IReadOnlyList<decimal> values, int period)
    {
        var result = new List<decimal>(values.Count);
        var multiplier = 2m / (period + 1);
        var ema = values[0];
        foreach (var value in values)
        {
            ema = ((value - ema) * multiplier) + ema;
            result.Add(ema);
        }

        return result;
    }

    private static List<decimal> Sma(IReadOnlyList<decimal> values, int period)
    {
        var result = new List<decimal>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var start = Math.Max(0, index - period + 1);
            var count = index - start + 1;
            result.Add(values.Skip(start).Take(count).Average());
        }

        return result;
    }

    private static List<decimal> Rsi(IReadOnlyList<decimal> closes, int period)
    {
        var result = Enumerable.Repeat(50m, closes.Count).ToList();
        for (var index = period; index < closes.Count; index++)
        {
            var gains = 0m;
            var losses = 0m;
            for (var lookback = index - period + 1; lookback <= index; lookback++)
            {
                var change = closes[lookback] - closes[lookback - 1];
                if (change >= 0) gains += change;
                else losses += Math.Abs(change);
            }

            result[index] = losses == 0 ? 100m : 100m - (100m / (1m + (gains / losses)));
        }

        return result;
    }

    private static List<decimal> Atr(IReadOnlyList<BacktestCandle> candles, int period)
    {
        var trueRanges = new List<decimal> { candles[0].High - candles[0].Low };
        for (var index = 1; index < candles.Count; index++)
        {
            var highLow = candles[index].High - candles[index].Low;
            var highClose = Math.Abs(candles[index].High - candles[index - 1].Close);
            var lowClose = Math.Abs(candles[index].Low - candles[index - 1].Close);
            trueRanges.Add(Math.Max(highLow, Math.Max(highClose, lowClose)));
        }

        return Sma(trueRanges, period);
    }

    private static decimal Sqrt(decimal value)
    {
        return value <= 0 ? 0 : (decimal)Math.Sqrt((double)value);
    }
}
