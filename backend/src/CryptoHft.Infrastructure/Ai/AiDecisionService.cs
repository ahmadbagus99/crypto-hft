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
            decision = ApplyValidation(decision, validation);
        }

        // Log the decision for online evaluation (fire-and-forget against the DB)
        try { await adaptiveWeights.LogDecisionAsync(decision, cancellationToken); }
        catch (Exception ex) { logger.LogDebug(ex, "decision logging failed"); }

        return decision;
    }

    private static AdvancedDecision ApplyValidation(AdvancedDecision d, LlmValidation v)
    {
        if (!v.Used) return d with { Llm = v };

        var shouldTrade = d.ShouldTrade && v.Confirmed;
        var noTradeReason = d.NoTradeReason;
        if (!v.Confirmed)
            noTradeReason = string.IsNullOrEmpty(noTradeReason) ? "LLM vetoed the signal" : $"{noTradeReason}; LLM vetoed";

        // Blend confidence: average rule-based and LLM-adjusted
        var blended = Math.Round((d.Confidence + v.AdjustedConfidence) / 2m, 1);

        return d with
        {
            Confidence = blended,
            ShouldTrade = shouldTrade,
            NoTradeReason = noTradeReason,
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
