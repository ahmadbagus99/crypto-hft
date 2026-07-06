using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Account;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Risk;
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
    IAutoTradeRiskGate riskGate,
    IOpenPositionRevalidationStore revalidationStore,
    IPositionHistoryService positionHistoryService,
    ITrailingStopGuard trailingStopGuard,
    ITrailingStopActivityStore trailingStopActivity,
    ILatestDecisionStore decisionStore,
    ILogger<AutoTradingWorker> logger) : BackgroundService
{
    private const string Symbol = "BTCUSDT";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OpenPositionRevalidationInterval = TimeSpan.FromMinutes(30);
    private DateTimeOffset _nextOpenPositionRevalidationAt = DateTimeOffset.MinValue;
    private int _openPositionWarningCount;
    private TradeSide? _lastOpenPositionSide;
    private decimal? _lastOpenPositionEntryPrice;

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

        var (positionStateKnown, openPosition) = await GetOpenPositionAsync(cancellationToken);
        if (!positionStateKnown) return;
        await positionHistoryService.ObserveAsync(Symbol, openPosition, cancellationToken);

        // While a position is open, keep entry analysis paused so no new trade can be opened and
        // no Claude tokens are spent. Every tick the trailing guard may ratchet the stop
        // (breakeven at +1R, then trail); every 30 minutes a rule-based health check runs — if the
        // opposite side confirms twice (or becomes extremely strong once), close via reduce-only.
        if (openPosition is not null)
        {
            await trailingStopGuard.ApplyAsync(Symbol, openPosition, cancellationToken);
            await RevalidateOpenPositionAsync(settings, openPosition, cancellationToken);
            return;
        }
        ResetOpenPositionRevalidationState();
        revalidationStore.Clear(Symbol);
        // Position closed: the trailing history belongs to that position only — wipe it so
        // the dashboard card starts fresh with the next position.
        trailingStopActivity.Clear(Symbol);

        // Run the analysis once per tick and cache it — this is the single place that may call
        // Claude. The dashboard reads the cached result so opening it never triggers extra
        // (billed) analyses.
        var decision = await aiDecisionService.AnalyzeAsync(Symbol, cancellationToken);
        decisionStore.Set(Symbol, decision);

        logger.LogInformation(
            "AI decision: {Action} conf={Confidence} regime={Regime} shouldTrade={ShouldTrade} {Reason}",
            decision.Action, decision.Confidence, decision.Regime, decision.ShouldTrade, decision.NoTradeReason);

        // Order placement only in Auto mode.
        if (!settings.AutoTradingEnabled) return;
        if (!decision.ShouldTrade) return;
        if (decision.PositionSizeQuantity <= 0)
        {
            logger.LogWarning("Auto-trade skipped: computed quantity is zero");
            return;
        }

        // Account-level risk gate: daily loss / consecutive losses pause trading; the exposure
        // cap only trims the quantity. The signal itself is never re-judged here — confidence
        // above the threshold already decided that the position opens.
        var verdict = await riskGate.EvaluateAsync(
            Symbol, decision.PositionSizeQuantity, decision.EntryPrice, decision.Leverage, cancellationToken);
        if (!verdict.Allowed)
        {
            logger.LogWarning("Auto-trade blocked by risk gate: {Reason}", verdict.Reason);
            return;
        }
        var quantity = verdict.AdjustedQuantity ?? decision.PositionSizeQuantity;
        if (verdict.AdjustedQuantity is not null)
            logger.LogInformation("Risk gate resized order: {Reason}", verdict.Reason);

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
            quantity,
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
            logger.LogInformation("Auto-trade placed: {Side} {Qty} {Symbol} order {OrderId} (paper={Paper}) {Message}",
                side, quantity, Symbol, result.OrderId, result.IsPaper, result.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Auto-trade order rejected");
        }
    }

    private async Task<(bool StateKnown, FuturesPositionInfo? Position)> GetOpenPositionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var positions = await accountClient.GetPositionsAsync(Symbol, cancellationToken);
            return (true, positions.FirstOrDefault(p => Math.Abs(p.PositionAmount) > 0));
        }
        catch (Exception ex)
        {
            // If we can't confirm position state, err on the safe side and skip trading.
            logger.LogWarning(ex, "Position state unavailable; skipping this auto-trading tick");
            return (false, null);
        }
    }

    private async Task RevalidateOpenPositionAsync(
        RuntimeTradingSettings settings,
        FuturesPositionInfo position,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var openSide = position.PositionAmount > 0 ? TradeSide.Long : TradeSide.Short;
        var quantity = Math.Abs(position.PositionAmount);

        if (_lastOpenPositionSide != openSide
            || _lastOpenPositionEntryPrice != position.EntryPrice
            || _nextOpenPositionRevalidationAt == DateTimeOffset.MinValue)
        {
            _lastOpenPositionSide = openSide;
            _lastOpenPositionEntryPrice = position.EntryPrice;
            _openPositionWarningCount = 0;
            _nextOpenPositionRevalidationAt = now.Add(OpenPositionRevalidationInterval);
            revalidationStore.StartOrUpdatePosition(Symbol, openSide, quantity, position.EntryPrice, _nextOpenPositionRevalidationAt);
            logger.LogInformation(
                "Position open ({Side} {Qty} {Symbol}); entry analysis paused, first rule-based revalidation at {NextCheck:u}",
                openSide, quantity, Symbol, _nextOpenPositionRevalidationAt);
            return;
        }

        if (now < _nextOpenPositionRevalidationAt)
        {
            logger.LogDebug(
                "Analysis paused: position open ({Side} {Qty} {Symbol}); next rule-based revalidation at {NextCheck:u}",
                openSide, quantity, Symbol, _nextOpenPositionRevalidationAt);
            return;
        }

        _nextOpenPositionRevalidationAt = now.Add(OpenPositionRevalidationInterval);
        revalidationStore.SetNextCheck(Symbol, _nextOpenPositionRevalidationAt);

        var decision = await aiDecisionService.AnalyzeRuleBasedAsync(Symbol, cancellationToken);
        var verdict = OpenPositionRevalidationPolicy.Evaluate(
            openSide,
            decision,
            settings.ConfidenceThreshold,
            _openPositionWarningCount);
        _openPositionWarningCount = verdict.WarningCount;

        logger.LogInformation(
            "Open-position revalidation: position={Side} oppositeConf={OppositeConfidence:F0} action={Action} reason={Reason}",
            openSide, verdict.OppositeConfidence, verdict.Action, verdict.Reason);

        revalidationStore.Add(new OpenPositionRevalidationRecord(
            Symbol,
            openSide,
            quantity,
            position.EntryPrice,
            position.MarkPrice,
            position.UnrealizedProfit,
            verdict.OppositeConfidence,
            verdict.Action,
            verdict.Reason,
            now), _nextOpenPositionRevalidationAt);

        if (verdict.Action != OpenPositionRevalidationAction.Close)
            return;

        if (!settings.AutoTradingEnabled)
        {
            logger.LogWarning("Open-position close signal ignored because auto trading is disabled: {Reason}", verdict.Reason);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<ITradingExecutor>();
        var closeReason =
            $"Auto close: rule-based revalidation invalidated {openSide} position ({verdict.Reason})";

        try
        {
            var result = await executor.ClosePositionAsync(
                new ClosePositionRequest(Symbol, openSide, quantity, closeReason),
                cancellationToken);
            ResetOpenPositionRevalidationState();
            revalidationStore.Clear(Symbol);
            logger.LogWarning(
                "Auto-closed open position: {Side} {Qty} {Symbol} order {OrderId} (paper={Paper}) {Message}",
                openSide, quantity, Symbol, result.OrderId, result.IsPaper, result.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Auto-close rejected after open-position revalidation");
        }
    }

    private void ResetOpenPositionRevalidationState()
    {
        _nextOpenPositionRevalidationAt = DateTimeOffset.MinValue;
        _openPositionWarningCount = 0;
        _lastOpenPositionSide = null;
        _lastOpenPositionEntryPrice = null;
    }
}
