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
    IAdvancedDecisionEngine engine,
    ILlmDecisionValidator llmValidator,
    IAdaptiveWeightService adaptiveWeights,
    IRuntimeTradingSettingsService settingsService,
    IFuturesAccountClient accountClient,
    ILogger<AiDecisionService> logger) : IAiDecisionService
{
    public async Task<AdvancedDecision> AnalyzeAsync(string symbol, CancellationToken cancellationToken)
    {
        symbol = symbol.ToUpperInvariant();
        var settings = settingsService.GetRuntimeSettings();

        var timeframesTask = timeframeProvider.GetTimeframesAsync(symbol, cancellationToken);
        var derivativesTask = derivativesProvider.GetSnapshotAsync(symbol, cancellationToken);
        var sentimentTask = sentimentProvider.GetSentimentAsync(cancellationToken);
        var macroTask = macroProvider.GetSnapshotAsync(cancellationToken);
        var onchainTask = onchainProvider.GetSnapshotAsync(cancellationToken);
        var priceTask = timeframeProvider.GetLastPriceAsync(symbol, cancellationToken);

        await Task.WhenAll(timeframesTask, derivativesTask, sentimentTask, macroTask, onchainTask, priceTask);

        var input = new AdvancedDecisionInput(
            symbol, priceTask.Result, timeframesTask.Result, derivativesTask.Result, sentimentTask.Result,
            macroTask.Result, onchainTask.Result);

        var equity = await GetEquityAsync(cancellationToken);
        var profile = new RiskProfile(
            MaxDailyLoss: settings.MaxDailyLossPercent,
            MaxConsecutiveLosses: 3,
            MaxOpenPositions: 1,
            MaxExposure: settings.MaxExposurePercent,
            RiskPerTrade: settings.RiskPerTradePercent,
            MinimumRiskReward: 2m,
            AutoTradeConfidenceThreshold: settings.ConfidenceThreshold);

        // Adaptive learning: load learned weight multipliers for the current regime
        var primary = input.Timeframes.FirstOrDefault(t => t.Interval == "1h")
                      ?? input.Timeframes.OrderByDescending(t => t.Candles.Count).First();
        var regime = MarketRegimeDetector.Detect(primary.Candles);
        var multipliers = await adaptiveWeights.GetMultipliersAsync(regime, cancellationToken);

        var decision = engine.Evaluate(input, profile, equity, multipliers);

        // Hybrid: only call the LLM for actionable signals that could open an order, so the
        // validation gate tracks the same configurable confidence threshold (cost control).
        if (decision.Confidence >= settings.ConfidenceThreshold && decision.Action != DecisionAction.NoTrade)
        {
            var validation = await llmValidator.ValidateAsync(decision, input, cancellationToken);
            decision = ApplyValidation(decision, validation, profile.MinimumRiskReward);
        }

        // Log the decision for online evaluation (fire-and-forget against the DB)
        try { await adaptiveWeights.LogDecisionAsync(decision, cancellationToken); }
        catch (Exception ex) { logger.LogDebug(ex, "decision logging failed"); }

        return decision;
    }

    // Hard safety caps for Claude's defensive resizing when it is hesitant.
    private const decimal MinSizeMultiplier = 0.1m;
    private const decimal MaxSizeMultiplier = 1.5m;
    private const int MinLeverage = 1;
    private const int MaxLeverage = 10;

    // Claude is advisory only: it never blocks a trade that clears the confidence threshold.
    // When it does NOT fully confirm, it may resize the trade defensively (size / leverage /
    // SL / TP) within hard caps; on full confirmation the rule-engine baseline is kept.
    // Its narrative + risks are always attached for the dashboard and DB learning log.
    private static AdvancedDecision ApplyValidation(AdvancedDecision d, LlmValidation v, decimal minRiskReward)
    {
        if (!v.Used || !d.ShouldTrade || v.Confirmed)
            return d with { Llm = v };

        var isBuy = d.Action is DecisionAction.WeakBuy or DecisionAction.Buy or DecisionAction.StrongBuy;

        // Size: clamp the multiplier, then scale the baseline qty.
        var mult = Math.Clamp(v.SizeMultiplier, MinSizeMultiplier, MaxSizeMultiplier);
        var qty = Math.Round(d.PositionSizeQuantity * mult, 3);

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

    private async Task<decimal> GetEquityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var wallets = await accountClient.GetWalletBalancesAsync(cancellationToken);
            var equity = wallets.UsdEquity();
            return equity > 0 ? equity : 100000m;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "equity fetch failed, using default");
            return 100000m;
        }
    }
}
