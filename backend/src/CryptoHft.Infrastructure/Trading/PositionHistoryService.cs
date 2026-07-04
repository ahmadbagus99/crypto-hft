using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoHft.Application.Account;
using CryptoHft.Application.Trading;
using CryptoHft.Domain.Entities;
using CryptoHft.Domain.Enums;
using CryptoHft.Infrastructure.Binance;
using CryptoHft.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Trading;

public sealed class PositionHistoryService(
    IServiceScopeFactory scopeFactory,
    IRuntimeTradingSettingsService settingsService,
    IHttpClientFactory httpClientFactory,
    IOptions<BinanceOptions> options,
    ILogger<PositionHistoryService> logger) : IPositionHistoryService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, TrackedPosition> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly BinanceOptions _options = options.Value;

    public async Task ObserveAsync(string symbol, FuturesPositionInfo? openPosition, CancellationToken cancellationToken)
    {
        symbol = symbol.ToUpperInvariant();
        TrackedPosition? toClose = null;

        lock (_lock)
        {
            if (openPosition is null || Math.Abs(openPosition.PositionAmount) <= 0)
            {
                _active.Remove(symbol, out toClose);
            }
            else
            {
                var side = openPosition.PositionAmount > 0 ? TradeSide.Long : TradeSide.Short;
                if (!_active.TryGetValue(symbol, out var tracked)
                    || tracked.Side != side)
                {
                    if (tracked is not null)
                        toClose = tracked;
                    tracked = TrackedPosition.From(openPosition, side);
                    _active[symbol] = tracked;
                }
                else
                {
                    tracked.Update(openPosition);
                }
            }
        }

        if (toClose is not null)
            await SaveClosedAsync(toClose, cancellationToken);
    }

    private async Task SaveClosedAsync(TrackedPosition tracked, CancellationToken cancellationToken)
    {
        var closedAt = DateTimeOffset.UtcNow;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var (takeProfit, stopLoss) = await GetLatestProtectiveLevelsAsync(db, tracked, cancellationToken);
            var realizedPnl = await ResolveRealizedPnlAsync(tracked, closedAt, cancellationToken);
            var margin = tracked.Leverage > 0
                ? Math.Abs(tracked.Quantity) * tracked.EntryPrice / tracked.Leverage
                : 0m;
            var roi = margin > 0 ? realizedPnl / margin : 0m;

            // Why did it close? App-initiated exits leave a reduce-only close order; exchange
            // SL/TP fills are inferred from the last mark vs the protective levels.
            var closeOrderReason = await GetRecentCloseOrderReasonAsync(db, tracked, closedAt, cancellationToken);
            var closeReason = PositionCloseClassifier.Classify(
                tracked.Side, tracked.MarkPrice, stopLoss, takeProfit, closeOrderReason);

            db.Positions.Add(new Position
            {
                Symbol = tracked.Symbol,
                Side = tracked.Side,
                Quantity = Math.Abs(tracked.Quantity),
                EntryPrice = tracked.EntryPrice,
                MarkPrice = tracked.MarkPrice,
                UnrealizedPnl = tracked.UnrealizedProfit,
                RealizedPnl = realizedPnl,
                Roi = roi,
                Leverage = tracked.Leverage,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                CloseReason = closeReason,
                OpenedAt = tracked.OpenedAt,
                ClosedAt = closedAt
            });

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Position history saved: {Side} {Qty} {Symbol} realizedPnL={Pnl:F4}",
                tracked.Side, Math.Abs(tracked.Quantity), tracked.Symbol, realizedPnl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "position history save failed");
        }
    }

    // A reduce-only market order shortly before the close means the app (auto-close
    // revalidation or manual dashboard close) exited the position rather than the exchange.
    private static async Task<string?> GetRecentCloseOrderReasonAsync(
        TradingDbContext db,
        TrackedPosition tracked,
        DateTimeOffset closedAt,
        CancellationToken cancellationToken)
    {
        var closeSide = tracked.Side == TradeSide.Long ? TradeSide.Short : TradeSide.Long;
        var order = await db.Orders
            .AsNoTracking()
            .Where(o => o.Symbol == tracked.Symbol
                        && o.ReduceOnly
                        && o.Side == closeSide
                        && o.Kind == OrderKind.Market
                        && o.CreatedAt >= closedAt.AddMinutes(-2)
                        && o.CreatedAt <= closedAt.AddSeconds(10))
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return order?.Reason;
    }

    private static async Task<(decimal? takeProfit, decimal? stopLoss)> GetLatestProtectiveLevelsAsync(
        TradingDbContext db,
        TrackedPosition tracked,
        CancellationToken cancellationToken)
    {
        var closeSide = tracked.Side == TradeSide.Long ? TradeSide.Short : TradeSide.Long;
        var orders = await db.Orders
            .AsNoTracking()
            .Where(order => order.Symbol == tracked.Symbol
                            && order.ReduceOnly
                            && order.Side == closeSide
                            && order.Status == OrderStatus.New
                            && (order.Kind == OrderKind.TakeProfit || order.Kind == OrderKind.StopMarket))
            .OrderByDescending(order => order.CreatedAt)
            .ToListAsync(cancellationToken);

        var takeProfit = orders.FirstOrDefault(order => order.Kind == OrderKind.TakeProfit)?.StopPrice;
        var stopLoss = orders.FirstOrDefault(order => order.Kind == OrderKind.StopMarket)?.StopPrice;
        return (takeProfit, stopLoss);
    }

    private async Task<decimal> ResolveRealizedPnlAsync(
        TrackedPosition tracked,
        DateTimeOffset closedAt,
        CancellationToken cancellationToken)
    {
        var settings = settingsService.GetRuntimeSettings();
        if (settings.PaperTradingOnly
            || string.IsNullOrWhiteSpace(settings.ApiKey)
            || string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            return tracked.UnrealizedProfit;
        }

        try
        {
            var entries = await FetchRealizedPnlAsync(
                tracked.Symbol,
                tracked.OpenedAt.AddMinutes(-2),
                closedAt.AddMinutes(2),
                settings,
                cancellationToken);
            return entries.Count == 0 ? tracked.UnrealizedProfit : entries.Sum(entry => entry.Pnl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "realized PnL fetch failed; using last unrealized PnL snapshot");
            return tracked.UnrealizedProfit;
        }
    }

    private async Task<IReadOnlyList<RealizedPnlEntry>> FetchRealizedPnlAsync(
        string symbol,
        DateTimeOffset start,
        DateTimeOffset end,
        RuntimeTradingSettings settings,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var query = $"symbol={symbol}&incomeType=REALIZED_PNL&startTime={start.ToUnixTimeMilliseconds()}" +
                    $"&endTime={end.ToUnixTimeMilliseconds()}&limit=1000&timestamp={timestamp}&recvWindow=5000";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(settings.ApiSecret!));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(query))).ToLowerInvariant();
        var url = $"{_options.RestBaseUrl.TrimEnd('/')}/fapi/v1/income?{query}&signature={signature}";

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-MBX-APIKEY", settings.ApiKey!);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"income fetch failed: {(int)response.StatusCode} {body}");

        using var doc = JsonDocument.Parse(body);
        var entries = new List<RealizedPnlEntry>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("income", out var income) || !item.TryGetProperty("time", out var time))
                continue;
            var pnl = income.ValueKind == JsonValueKind.Number
                ? income.GetDecimal()
                : decimal.Parse(income.GetString() ?? "0", CultureInfo.InvariantCulture);
            entries.Add(new RealizedPnlEntry(DateTimeOffset.FromUnixTimeMilliseconds(time.GetInt64()), pnl));
        }
        return entries;
    }

    private sealed class TrackedPosition
    {
        private TrackedPosition(
            string symbol,
            TradeSide side,
            decimal quantity,
            decimal entryPrice,
            decimal markPrice,
            decimal unrealizedProfit,
            int leverage)
        {
            Symbol = symbol;
            Side = side;
            Quantity = quantity;
            EntryPrice = entryPrice;
            MarkPrice = markPrice;
            UnrealizedProfit = unrealizedProfit;
            Leverage = leverage;
            OpenedAt = DateTimeOffset.UtcNow;
        }

        public string Symbol { get; }
        public TradeSide Side { get; }
        public decimal Quantity { get; private set; }
        public decimal EntryPrice { get; private set; }
        public decimal MarkPrice { get; private set; }
        public decimal UnrealizedProfit { get; private set; }
        public int Leverage { get; private set; }
        public DateTimeOffset OpenedAt { get; }

        public static TrackedPosition From(FuturesPositionInfo position, TradeSide side)
            => new(
                position.Symbol.ToUpperInvariant(),
                side,
                position.PositionAmount,
                position.EntryPrice,
                position.MarkPrice,
                position.UnrealizedProfit,
                (int)Math.Round(position.Leverage));

        public void Update(FuturesPositionInfo position)
        {
            Quantity = position.PositionAmount;
            EntryPrice = position.EntryPrice;
            MarkPrice = position.MarkPrice;
            UnrealizedProfit = position.UnrealizedProfit;
            Leverage = (int)Math.Round(position.Leverage);
        }
    }
}
