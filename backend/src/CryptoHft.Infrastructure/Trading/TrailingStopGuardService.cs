using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Account;
using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;
using CryptoHft.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoHft.Infrastructure.Trading;

// Runs on every auto-trading tick (~30s) while a position is open. Reads the outstanding
// protective levels from the Orders table (the same source Position History uses, so the
// two never disagree), asks TrailingStopPolicy for a verdict, and executes the amendment
// through the trading executor. The initial stop — the R unit — is captured on the first
// tick of each position and kept in memory; after an app restart mid-position the policy
// falls back to the TP-derived risk unit, so the ratchet keeps working.
public sealed class TrailingStopGuardService(
    IServiceScopeFactory scopeFactory,
    IRuntimeTradingSettingsService settingsService,
    ITrailingStopActivityStore activityStore,
    ILogger<TrailingStopGuardService> logger) : ITrailingStopGuard
{
    private readonly object _lock = new();
    private string? _positionKey;
    private decimal? _initialStopLoss;

    public async Task ApplyAsync(string symbol, FuturesPositionInfo position, CancellationToken cancellationToken)
    {
        try
        {
            var settings = settingsService.GetRuntimeSettings();
            // Paper mode has no exchange position to protect; with auto trading off the bot
            // must not touch live orders on its own.
            if (settings.PaperTradingOnly || !settings.AutoTradingEnabled) return;

            var quantity = Math.Abs(position.PositionAmount);
            if (quantity <= 0) return;
            var side = position.PositionAmount > 0 ? TradeSide.Long : TradeSide.Short;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var (currentStopLoss, takeProfit) = await GetProtectiveLevelsAsync(db, symbol, side, cancellationToken);

            var initialStopLoss = ResolveInitialStopLoss(symbol, side, position.EntryPrice, currentStopLoss);
            activityStore.StartOrUpdatePosition(symbol, side, position.EntryPrice, initialStopLoss, currentStopLoss);

            var verdict = TrailingStopPolicy.Evaluate(
                side, position.EntryPrice, position.MarkPrice, initialStopLoss, currentStopLoss, takeProfit);
            if (verdict.NewStopLoss is not decimal newStop) return;

            var executor = scope.ServiceProvider.GetRequiredService<ITradingExecutor>();
            var result = await executor.AmendStopLossAsync(new AmendStopLossRequest(
                symbol, side, quantity, newStop, $"Trailing stop: {verdict.Reason}"), cancellationToken);

            activityStore.Record(new TrailingStopEvent(
                symbol, side, position.EntryPrice, position.MarkPrice,
                currentStopLoss, newStop, verdict.ProfitR, verdict.Reason, DateTimeOffset.UtcNow));

            logger.LogInformation(
                "Trailing stop ratcheted: {Side} {Symbol} SL {Old} -> {New} (order {OrderId}) {Message}",
                side, symbol, currentStopLoss, newStop, result.OrderId, result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Trailing stop guard failed for {Symbol}; will retry next tick", symbol);
        }
    }

    // The outstanding protective levels: latest still-open reduce-only stop/TP for the
    // position's close side — identical query shape to PositionHistoryService's.
    private static async Task<(decimal? StopLoss, decimal? TakeProfit)> GetProtectiveLevelsAsync(
        TradingDbContext db, string symbol, TradeSide positionSide, CancellationToken cancellationToken)
    {
        var normalizedSymbol = symbol.ToUpperInvariant();
        var closeSide = positionSide == TradeSide.Long ? TradeSide.Short : TradeSide.Long;
        var orders = await db.Orders
            .AsNoTracking()
            .Where(o => o.Symbol == normalizedSymbol
                        && o.ReduceOnly
                        && o.Side == closeSide
                        && o.Status == OrderStatus.New
                        && (o.Kind == OrderKind.TakeProfit || o.Kind == OrderKind.StopMarket))
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

        return (
            orders.FirstOrDefault(o => o.Kind == OrderKind.StopMarket)?.StopPrice,
            orders.FirstOrDefault(o => o.Kind == OrderKind.TakeProfit)?.StopPrice);
    }

    // First tick of a new position: remember its entry stop as the R unit. Amendments never
    // overwrite it; a different position (new side/entry) resets it.
    private decimal? ResolveInitialStopLoss(string symbol, TradeSide side, decimal entryPrice, decimal? currentStopLoss)
    {
        var key = $"{symbol.ToUpperInvariant()}|{side}|{entryPrice}";
        lock (_lock)
        {
            if (_positionKey != key)
            {
                _positionKey = key;
                _initialStopLoss = currentStopLoss;
            }
            return _initialStopLoss;
        }
    }
}
