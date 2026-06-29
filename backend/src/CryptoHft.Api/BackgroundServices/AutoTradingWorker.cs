using CryptoHft.Application.Abstractions;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;

namespace CryptoHft.Api.BackgroundServices;

// The autonomous trading loop. On each tick (when Auto mode is enabled) it runs the AI
// decision engine, applies a hard risk gate, and — if the signal is actionable and no
// position is already open — places an order through the executor (paper or live per settings).
public sealed class AutoTradingWorker(
    IServiceScopeFactory scopeFactory,
    IAiDecisionService aiDecisionService,
    IRuntimeTradingSettingsService settingsService,
    IFuturesAccountClient accountClient,
    ILatestDecisionStore decisionStore,
    ILogger<AutoTradingWorker> logger) : BackgroundService
{
    private const string Symbol = "BTCUSDT";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Auto-trading worker started (interval {Interval}s)", Interval.TotalSeconds);
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auto-trading tick failed");
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var settings = settingsService.GetRuntimeSettings();

        // Run the analysis once per tick regardless of mode and cache it — this is the
        // single place that may call Claude. The dashboard reads the cached result so
        // opening it never triggers extra (billed) analyses.
        var decision = await aiDecisionService.AnalyzeAsync(Symbol, cancellationToken);
        decisionStore.Set(Symbol, decision);

        logger.LogInformation(
            "AI decision: {Action} conf={Confidence} regime={Regime} shouldTrade={ShouldTrade} {Reason}",
            decision.Action, decision.Confidence, decision.Regime, decision.ShouldTrade, decision.NoTradeReason);

        // Order placement only in Auto mode.
        if (!settings.AutoTradingEnabled) return;

        // Don't open a new position if one is already active (MaxOpenPositions = 1).
        if (await HasOpenPositionAsync(cancellationToken))
        {
            logger.LogDebug("Auto-trade skipped: position already open");
            return;
        }

        if (!decision.ShouldTrade) return;
        if (decision.PositionSizeQuantity <= 0)
        {
            logger.LogWarning("Auto-trade skipped: computed quantity is zero");
            return;
        }

        var side = decision.Action is DecisionAction.WeakBuy or DecisionAction.Buy or DecisionAction.StrongBuy
            ? TradeSide.Long
            : TradeSide.Short;

        var reason = $"AI auto {decision.Action} conf {decision.Confidence:F0}/{decision.ProbabilityOfSuccess:F0}% regime {decision.Regime}";

        using var scope = scopeFactory.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ITradingExecutor>();

        var order = new TradeOrderRequest(
            Symbol,
            side,
            OrderKind.Market,
            decision.PositionSizeQuantity,
            Price: null,
            StopPrice: null,
            TakeProfit: decision.TakeProfit,
            StopLoss: decision.StopLoss,
            Leverage: decision.Leverage,
            ReduceOnly: false,
            TradingMode.Auto,
            reason);

        try
        {
            var result = await executor.PlaceAsync(order, cancellationToken);
            logger.LogInformation("Auto-trade placed: {Side} {Qty} {Symbol} order {OrderId} (paper={Paper})",
                side, decision.PositionSizeQuantity, Symbol, result.OrderId, result.IsPaper);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Auto-trade order rejected");
        }
    }

    private async Task<bool> HasOpenPositionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var positions = await accountClient.GetPositionsAsync(Symbol, cancellationToken);
            return positions.Any(p => Math.Abs(p.PositionAmount) > 0);
        }
        catch
        {
            // If we can't confirm position state, err on the safe side and skip trading.
            return true;
        }
    }
}
