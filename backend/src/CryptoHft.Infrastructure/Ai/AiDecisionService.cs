using CryptoHft.Application.Abstractions;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Risk;
using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Ai;

// Orchestrates a full AI decision: gather multi-timeframe + derivatives + sentiment data,
// run the rule-based engine, then (for strong signals) validate with Claude and fold the
// LLM's verdict into the final decision.
public sealed class AiDecisionService(
    IMultiTimeframeProvider timeframeProvider,
    IDerivativesDataProvider derivativesProvider,
    ISentimentProvider sentimentProvider,
    IAdvancedDecisionEngine engine,
    ILlmDecisionValidator llmValidator,
    IRuntimeTradingSettingsService settingsService,
    IFuturesAccountClient accountClient,
    IOptions<AiOptions> aiOptions,
    ILogger<AiDecisionService> logger) : IAiDecisionService
{
    private readonly AiOptions _aiOptions = aiOptions.Value;

    public async Task<AdvancedDecision> AnalyzeAsync(string symbol, CancellationToken cancellationToken)
    {
        symbol = symbol.ToUpperInvariant();
        var settings = settingsService.GetRuntimeSettings();

        var timeframesTask = timeframeProvider.GetTimeframesAsync(symbol, cancellationToken);
        var derivativesTask = derivativesProvider.GetSnapshotAsync(symbol, cancellationToken);
        var sentimentTask = sentimentProvider.GetSentimentAsync(cancellationToken);
        var priceTask = timeframeProvider.GetLastPriceAsync(symbol, cancellationToken);

        await Task.WhenAll(timeframesTask, derivativesTask, sentimentTask, priceTask);

        var input = new AdvancedDecisionInput(
            symbol, priceTask.Result, timeframesTask.Result, derivativesTask.Result, sentimentTask.Result);

        var equity = await GetEquityAsync(cancellationToken);
        var profile = new RiskProfile(
            MaxDailyLoss: settings.MaxDailyLossPercent,
            MaxConsecutiveLosses: 3,
            MaxOpenPositions: 1,
            MaxExposure: settings.MaxExposurePercent,
            RiskPerTrade: settings.RiskPerTradePercent,
            MinimumRiskReward: 2m,
            AutoTradeConfidenceThreshold: 85m);

        var decision = engine.Evaluate(input, profile, equity);

        // Hybrid: only call the LLM for actionable, high-confidence signals to control cost.
        if (decision.Confidence >= _aiOptions.LlmConfidenceThreshold && decision.Action != DecisionAction.NoTrade)
        {
            var validation = await llmValidator.ValidateAsync(decision, input, cancellationToken);
            decision = ApplyValidation(decision, validation);
        }

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
            var usdt = wallets.FirstOrDefault(w => w.Asset == "USDT");
            return usdt is { Balance: > 0 } ? usdt.Balance + usdt.CrossUnrealizedPnl : 100000m;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "equity fetch failed, using default");
            return 100000m;
        }
    }
}
