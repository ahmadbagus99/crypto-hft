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

    public async Task<AdvancedDecision> AnalyzeRuleBasedAndLogAsync(string symbol, CancellationToken cancellationToken)
        => await AnalyzeCoreAsync(symbol, useLlm: false, logDecision: true, cancellationToken);

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

        // The trading style picks the lens: which timeframe anchors regime/geometry and
        // how the MTF votes are weighted. Read per tick, so a settings change takes
        // effect on the next scan without a restart. Resolved before the risk profile
        // because the reward:risk the style is built around belongs to the style.
        var styleProfile = TradingStyleProfile.For((TradingStyle)settings.TradingStyle);

        var profile = new RiskProfile(
            MaxDailyLoss: settings.MaxDailyLossPercent,
            MaxConsecutiveLosses: 3,
            MaxOpenPositions: 1,
            MaxExposure: settings.MaxExposurePercent,
            RiskPerTrade: settings.RiskPerTradePercent,
            MinimumRiskReward: styleProfile.MinimumRiskReward,
            AutoTradeConfidenceThreshold: settings.ConfidenceThreshold);

        // Adaptive learning: load learned weight multipliers + execution baselines
        // (SL/TP geometry, leverage factor) for the current regime — detected on the
        // style's primary timeframe so the engine and the learning lookup agree.
        var primary = input.Timeframes.FirstOrDefault(t => t.Interval == styleProfile.PrimaryInterval)
                      ?? input.Timeframes.OrderByDescending(t => t.Candles.Count).First();
        var regime = MarketRegimeDetector.Detect(primary.Candles);
        var adjustments = await adaptiveWeights.GetFactorAdjustmentsAsync(regime, cancellationToken);
        var tuning = await adaptiveWeights.GetExecutionTuningAsync(regime, cancellationToken);
        // Centres each category on its own observed distribution. Failure is non-fatal:
        // an empty set leaves the engine on the original fixed neutral of 50.
        IReadOnlyDictionary<string, decimal> baselines = new Dictionary<string, decimal>();
        try { baselines = await adaptiveWeights.GetCategoryBaselinesAsync(cancellationToken); }
        catch (Exception ex) { logger.LogDebug(ex, "category baselines unavailable"); }

        var decision = engine.Evaluate(input, profile, equity ?? 0m, adjustments, tuning, styleProfile, baselines);

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
            decision = ApplyValidation(decision, validation, profile.MinimumRiskReward, settings.AiDirectionEnabled);
        }

        // Log the decision for online evaluation (fire-and-forget against the DB)
        if (logDecision)
        {
            try { await adaptiveWeights.LogDecisionAsync(decision, cancellationToken); }
            catch (Exception ex) { logger.LogDebug(ex, "decision logging failed"); }
        }

        return decision;
    }

    // What an unconfirmed level costs in size. Half is a starting guess, not a measurement —
    // nothing in the record says how much worse an early entry actually does, because until
    // now those trades were refused rather than taken. Once enough closed positions carry
    // LlmLevelConfirmed the factor can be fitted, or the veto restored, on evidence.
    internal const decimal UnconfirmedLevelSizeFactor = 0.5m;

    // Hard safety caps for Claude's defensive resizing when it is hesitant.
    private const decimal MinSizeMultiplier = 0.1m;
    private const decimal MaxSizeMultiplier = 1.5m;
    private const int MinLeverage = 1;
    private const int MaxLeverage = 20;

    // Two modes, chosen by the "AI Ikut Menentukan Arah" setting.
    //
    // Off (default): Claude is advisory. It never blocks a trade that cleared the threshold,
    // and only sizes the execution (size / leverage / SL / TP) within hard caps.
    //
    // On (auditor): the engine still decides WHICH setups Claude sees — it is never asked
    // about a candidate the engine rejected — but the verdict then decides whether the
    // position opens. Claude reads the named levels and the candle block and either confirms
    // the engine's read or declines it. Only the engine's proposed side is ever on the table,
    // so this can refuse a trade but never invent or reverse one.
    private static AdvancedDecision ApplyValidation(
        AdvancedDecision d, LlmValidation v, decimal minRiskReward, bool auditorMode)
    {
        if (!v.Used || !d.ShouldTrade)
            return d with { Llm = v };

        var isBuy = d.Action is DecisionAction.WeakBuy or DecisionAction.Buy or DecisionAction.StrongBuy;

        // Auditor mode: the engine has already accepted this setup, and Claude now checks it
        // against the named levels and the candle block. Its verdict is binding — a refusal
        // means no position opens on this pass. That is not a rejection of the strategy: the
        // engine keeps scanning every 30 seconds and will present the next candidate, so the
        // cost of declining is one setup, not the day. Only the side the engine proposed is
        // ever considered, so nothing here can reverse a trade or move its protective levels.
        if (auditorMode && !v.Confirmed)
        {
            return d with
            {
                Action = DecisionAction.NoTrade,
                ShouldTrade = false,
                PositionSizeQuantity = 0m,
                Confidence = Math.Round(Math.Clamp(100m - Math.Abs(d.ConfidenceBuy - 50m) * 2m, 0m, 100m), 1),
                NoTradeReason = $"Claude did not confirm the {(isBuy ? "long" : "short")} — waiting for the next setup",
                Reasons = d.Reasons.Append(
                    $"AI audit: NOT CONFIRMED (conviction {v.AdjustedConfidence:F0}"
                    + (v.AlignedCount is int a && v.BlockingCount is int b ? $", aligned {a} vs blocking {b}" : "")
                    + $"). {v.Narrative}").ToList(),
                Llm = v
            };
        }

        if (auditorMode)
        {
            d = d with
            {
                Reasons = d.Reasons.Append(
                    $"AI audit: CONFIRMED (conviction {v.AdjustedConfidence:F0}"
                    + (v.AlignedCount is int a2 && v.BlockingCount is int b2 ? $", aligned {a2} vs blocking {b2}" : "")
                    + $"). {v.Narrative}").ToList()
            };
        }

        // An entry into a level that has not yet rejected price is a setup without its trigger.
        // It used to veto the trade outright, which produced no position and therefore no
        // evidence about whether the caution was even warranted — two refusals at engine
        // conviction above 80 with nothing learned from either. Priced as a haircut instead,
        // the trade happens small and the outcome is recorded, so the question becomes
        // answerable from realized results rather than assumed. Applied here rather than asked
        // of the model so the discount is deterministic and shows up in the same place every
        // time.
        var mult = v.LevelConfirmed is false
            ? v.SizeMultiplier * UnconfirmedLevelSizeFactor
            : v.SizeMultiplier;
        mult = Math.Clamp(mult, MinSizeMultiplier, MaxSizeMultiplier);
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
