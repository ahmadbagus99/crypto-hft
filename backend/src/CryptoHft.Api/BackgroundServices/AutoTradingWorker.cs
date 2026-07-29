using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Account;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Risk;
using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;

namespace CryptoHft.Api.BackgroundServices;

// The autonomous trading loop. Auto mode first confirms a rule-based signal over time,
// checks account risk, then invokes Claude once as a bounded validator before execution.
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
    private DateTimeOffset _nextOpenPositionRevalidationAt = DateTimeOffset.MinValue;
    private int _openPositionWarningCount;
    private TradeSide? _lastOpenPositionSide;
    private decimal? _lastOpenPositionEntryPrice;
    private int _lastOpenPositionCheckIntervalMinutes;
    private AutoEntryCandidate? _entryCandidate;
    private DateTimeOffset _entryCooldownUntil = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastCooldownSourceClosedAt;
    private bool _entryCooldownInitialized;
    private bool _observedOpenPosition;

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
        // (breakeven at +1R, then trail); at the configured interval a rule-based health check runs — if the
        // opposite side confirms twice (or becomes extremely strong once), close via reduce-only.
        if (openPosition is not null)
        {
            _observedOpenPosition = true;
            _entryCandidate = null;
            await trailingStopGuard.ApplyAsync(Symbol, openPosition, cancellationToken);
            await RevalidateOpenPositionAsync(settings, openPosition, cancellationToken);
            return;
        }
        var positionJustClosed = _observedOpenPosition;
        _observedOpenPosition = false;
        ResetOpenPositionRevalidationState();
        revalidationStore.Clear(Symbol);
        // Position closed: the trailing history belongs to that position only — wipe it so
        // the dashboard card starts fresh with the next position.
        trailingStopActivity.Clear(Symbol);

        var now = DateTimeOffset.UtcNow;
        // Entry timings follow the configured trading style (read per tick, so a settings
        // change applies on the next scan). Scalper compresses confirmation and cooldowns.
        var timing = AutoEntryTiming.For((TradingStyle)settings.TradingStyle);
        if (!_entryCooldownInitialized || positionJustClosed)
            await RefreshEntryCooldownAsync(positionJustClosed, timing, now, cancellationToken);

        // Manual mode keeps the existing dashboard analysis behavior. Auto mode starts with a
        // rule-only scan so Claude is called only after the signal persists long enough.
        if (!settings.AutoTradingEnabled)
        {
            _entryCandidate = null;
            var manualDecision = await aiDecisionService.AnalyzeAsync(Symbol, cancellationToken);
            decisionStore.Set(Symbol, manualDecision);
            LogDecision(manualDecision);
            return;
        }

        var scanDecision = await aiDecisionService.AnalyzeRuleBasedAndLogAsync(Symbol, cancellationToken);
        decisionStore.Set(Symbol, scanDecision);
        LogDecision(scanDecision);

        if (now < _entryCooldownUntil)
        {
            _entryCandidate = null;
            var cooldownReason = $"entry cooldown active until {_entryCooldownUntil:u}";
            decisionStore.Set(Symbol, BlockDecision(scanDecision, cooldownReason));
            logger.LogInformation("Auto-entry blocked: {Reason}", cooldownReason);
            return;
        }

        var confirmation = AutoEntryPolicy.EvaluateConfirmation(
            scanDecision,
            settings.ConfidenceThreshold,
            _entryCandidate,
            now,
            timing);
        _entryCandidate = confirmation.Candidate;
        if (confirmation.Action != AutoEntryConfirmationAction.Ready || confirmation.Side is null)
        {
            if (confirmation.Action == AutoEntryConfirmationAction.Wait)
            {
                decisionStore.Set(Symbol, BlockDecision(scanDecision, confirmation.Reason));
                logger.LogInformation("Auto-entry confirmation: {Reason}", confirmation.Reason);
            }
            return;
        }

        // Account pauses are checked before Claude to avoid spending tokens on an order that
        // cannot execute. Exposure is still evaluated later against the final quantity.
        var accountStatus = await riskGate.GetStatusAsync(cancellationToken);
        if (!accountStatus.TradingAllowed)
        {
            _entryCandidate = null;
            decisionStore.Set(Symbol, BlockDecision(scanDecision, accountStatus.Reason));
            logger.LogWarning("Auto-entry blocked before Claude: {Reason}", accountStatus.Reason);
            return;
        }

        var decision = await aiDecisionService.AnalyzeAsync(Symbol, cancellationToken);
        var llmVerdict = AutoEntryPolicy.EvaluateLlm(
            decision,
            confirmation.Side.Value,
            settings.ConfidenceThreshold);
        if (!llmVerdict.Allowed)
        {
            _entryCandidate = null;
            decisionStore.Set(Symbol, BlockDecision(decision, llmVerdict.Reason));
            logger.LogWarning("Auto-entry blocked after Claude validation: {Reason}", llmVerdict.Reason);
            return;
        }

        decisionStore.Set(Symbol, decision);
        logger.LogInformation("Auto-entry validated: {Reason}", llmVerdict.Reason);

        var sizing = AutoPositionSizingPolicy.Resolve(
            settings.AutoSizingMode,
            decision.PositionSizeQuantity,
            decision.Leverage,
            decision.EntryPrice,
            settings.TargetMarginUsdt,
            settings.TargetLeverage,
            decision.Llm.Used ? decision.Llm.SizeMultiplier : null);

        if (sizing.Quantity <= 0)
        {
            logger.LogWarning("Auto-trade skipped: computed quantity is zero");
            return;
        }
        if (settings.AutoSizingMode == AutoPositionSizingPolicy.TargetMarginLeverageMode)
        {
            logger.LogInformation(
                "Auto sizing override: qty {DecisionQty} -> {Qty}, leverage {DecisionLev}x -> {Lev}x ({Reason})",
                decision.PositionSizeQuantity, sizing.Quantity, decision.Leverage, sizing.Leverage, sizing.Reason);
        }

        // Account-level risk gate: daily loss / consecutive losses pause trading; the exposure
        // cap only trims the quantity. The signal itself is never re-judged here — confidence
        // above the threshold already decided that the position opens.
        var verdict = await riskGate.EvaluateAsync(
            Symbol, sizing.Quantity, decision.EntryPrice, sizing.Leverage, cancellationToken);
        if (!verdict.Allowed)
        {
            logger.LogWarning("Auto-trade blocked by risk gate: {Reason}", verdict.Reason);
            return;
        }
        var quantity = verdict.AdjustedQuantity ?? sizing.Quantity;
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
            Leverage: sizing.Leverage,
            ReduceOnly: false,
            TradingMode.Auto,
            reason);

        try
        {
            var result = await executor.PlaceAsync(order, cancellationToken);
            _entryCandidate = null;
            logger.LogInformation("Auto-trade placed: {Side} {Qty} {Symbol} order {OrderId} (paper={Paper}) {Message}",
                side, quantity, Symbol, result.OrderId, result.IsPaper, result.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Auto-trade order rejected");
        }
    }

    private async Task RefreshEntryCooldownAsync(
        bool positionJustClosed,
        AutoEntryTiming timing,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _entryCooldownInitialized = true;

        // ObserveAsync persists the close before this method runs. Keep a conservative fallback
        // if that write or its close-reason classification fails, so a database problem cannot
        // cause an immediate re-entry. Unknown exits use the same stop-loss window.
        if (positionJustClosed)
        {
            _entryCandidate = null;
            _entryCooldownUntil = now.Add(timing.StopLossCooldown);
        }

        var latest = await positionHistoryService.GetLatestClosedAsync(Symbol, cancellationToken);
        if (latest is null)
            return;

        if (positionJustClosed && latest.ClosedAt < now.AddMinutes(-2))
        {
            logger.LogWarning(
                "Latest Position History row ({ClosedAt:u}) does not match the close just observed; " +
                "keeping conservative entry cooldown until {ResumeAt:u}",
                latest.ClosedAt, _entryCooldownUntil);
            return;
        }

        if (latest.ClosedAt == _lastCooldownSourceClosedAt)
            return;

        _lastCooldownSourceClosedAt = latest.ClosedAt;
        _entryCooldownUntil = latest.ClosedAt.Add(AutoEntryPolicy.CooldownFor(latest.CloseReason, timing));
        _entryCandidate = null;
        logger.LogInformation(
            "Entry cooldown updated from {CloseReason} close at {ClosedAt:u}; entries resume at {ResumeAt:u}",
            latest.CloseReason, latest.ClosedAt, _entryCooldownUntil);
    }

    private static AdvancedDecision BlockDecision(AdvancedDecision decision, string reason)
        => decision with
        {
            ShouldTrade = false,
            NoTradeReason = decision.NoTradeReason.Length == 0
                ? reason
                : $"{decision.NoTradeReason}; {reason}"
        };

    private void LogDecision(AdvancedDecision decision)
    {
        logger.LogInformation(
            "AI decision: {Action} conf={Confidence} regime={Regime} shouldTrade={ShouldTrade} {Reason}",
            decision.Action, decision.Confidence, decision.Regime, decision.ShouldTrade, decision.NoTradeReason);
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
        var checkIntervalMinutes = Math.Clamp(settings.PositionCheckIntervalMinutes <= 0 ? 30 : settings.PositionCheckIntervalMinutes, 5, 120);
        var checkInterval = TimeSpan.FromMinutes(checkIntervalMinutes);

        if (_lastOpenPositionSide != openSide
            || _lastOpenPositionEntryPrice != position.EntryPrice
            || _nextOpenPositionRevalidationAt == DateTimeOffset.MinValue)
        {
            _lastOpenPositionSide = openSide;
            _lastOpenPositionEntryPrice = position.EntryPrice;
            _lastOpenPositionCheckIntervalMinutes = checkIntervalMinutes;
            _openPositionWarningCount = 0;
            _nextOpenPositionRevalidationAt = now.Add(checkInterval);
            revalidationStore.StartOrUpdatePosition(Symbol, openSide, quantity, position.EntryPrice, _nextOpenPositionRevalidationAt);
            logger.LogInformation(
                "Position open ({Side} {Qty} {Symbol}); entry analysis paused, first rule-based revalidation at {NextCheck:u} (interval {Interval}m)",
                openSide, quantity, Symbol, _nextOpenPositionRevalidationAt, checkIntervalMinutes);
            return;
        }

        if (_lastOpenPositionCheckIntervalMinutes != checkIntervalMinutes)
        {
            _lastOpenPositionCheckIntervalMinutes = checkIntervalMinutes;
            _nextOpenPositionRevalidationAt = now.Add(checkInterval);
            revalidationStore.SetNextCheck(Symbol, _nextOpenPositionRevalidationAt);
            logger.LogInformation(
                "Open-position revalidation interval changed to {Interval}m; next check rescheduled at {NextCheck:u}",
                checkIntervalMinutes, _nextOpenPositionRevalidationAt);
            return;
        }

        if (now < _nextOpenPositionRevalidationAt)
        {
            logger.LogDebug(
                "Analysis paused: position open ({Side} {Qty} {Symbol}); next rule-based revalidation at {NextCheck:u}",
                openSide, quantity, Symbol, _nextOpenPositionRevalidationAt);
            return;
        }

        _nextOpenPositionRevalidationAt = now.Add(checkInterval);
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
        _lastOpenPositionCheckIntervalMinutes = 0;
    }
}
