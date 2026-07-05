using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Account;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Risk;
using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace CryptoHft.Infrastructure.Ai;

// Orchestrates a full AI decision: gather multi-timeframe + derivatives + sentiment data,
// run the rule-based engine, then (for strong signals) validate with Claude and fold the
// LLM's verdict into the final decision.
public sealed class AiDecisionService(
    IMultiTimeframeProvider timeframeProvider,
    IDerivativesDataProvider derivativesProvider,
    ISentimentProvider sentimentProvider,
    IMacroDataProvider macroProvider,
    IOnchainDataProvider onchainProvider,
    IEconomicCalendarProvider calendarProvider,
    IAdvancedDecisionEngine engine,
    ILlmDecisionValidator llmValidator,
    IAdaptiveWeightService adaptiveWeights,
    IRuntimeTradingSettingsService settingsService,
    IFuturesAccountClient accountClient,
    ILogger<AiDecisionService> logger) : IAiDecisionService
{
    public async Task<AdvancedDecision> AnalyzeAsync(string symbol, CancellationToken cancellationToken)
        => await AnalyzeCoreAsync(symbol, useLlm: true, logDecision: true, cancellationToken);

    public async Task<AdvancedDecision> AnalyzeRuleBasedAsync(string symbol, CancellationToken cancellationToken)
        => await AnalyzeCoreAsync(symbol, useLlm: false, logDecision: false, cancellationToken);

    private async Task<AdvancedDecision> AnalyzeCoreAsync(
        string symbol,
        bool useLlm,
        bool logDecision,
        CancellationToken cancellationToken)
    {
        symbol = symbol.ToUpperInvariant();
        var settings = settingsService.GetRuntimeSettings();

        var timeframesTask = timeframeProvider.GetTimeframesAsync(symbol, cancellationToken);
        var derivativesTask = derivativesProvider.GetSnapshotAsync(symbol, cancellationToken);
        var sentimentTask = sentimentProvider.GetSentimentAsync(cancellationToken);
        var macroTask = macroProvider.GetSnapshotAsync(cancellationToken);
        var onchainTask = onchainProvider.GetSnapshotAsync(cancellationToken);
        var calendarTask = calendarProvider.GetActiveEventWindowAsync(cancellationToken);
        var priceTask = timeframeProvider.GetLastPriceAsync(symbol, cancellationToken);

        await Task.WhenAll(timeframesTask, derivativesTask, sentimentTask, macroTask, onchainTask, calendarTask, priceTask);

        var input = new AdvancedDecisionInput(
            symbol, priceTask.Result, timeframesTask.Result, derivativesTask.Result, sentimentTask.Result,
            macroTask.Result, onchainTask.Result, calendarTask.Result);

        var equity = await GetEquityAsync(cancellationToken);
        var profile = new RiskProfile(
            MaxDailyLoss: settings.MaxDailyLossPercent,
            MaxConsecutiveLosses: 3,
            MaxOpenPositions: 1,
            MaxExposure: settings.MaxExposurePercent,
            RiskPerTrade: settings.RiskPerTradePercent,
            MinimumRiskReward: 2m,
            AutoTradeConfidenceThreshold: settings.ConfidenceThreshold);

        // Adaptive learning: load learned weight multipliers + execution baselines
        // (SL/TP geometry, leverage factor) for the current regime
        var primary = input.Timeframes.FirstOrDefault(t => t.Interval == "1h")
                      ?? input.Timeframes.OrderByDescending(t => t.Candles.Count).First();
        var regime = MarketRegimeDetector.Detect(primary.Candles);
        var adjustments = await adaptiveWeights.GetFactorAdjustmentsAsync(regime, cancellationToken);
        var tuning = await adaptiveWeights.GetExecutionTuningAsync(regime, cancellationToken);

        var decision = engine.Evaluate(input, profile, equity ?? 0m, adjustments, tuning);

        // Live mode with unknown equity: sizing against a guessed balance is dangerous, so the
        // trade is blocked for this tick (analysis still runs for the dashboard).
        if (equity is null)
        {
            var reason = "Account equity unavailable — sizing blocked (fail-safe)";
            decision = decision with
            {
                ShouldTrade = false,
                PositionSizeQuantity = 0m,
                NoTradeReason = decision.NoTradeReason.Length == 0
                    ? reason
                    : $"{decision.NoTradeReason}; {reason}"
            };
        }

        // Hybrid: only call the LLM for actionable signals that could open an order, so the
        // validation gate tracks the same configurable confidence threshold (cost control).
        // Skipped when equity is unknown: nothing will execute, so the call would be wasted spend.
        if (useLlm && equity is not null && decision.Confidence >= settings.ConfidenceThreshold && decision.Action != DecisionAction.NoTrade)
        {
            var validation = await llmValidator.ValidateAsync(decision, input, cancellationToken);
            decision = ApplyValidation(decision, validation, profile.MinimumRiskReward);
        }

        // Log the decision for online evaluation (fire-and-forget against the DB)
        if (logDecision)
        {
            try { await adaptiveWeights.LogDecisionAsync(decision, cancellationToken); }
            catch (Exception ex) { logger.LogDebug(ex, "decision logging failed"); }
        }

        return decision;
    }

    // Hard safety caps for Claude's defensive resizing when it is hesitant.
    private const decimal MinSizeMultiplier = 0.1m;
    private const decimal MaxSizeMultiplier = 1.5m;
    private const int MinLeverage = 1;
    private const int MaxLeverage = 20;

    // Claude is advisory only: it never blocks a trade that clears the confidence threshold.
    // It ALWAYS sizes the execution (size / leverage / SL / TP) within hard caps — regardless of
    // the confirmed flag, which is now just a display signal for "clean backdrop vs hesitant".
    // Decoupling sizing from confirmation keeps the narrative ("trading at 0.65x") consistent with
    // what actually gets placed. Its narrative + risks are always attached for the dashboard/DB log.
    private static AdvancedDecision ApplyValidation(AdvancedDecision d, LlmValidation v, decimal minRiskReward)
    {
        if (!v.Used || !d.ShouldTrade)
            return d with { Llm = v };

        var isBuy = d.Action is DecisionAction.WeakBuy or DecisionAction.Buy or DecisionAction.StrongBuy;

        // Size: clamp the multiplier, then scale the baseline qty. Keep 6-dp precision (matching the
        // engine baseline) so a small budget is not re-zeroed here — the exchange rule validator
        // raises the final qty up to the venue minimum before placement.
        var mult = Math.Clamp(v.SizeMultiplier, MinSizeMultiplier, MaxSizeMultiplier);
        var qty = Math.Round(d.PositionSizeQuantity * mult, 6);

        // Leverage: clamp to the hard range, else keep baseline.
        var leverage = v.Leverage is int l ? Math.Clamp(l, MinLeverage, MaxLeverage) : d.Leverage;

        // SL/TP: accept Claude's pair only if both sit on the correct side of entry and the
        // resulting risk/reward still clears the minimum; otherwise keep the rule baseline.
        var stopLoss = d.StopLoss;
        var takeProfit = d.TakeProfit;
        if (v.StopLoss is decimal cSl && v.TakeProfit is decimal cTp)
        {
            var slOk = isBuy ? cSl < d.EntryPrice : cSl > d.EntryPrice;
            var tpOk = isBuy ? cTp > d.EntryPrice : cTp < d.EntryPrice;
            var risk = Math.Abs(d.EntryPrice - cSl);
            var reward = Math.Abs(cTp - d.EntryPrice);
            var rr = risk <= 0 ? 0 : reward / risk;
            if (slOk && tpOk && rr >= minRiskReward)
            {
                stopLoss = Math.Round(cSl, 2);
                takeProfit = Math.Round(cTp, 2);
            }
        }

        var finalRisk = Math.Abs(d.EntryPrice - stopLoss);
        var finalReward = Math.Abs(takeProfit - d.EntryPrice);
        var riskReward = finalRisk <= 0 ? d.RiskReward : Math.Round(finalReward / finalRisk, 2);

        return d with
        {
            PositionSizeQuantity = qty,
            Leverage = leverage,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            RiskReward = riskReward,
            Llm = v
        };
    }

    // Paper mode may run without exchange credentials, so it falls back to a nominal paper
    // equity. Live mode must never size against a guessed balance: a failed/zero equity read
    // returns null and the caller blocks the trade for this tick.
    private const decimal PaperFallbackEquity = 100000m;

    private async Task<decimal?> GetEquityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var wallets = await accountClient.GetWalletBalancesAsync(cancellationToken);
            var equity = wallets.UsdEquity();
            if (equity > 0) return equity;
            logger.LogWarning("equity read returned {Equity} — treating as unavailable", equity);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "equity fetch failed");
        }
        return settingsService.GetRuntimeSettings().PaperTradingOnly ? PaperFallbackEquity : null;
    }
}
