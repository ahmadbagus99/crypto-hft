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
        var profile = new RiskProfile(
            MaxDailyLoss: settings.MaxDailyLossPercent,
            MaxConsecutiveLosses: 3,
            MaxOpenPositions: 1,
            MaxExposure: settings.MaxExposurePercent,
            RiskPerTrade: settings.RiskPerTradePercent,
            MinimumRiskReward: 2m,
            AutoTradeConfidenceThreshold: settings.ConfidenceThreshold);

        // The trading style picks the lens: which timeframe anchors regime/geometry and
        // how the MTF votes are weighted. Read per tick, so a settings change takes
        // effect on the next scan without a restart.
        var styleProfile = TradingStyleProfile.For((TradingStyle)settings.TradingStyle);

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

    // Same directional bands the engine uses, so a blended score is classified identically
    // to a raw one and the entry gate sees one consistent scale.
    private static DecisionAction ToBlendedAction(decimal directional) => directional switch
    {
        >= 80 => DecisionAction.StrongBuy,
        >= 65 => DecisionAction.Buy,
        > 55 => DecisionAction.WeakBuy,
        >= 45 => DecisionAction.NoTrade,
        > 35 => DecisionAction.WeakSell,
        > 20 => DecisionAction.Sell,
        _ => DecisionAction.StrongSell
    };

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
    // How much of the final directional score Claude owns when direction blending is on.
    // A third is deliberate: enough that Claude can talk the engine out of a marginal setup
    // or turn it around, not enough to manufacture a trade the engine sees nothing in. The
    // engine keeps the majority because it is deterministic and auditable; Claude is not.
    internal const decimal AiDirectionWeight = 0.35m;

    // Blends Claude's conviction into the directional score and returns the resulting score.
    // adjusted_confidence is Claude's 0-100 conviction FOR THE SIDE THE ENGINE PROPOSED, so it
    // is first put back on the shared bullish scale before mixing.
    internal static decimal BlendDirection(decimal engineBullishScore, decimal adjustedConfidence, bool engineProposedLong)
    {
        var claudeBullish = engineProposedLong ? adjustedConfidence : 100m - adjustedConfidence;
        var blended = engineBullishScore * (1m - AiDirectionWeight) + claudeBullish * AiDirectionWeight;
        return Math.Clamp(blended, 0m, 100m);
    }

    private static AdvancedDecision ApplyValidation(
        AdvancedDecision d, LlmValidation v, decimal minRiskReward, bool blendDirection)
    {
        if (!v.Used || !d.ShouldTrade)
            return d with { Llm = v };

        var isBuy = d.Action is DecisionAction.WeakBuy or DecisionAction.Buy or DecisionAction.StrongBuy;

        // Direction blending: Claude stops being purely advisory and joins the directional
        // read. It can strengthen the engine's call, weaken it below the entry threshold, or
        // — when it disagrees hard enough to drag the blended score past neutral — reverse it.
        // The protective geometry is mirrored on a reversal so a flipped trade is not left
        // with a stop on the wrong side of entry.
        if (blendDirection)
        {
            var blended = BlendDirection(d.ConfidenceBuy, v.AdjustedConfidence, isBuy);
            var flipped = (blended > 50m) != isBuy;

            if (flipped)
            {
                var entry = d.EntryPrice;
                var stopDistance = Math.Abs(entry - d.StopLoss);
                var targetDistance = Math.Abs(d.TakeProfit - entry);
                var nowLong = blended > 50m;
                d = d with
                {
                    StopLoss = Math.Round(nowLong ? entry - stopDistance : entry + stopDistance, 2),
                    TakeProfit = Math.Round(nowLong ? entry + targetDistance : entry - targetDistance, 2),
                };
                isBuy = nowLong;
            }

            var action = ToBlendedAction(blended);
            var conviction = blended > 50m ? blended : 100m - blended;
            var actionable = action != DecisionAction.NoTrade;
            d = d with
            {
                Action = action,
                ConfidenceBuy = Math.Round(blended, 1),
                ConfidenceSell = Math.Round(100m - blended, 1),
                ConfidenceHold = Math.Round(Math.Clamp(100m - Math.Abs(blended - 50m) * 2m, 0m, 100m), 1),
                Confidence = Math.Round(actionable ? conviction : Math.Clamp(100m - Math.Abs(blended - 50m) * 2m, 0m, 100m), 1),
                ShouldTrade = actionable && d.ShouldTrade,
                NoTradeReason = actionable ? d.NoTradeReason : "AI + engine blend is neutral (Hold)",
                Reasons = d.Reasons.Append(
                    $"AI direction blend: engine {d.ConfidenceBuy:F1} x {1m - AiDirectionWeight:P0} + Claude {v.AdjustedConfidence:F1} x {AiDirectionWeight:P0} = {blended:F1}"
                    + (flipped ? " — SIDE REVERSED, protective levels mirrored" : "")).ToList()
            };

            if (!d.ShouldTrade) return d with { Llm = v };
        }

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
