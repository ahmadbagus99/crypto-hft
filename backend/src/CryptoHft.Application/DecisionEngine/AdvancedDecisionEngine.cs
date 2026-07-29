using CryptoHft.Application.Risk;
using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.DecisionEngine;

// The rule-based brain. Computes 0-100 factor scores across technical, order-flow,
// derivatives, and sentiment dimensions, applies learned per-category adjustments,
// and produces an explainable, risk-managed decision. The LLM layer validates on top.
public sealed class AdvancedDecisionEngine : IAdvancedDecisionEngine
{
    // Fixed weights for the DIRECTIONAL categories (sum to 1.0). Adaptive multipliers
    // adjust these per category. Volatility and liquidity are deliberately absent: they
    // measure trading conditions, not direction, so they no longer vote on long/short —
    // they act as a conviction dampener instead (see ConditionDampener) and are still
    // scored for display/LLM context.
    private static readonly IReadOnlyDictionary<string, decimal> CategoryWeights = new Dictionary<string, decimal>
    {
        ["technical"] = 0.22m,
        ["structure"] = 0.17m,
        ["orderbook"] = 0.17m,
        ["derivatives"] = 0.16m,
        ["onchain"] = 0.10m,
        ["macro"] = 0.10m,
        ["sentiment"] = 0.04m,
        ["news"] = 0.04m
    };

    // Condition gauges: scored and displayed, but excluded from the directional blend
    // and from directional-accuracy learning (they never take a side).
    public static readonly IReadOnlySet<string> NonDirectionalCategories =
        new HashSet<string> { "volatility", "liquidity" };

    // The categories that vote direction and therefore learn directional accuracy.
    // FactorStats rows outside this set (e.g. component names from an older scoring
    // scheme such as "Trend"/"SmartMoney") are stale and must not enter the weight
    // normalization — their evidence would skew the multipliers of live categories.
    public static readonly IReadOnlySet<string> DirectionalCategories =
        new HashSet<string>(CategoryWeights.Keys);

    // macro and onchain are flagged dynamically only when their providers return no data.

    public AdvancedDecision Evaluate(
        AdvancedDecisionInput input, RiskProfile profile, decimal equity,
        IReadOnlyDictionary<string, FactorAdjustment>? factorAdjustments = null,
        ExecutionTuning? tuning = null,
        TradingStyleProfile? styleProfile = null)
    {
        var style = styleProfile ?? TradingStyleProfile.Intraday;
        // The learned execution tuning was collected from intraday (1h-geometry) exits;
        // a style that anchors on a different timeframe uses its own fixed baseline and
        // a neutral leverage factor instead of consuming evidence that doesn't apply.
        var exec = style.UseLearnedTuning
            ? tuning ?? ExecutionTuning.Default
            : new ExecutionTuning(style.FallbackSlAtrMultiplier, style.FallbackTpAtrMultiplier, 1m);
        var primary = GetTimeframe(input, style.PrimaryInterval)
                      ?? input.Timeframes.OrderByDescending(t => t.Candles.Count).First();
        var candles = primary.Candles;
        // SMC + order-flow run on the entry timeframe (one step below primary) for finer structure
        var smcTf = GetTimeframe(input, style.StructureInterval) ?? primary;
        var flowTf = GetTimeframe(input, "5m") ?? smcTf;
        var regime = MarketRegimeDetector.Detect(candles);

        // Per-timeframe directional votes feed the trend/momentum/structure consensus.
        // Higher timeframes carry more weight (they set the bias), lower ones time the
        // entry — a single noisy timeframe can no longer flip the technical read. The
        // style shifts where that weight sits (scalper leans on 5m/15m).
        var votes = CollectTimeframeVotes(input, style.VoteWeights);

        // Primary-timeframe ATR anchors all level/geometry analysis below.
        var atr = TechnicalIndicators.Atr(candles)[^1];
        if (atr <= 0) atr = input.LastPrice * 0.003m;

        // Level-based analyses on the primary timeframe. Their full signal records are
        // kept (not just the scores) because S/R levels and pattern targets also refine
        // the TP geometry after the directional read is settled.
        var fib = FibonacciAnalysis.Analyze(candles, atr);
        var pattern = ChartPatternDetector.Detect(candles, atr);
        var srLevels = SupportResistanceLevels.Analyze(candles, atr);

        // Internal factor components (kept for transparency + adaptive learning detail)
        var components = new List<ScoreComponent>
        {
            ScoreTrendConsensus(votes),
            ScoreMomentumConsensus(votes, candles),
            ScoreVolume(candles),
            ScorePriceAction(votes, candles, regime),
            ScoreSmartMoney(smcTf.Candles),
            new ScoreComponent("Fibonacci", fib.Score, 0, fib.Summary),
            new ScoreComponent("Pattern", pattern.Score, 0, pattern.Summary),
            new ScoreComponent("SupportResistance", srLevels.Score, 0, srLevels.Summary),
            ScoreOrderFlow(input.Derivatives, flowTf.Candles),
            ScoreDerivatives(input.Derivatives, PriceChangePercent(flowTf.Candles, lookback: 1)),
            ScoreNews(input.Sentiment),
            ScoreSocial(input.Sentiment),
            ScoreVolatility(candles)
        };
        var c = components.ToDictionary(x => x.Name, x => x.Score);

        // Roll the components up into the institutional scoring categories (0-100, bullish > 50).
        // volatility/liquidity stay in the dictionary for the dashboard and the LLM payload,
        // but carry no directional weight.
        var scores = new Dictionary<string, decimal>
        {
            ["technical"] = Avg(c["Trend"], c["Momentum"], c["Volume"]),
            // Structure blends SMC + market structure with the classical level analyses.
            // SMC and price action stay the anchors; patterns speak loudest at breakouts,
            // fib and horizontal S/R add pullback/level confluence.
            ["structure"] = c["SmartMoney"] * 0.30m + c["PriceAction"] * 0.25m
                          + c["Pattern"] * 0.20m + c["Fibonacci"] * 0.125m
                          + c["SupportResistance"] * 0.125m,
            ["orderbook"] = c["OrderFlow"],
            ["derivatives"] = c["Derivatives"],
            ["onchain"] = input.Onchain.Available ? input.Onchain.Score : 50m,
            ["macro"] = input.Macro.Available ? input.Macro.Score : 50m,
            ["sentiment"] = c["Social"],
            ["news"] = c["News"],
            ["liquidity"] = ScoreLiquidity(input.Derivatives, input.LastPrice),
            ["volatility"] = c["Volatility"]
        };

        // Apply learned adaptive multipliers (Bayesian) on top of the fixed category weights
        var weights = ApplyMultipliers(CategoryWeights, factorAdjustments);

        // Directional score D (0-100): weighted blend of category scores. A category whose
        // raw score has proven consistently anti-predictive (realized directional accuracy
        // < 40%) is folded around 50 before blending — weight scaling alone cannot repair
        // a factor that is reliably wrong-way.
        var invertedCategories = new List<string>();
        decimal weightedSum = 0, weightTotal = 0;
        foreach (var (key, w) in weights)
        {
            if (!scores.TryGetValue(key, out var s)) continue;
            if (factorAdjustments is not null
                && factorAdjustments.TryGetValue(key, out var adj) && adj.Inverted)
            {
                s = 100m - s;
                invertedCategories.Add(key);
            }
            weightedSum += s * w;
            weightTotal += w;
        }
        var directional = weightTotal == 0 ? 50m : Math.Clamp(weightedSum / weightTotal, 0m, 100m);

        // Condition dampener: extreme volatility, dead tape, or a wide spread reduce the
        // conviction of BOTH sides symmetrically instead of casting a fake directional vote.
        var atrPct = input.LastPrice == 0 ? 0m : atr / input.LastPrice * 100m;
        var spreadFraction = input.LastPrice == 0 ? 0m : input.Derivatives.BidAskSpread / input.LastPrice;
        var dampener = ConditionDampener(atrPct, spreadFraction);
        directional = 50m + (directional - 50m) * dampener;

        // Anti-chasing dampener: when price has already run far in the signal's own direction
        // over the recent lookback, high "confluence" mostly restates the move that already
        // happened — realized calibration shows those late entries lose most often (the 60-70
        // confidence bucket wins materially less than 50-60). Conviction is pulled toward
        // neutral so a late signal no longer clears the entry threshold on a move it missed.
        var recentMoveAtr = RecentMoveInAtr(candles, atr);
        var chase = ChasingDampener(directional, recentMoveAtr);
        directional = 50m + (directional - 50m) * chase;

        // Symmetric buy/sell/hold confidences derived from the directional score.
        var confidenceBuy = directional;
        var confidenceSell = 100m - directional;
        var confidenceHold = Math.Clamp(100m - Math.Abs(directional - 50m) * 2m, 0m, 100m);

        var action = ToAction(directional);
        var isBuy = action is DecisionAction.WeakBuy or DecisionAction.Buy or DecisionAction.StrongBuy;
        var isSell = action is DecisionAction.WeakSell or DecisionAction.Sell or DecisionAction.StrongSell;

        // Conviction of the recommended side — this is the value all gates compare to the threshold.
        var confidence = isBuy ? confidenceBuy : isSell ? confidenceSell : confidenceHold;

        var entry = input.LastPrice;

        // SL/TP geometry: ATR multipliers learned per regime from realized exits
        // (ExecutionTuningPolicy); defaults reproduce the original fixed 2x/4x.
        decimal stopLoss, takeProfit;
        if (isSell)
        {
            stopLoss = entry + atr * exec.SlAtrMultiplier;
            takeProfit = entry - atr * exec.TpAtrMultiplier;
        }
        else
        {
            stopLoss = entry - atr * exec.SlAtrMultiplier;
            takeProfit = entry + atr * exec.TpAtrMultiplier;
        }

        // Volume profile (primary TF, ~10 days): POC/VAH/VAL + HVN/LVN refine the TP/SL
        // candidates — never the direction. A target beyond a high-volume wall is pulled
        // in front of it; a stop parked in a thin LVN is tucked behind the nearest HVN
        // shelf (dollar risk is constant, qty shrinks). Applied BEFORE qty/RR so sizing
        // follows the final geometry. Realized TP-hit-rate (ExecutionStats) is the judge.
        var volumeLevels = VolumeProfile.Build(candles);
        var profileNotes = new List<string>();
        var profileCautions = new List<string>();
        if (volumeLevels is not null)
        {
            var (snappedTp, tpNote) = VolumeProfile.SnapTakeProfit(volumeLevels, !isSell, entry, takeProfit, atr);
            if (snappedTp is decimal newTp) takeProfit = newTp;
            if (tpNote is not null) { profileNotes.Add(tpNote); if (snappedTp is null) profileCautions.Add(tpNote); }

            var (adjustedSl, slNote) = VolumeProfile.AdjustStopLoss(volumeLevels, !isSell, entry, stopLoss, atr);
            if (adjustedSl is decimal newSl) stopLoss = newSl;
            if (slNote is not null) { profileNotes.Add(slNote); if (adjustedSl is null) profileCautions.Add(slNote); }

            if (VolumeProfile.ConfluenceNote(volumeLevels, !isSell, entry, atr) is string confluence)
                profileNotes.Add(confluence);
        }

        // Horizontal S/R refinement after the volume-profile pass: a TP parked beyond a
        // multiply-tested wall is pulled to the near side of it (same 60% reward guardrail).
        var (srTp, srTpNote) = SupportResistanceLevels.SnapTakeProfit(srLevels, !isSell, entry, takeProfit, atr);
        if (srTp is decimal srSnapped) takeProfit = srSnapped;
        if (srTpNote is not null) { profileNotes.Add(srTpNote); if (srTp is null) profileCautions.Add(srTpNote); }
        var volumeProfileNote = volumeLevels is null
            ? ""
            : profileNotes.Count == 0 ? volumeLevels.Summary : $"{volumeLevels.Summary}; {string.Join("; ", profileNotes)}";

        var riskDistance = Math.Abs(entry - stopLoss);
        var rewardDistance = Math.Abs(takeProfit - entry);
        var riskReward = riskDistance == 0 ? 0 : rewardDistance / riskDistance;

        // Position sizing from risk-per-trade budget. Keep 6-dp precision so a small budget is
        // not prematurely rounded to zero — the AI multiplier scales this and the exchange rule
        // validator raises the final qty up to the venue minimum before the order is placed.
        var riskBudget = equity * profile.RiskPerTrade;
        var quantity = riskDistance <= 0 ? 0 : Math.Round(riskBudget / riskDistance, 6);
        // Confidence tier sets the leverage baseline; the learned factor (realized winrate
        // per regime, clamped 0.5-1.2x) scales it. Hard cap stays at 20x.
        var baseLeverage = confidence >= 90 ? 10 : confidence >= 80 ? 5 : 3;
        var leverage = Math.Clamp((int)Math.Round(baseLeverage * exec.LeverageFactor), 1, 20);

        // Probability of success: blend of confidence and multi-timeframe trend agreement
        var mtfAgreement = MultiTimeframeAgreement(votes, isBuy);
        var probability = Math.Clamp(confidence * 0.7m + mtfAgreement * 30m, 0m, 100m);

        // Entry gate. Per design, confidence is the SOLE hard gate that opens an order: an
        // actionable side whose conviction clears the threshold trades. The remaining quality
        // checks (RR, trend, funding, spread, scheduled events) are advisory "cautions" — they
        // never block; they are surfaced to the AI validator, which defensively downsizes
        // leverage and quantity when the backdrop is weak.
        var reasons = new List<string>();
        var noTradeReasons = new List<string>();
        var cautionReasons = new List<string>();

        var trendAligned = isBuy ? mtfAgreement >= 0.5m : isSell ? mtfAgreement >= 0.5m : false;
        if (!isBuy && !isSell) noTradeReasons.Add("Signal is neutral (Hold)");
        if (confidence < profile.AutoTradeConfidenceThreshold) noTradeReasons.Add($"Confidence {confidence:F0} below threshold {profile.AutoTradeConfidenceThreshold:F0}");

        if (riskReward < profile.MinimumRiskReward) cautionReasons.Add($"Risk/reward {riskReward:F2} below preferred {profile.MinimumRiskReward:F2}");
        if (!trendAligned && (isBuy || isSell))
            cautionReasons.Add($"Higher timeframe trend not aligned [trend votes: {VoteDetail(votes, v => v.Trend)}]");
        if (Math.Abs(input.Derivatives.FundingRate) > 0.0010m) cautionReasons.Add("Funding rate unhealthy (crowded)");
        if (input.Derivatives.BidAskSpread > entry * 0.0005m) cautionReasons.Add("Spread too wide / thin liquidity");
        if (dampener < 1m) cautionReasons.Add($"Trading conditions degraded — conviction dampened to {dampener:P0} (ATR {atrPct:F2}%, spread {spreadFraction:P3})");
        if (chase < 1m) cautionReasons.Add($"Late entry — price already moved {Math.Abs(recentMoveAtr):F1}x ATR with the signal over the last {ChaseLookbackCandles} candles; conviction dampened to {chase:P0}");
        if (input.ActiveEventWindow is string eventLabel)
            cautionReasons.Add(eventLabel);
        // Entering straight into a tested wall leaves little room before supply/demand
        // pushes back — advisory only, the validator downsizes on it.
        if (isBuy && srLevels.NearestResistance is { } wallAbove && wallAbove.Price - entry < atr)
            cautionReasons.Add($"Resistance {wallAbove.Price:F0} ({wallAbove.Touches} touches) less than 1 ATR overhead");
        if (isSell && srLevels.NearestSupport is { } wallBelow && entry - wallBelow.Price < atr)
            cautionReasons.Add($"Support {wallBelow.Price:F0} ({wallBelow.Touches} touches) less than 1 ATR below");
        cautionReasons.AddRange(profileCautions);

        var shouldTrade = noTradeReasons.Count == 0;

        // Style banner first (which lens produced this read), then category scores
        // ordered by weight, then the detailed factor breakdown.
        reasons.Add($"style {style.Name}: primary TF {primary.Interval}, SL {exec.SlAtrMultiplier:0.#}x / TP {exec.TpAtrMultiplier:0.#}x ATR");
        foreach (var (name, score) in scores.OrderByDescending(kv => weights.GetValueOrDefault(kv.Key, 0m)))
        {
            var note = name switch
            {
                "macro" => input.Macro.Available ? $" [{input.Macro.Summary}]" : " [no data source — neutral]",
                "onchain" => input.Onchain.Available ? $" [{input.Onchain.Summary}]" : " [no data source — neutral]",
                _ when NonDirectionalCategories.Contains(name) => " [condition gauge — no directional vote]",
                _ => ""
            };
            // The inverted tag applies to any directional category — macro/onchain included —
            // so it is appended on top of their summary note instead of being an unreachable
            // switch arm behind them.
            if (invertedCategories.Contains(name))
                note += " [inverted by learning — historically anti-predictive]";
            reasons.Add($"{name} ({score:F0}, w={weights.GetValueOrDefault(name, 0m):P0}){note}");
        }
        foreach (var comp in components)
            reasons.Add($"  · {comp.Name} ({comp.Score:F0}): {comp.Reason}");
        if (volumeProfileNote.Length > 0)
            reasons.Add($"  · VolumeProfile: {volumeProfileNote}");

        return new AdvancedDecision(
            Symbol: input.Symbol,
            Action: action,
            Confidence: Math.Round(confidence, 1),
            ConfidenceBuy: Math.Round(confidenceBuy, 1),
            ConfidenceSell: Math.Round(confidenceSell, 1),
            ConfidenceHold: Math.Round(confidenceHold, 1),
            ProbabilityOfSuccess: Math.Round(probability, 1),
            Regime: regime,
            EntryPrice: Math.Round(entry, 2),
            StopLoss: Math.Round(stopLoss, 2),
            TakeProfit: Math.Round(takeProfit, 2),
            TrailingStopPercent: Math.Round(atr / entry * 100m * 1.5m, 2),
            RiskReward: Math.Round(riskReward, 2),
            PositionSizeQuantity: quantity,
            Leverage: leverage,
            ShouldTrade: shouldTrade,
            NoTradeReason: noTradeReasons.Count == 0 ? "" : string.Join("; ", noTradeReasons),
            Cautions: cautionReasons,
            Scores: scores,
            Weights: weights,
            Components: components,
            Reasons: reasons,
            Llm: new LlmValidation(true, confidence, "", Array.Empty<string>(), false),
            Time: DateTimeOffset.UtcNow,
            VolumeProfileNote: volumeProfileNote);
    }

    private static TimeframeData? GetTimeframe(AdvancedDecisionInput input, string interval)
        => input.Timeframes.FirstOrDefault(t => t.Interval == interval);

    // Conviction compression for hostile trading conditions. 1.0 = full conviction.
    // Extreme ATR fades both sides (stop distance outruns edge), dead tape fades
    // breakout conviction, and a wide spread taxes entry+exit. Floor keeps a strong
    // consensus signal actionable even in rough tape (this never blocks — the
    // confidence threshold remains the sole gate).
    internal static decimal ConditionDampener(decimal atrPercent, decimal spreadFraction)
    {
        var dampener = 1m;
        if (atrPercent > 5m) dampener *= 0.75m;
        else if (atrPercent > 3m) dampener *= 0.85m;
        else if (atrPercent < 0.4m) dampener *= 0.92m;
        if (spreadFraction > 0.0005m) dampener *= 0.9m;
        return Math.Max(dampener, 0.65m);
    }

    // Anti-chasing thresholds (primary-TF candles, move measured in ATR multiples):
    // dampening starts once the aligned move exceeds ChaseStartAtr and bottoms out at
    // ChaseFloor by ChaseFullAtr. The floor keeps the conviction visible on the dashboard
    // while reliably pushing a late signal under the entry threshold.
    internal const int ChaseLookbackCandles = 6;
    internal const decimal ChaseStartAtr = 2.5m;
    internal const decimal ChaseFullAtr = 5.0m;
    internal const decimal ChaseFloor = 0.4m;

    // How far price travelled over the chase lookback, in ATR multiples (signed).
    internal static decimal RecentMoveInAtr(IReadOnlyList<Candle> candles, decimal atr)
    {
        if (atr <= 0 || candles.Count < 2) return 0m;
        var lookback = Math.Min(ChaseLookbackCandles, candles.Count - 1);
        var past = candles[^(lookback + 1)].Close;
        return (candles[^1].Close - past) / atr;
    }

    // Anti-chasing compression. 1.0 = untouched. Only a signal pointing the SAME way as an
    // already-extended move is faded — fading against the move (mean reversion) is not
    // chasing, and a neutral signal has no direction to chase.
    internal static decimal ChasingDampener(decimal directional, decimal recentMoveAtr)
    {
        var aligned = (directional > 50m && recentMoveAtr > 0m)
                      || (directional < 50m && recentMoveAtr < 0m);
        if (!aligned) return 1m;

        var extension = Math.Abs(recentMoveAtr);
        if (extension <= ChaseStartAtr) return 1m;
        var t = Math.Min((extension - ChaseStartAtr) / (ChaseFullAtr - ChaseStartAtr), 1m);
        return 1m - (1m - ChaseFloor) * t;
    }

    // Close-to-close move over the last `lookback` closed candles, in percent.
    private static decimal PriceChangePercent(IReadOnlyList<Candle> candles, int lookback)
    {
        if (candles.Count < lookback + 1) return 0m;
        var prev = candles[^(lookback + 1)].Close;
        return prev == 0 ? 0m : (candles[^1].Close - prev) / prev * 100m;
    }

    // Multiply fixed weights by learned multipliers, then renormalize to sum 1.
    private static IReadOnlyDictionary<string, decimal> ApplyMultipliers(
        IReadOnlyDictionary<string, decimal> baseWeights,
        IReadOnlyDictionary<string, FactorAdjustment>? adjustments)
    {
        if (adjustments is null || adjustments.Count == 0) return baseWeights;
        var adjusted = new Dictionary<string, decimal>();
        foreach (var (k, w) in baseWeights)
        {
            var m = adjustments.TryGetValue(k, out var adj) ? Math.Clamp(adj.Multiplier, 0.5m, 1.5m) : 1m;
            adjusted[k] = w * m;
        }
        var total = adjusted.Values.Sum();
        if (total <= 0) return baseWeights;
        foreach (var k in adjusted.Keys.ToList()) adjusted[k] = Math.Round(adjusted[k] / total, 4);
        return adjusted;
    }

    private static ScoreComponent ScoreSmartMoney(IReadOnlyList<Candle> candles)
    {
        var smc = SmartMoneyConcepts.Detect(candles);
        return new ScoreComponent("SmartMoney", smc.Score, 0, smc.Summary);
    }

    // ---- Multi-timeframe voting ---------------------------------------------------------
    // Each timeframe casts trend/momentum/structure votes; the consensus is the weighted
    // blend. Higher timeframes set the bias and weigh more, lower ones time the entry.
    // Missing/short timeframes simply drop out and the remaining weights renormalize.
    internal sealed record TimeframeVote(
        string Interval, decimal Weight, decimal Trend, decimal Momentum, decimal Structure);

    internal static List<TimeframeVote> CollectTimeframeVotes(
        AdvancedDecisionInput input,
        IReadOnlyList<(string Interval, decimal Weight)>? voteWeights = null)
    {
        var votes = new List<TimeframeVote>();
        foreach (var (interval, weight) in voteWeights ?? TradingStyleProfile.Intraday.VoteWeights)
        {
            var tf = GetTimeframe(input, interval);
            if (tf is null || tf.Candles.Count < 60) continue;
            var closes = tf.Candles.Select(c => c.Close).ToList();
            votes.Add(new TimeframeVote(
                interval, weight,
                TrendScoreFor(closes),
                MomentumScoreFor(tf.Candles),
                Math.Clamp(50m + TechnicalIndicators.MarketStructure(tf.Candles) * 20m, 0m, 100m)));
        }
        return votes;
    }

    private static decimal Consensus(List<TimeframeVote> votes, Func<TimeframeVote, decimal> vote)
    {
        var total = votes.Sum(v => v.Weight);
        return total == 0 ? 50m : Math.Clamp(votes.Sum(v => vote(v) * v.Weight) / total, 0m, 100m);
    }

    private static string VoteDetail(List<TimeframeVote> votes, Func<TimeframeVote, decimal> vote)
        => votes.Count == 0 ? "no timeframe data" : string.Join(" · ", votes.Select(v => $"{v.Interval} {vote(v):F0}"));

    // Weighted share of timeframes whose trend vote sits on the action's side.
    private static decimal MultiTimeframeAgreement(List<TimeframeVote> votes, bool bullish)
    {
        var total = votes.Sum(v => v.Weight);
        if (total == 0) return 0.5m;
        var agree = votes.Where(v => bullish ? v.Trend > 50m : v.Trend < 50m).Sum(v => v.Weight);
        return agree / total;
    }

    private static ScoreComponent ScoreTrendConsensus(List<TimeframeVote> votes)
        => new("Trend", Consensus(votes, v => v.Trend), 0,
            $"MTF trend votes [{VoteDetail(votes, v => v.Trend)}]");

    private static ScoreComponent ScoreMomentumConsensus(List<TimeframeVote> votes, IReadOnlyList<Candle> primaryCandles)
    {
        var closes = primaryCandles.Select(c => c.Close).ToList();
        var rsi = TechnicalIndicators.Rsi(closes)[^1];
        var (macd, signal, _) = TechnicalIndicators.Macd(closes);
        return new ScoreComponent("Momentum", Consensus(votes, v => v.Momentum), 0,
            $"MTF momentum votes [{VoteDetail(votes, v => v.Momentum)}], 1h RSI {rsi:F0}, MACD {(macd[^1] > signal[^1] ? "bullish" : "bearish")}");
    }

    // EMA-stack alignment gives the base score; EMA20/50 separation and EMA20 slope grade
    // it continuously so trend strength moves the score instead of jumping between bands.
    internal static decimal TrendScoreFor(List<decimal> closes)
    {
        var ema9 = TechnicalIndicators.Ema(closes, 9)[^1];
        var ema20Series = TechnicalIndicators.Ema(closes, 20);
        var ema20 = ema20Series[^1];
        var ema50 = TechnicalIndicators.Ema(closes, 50)[^1];
        var ema200 = closes.Count >= 200 ? TechnicalIndicators.Ema(closes, 200)[^1] : ema50;

        decimal score;
        if (ema9 > ema20 && ema20 > ema50 && ema50 > ema200) score = 82m;
        else if (ema9 < ema20 && ema20 < ema50 && ema50 < ema200) score = 18m;
        else if (ema9 > ema20 && ema20 > ema50) score = 66m;
        else if (ema9 < ema20 && ema20 < ema50) score = 34m;
        else score = 50m;

        // Separation between EMA20 and EMA50 (% of price): trend maturity/strength.
        var separationPct = ema50 == 0 ? 0m : (ema20 - ema50) / ema50 * 100m;
        score += Math.Clamp(separationPct * 8m, -8m, 8m);

        // EMA20 slope over the last 5 candles: is the trend still advancing?
        if (ema20Series.Length >= 6 && ema20Series[^6] != 0)
        {
            var slopePct = (ema20Series[^1] - ema20Series[^6]) / ema20Series[^6] * 100m;
            score += Math.Clamp(slopePct * 10m, -8m, 8m);
        }

        return Math.Clamp(score, 0, 100);
    }

    private static decimal MomentumScoreFor(IReadOnlyList<Candle> candles)
    {
        var closes = candles.Select(c => c.Close).ToList();
        var rsiSeries = TechnicalIndicators.Rsi(closes);
        var rsi = rsiSeries[^1];
        var (macd, signal, hist) = TechnicalIndicators.Macd(closes);
        var macdBull = macd[^1] > signal[^1];
        var stoch = TechnicalIndicators.StochasticK(candles);

        var score = 50m;
        if (rsi > 50 && rsi < 70) score += 15; else if (rsi >= 70) score -= 10; else if (rsi < 30) score += 5; else score -= 10;
        score += macdBull ? 15 : -15;
        if (stoch > 50 && stoch < 80) score += 10; else if (stoch <= 20) score += 5; else score -= 5;

        // MACD histogram slope: momentum accelerating (+) or fading (-) regardless of
        // which side of the signal line it sits on.
        if (hist.Length >= 2) score += hist[^1] > hist[^2] ? 5 : -5;

        // RSI/price divergence over the last 14 closed candles: momentum failing to
        // confirm a fresh price extreme is more predictive than any RSI level.
        score += DetectRsiDivergence(closes, rsiSeries) * 12m;

        return Math.Clamp(score, 0, 100);
    }

    // -1 bearish divergence, +1 bullish divergence, 0 none. Compares the price/RSI extremes
    // of the two most recent 7-candle windows; the RSI must miss its prior extreme by a
    // margin so ordinary noise never flags.
    internal static int DetectRsiDivergence(IReadOnlyList<decimal> closes, decimal[] rsiSeries)
    {
        const int window = 7;
        if (closes.Count < window * 2 || rsiSeries.Length < window * 2) return 0;

        var recentCloses = closes.Skip(closes.Count - window).ToList();
        var priorCloses = closes.Skip(closes.Count - window * 2).Take(window).ToList();
        var recentRsi = rsiSeries.Skip(rsiSeries.Length - window).ToList();
        var priorRsi = rsiSeries.Skip(rsiSeries.Length - window * 2).Take(window).ToList();

        const decimal rsiMargin = 2m;
        // Higher price high, lower RSI high -> bearish
        if (recentCloses.Max() > priorCloses.Max() && recentRsi.Max() < priorRsi.Max() - rsiMargin) return -1;
        // Lower price low, higher RSI low -> bullish
        if (recentCloses.Min() < priorCloses.Min() && recentRsi.Min() > priorRsi.Min() + rsiMargin) return 1;
        return 0;
    }

    private static ScoreComponent ScoreVolume(IReadOnlyList<Candle> candles)
    {
        var volumes = candles.Select(c => c.Volume).ToList();
        var volSma = TechnicalIndicators.Sma(volumes, 20)[^1];
        var lastVol = volumes[^1];
        var obvNow = TechnicalIndicators.Obv(candles);
        var obvPrev = TechnicalIndicators.Obv(candles.Take(candles.Count - 5).ToList());
        var obvRising = obvNow > obvPrev;
        var volExpansion = volSma == 0 ? 1m : lastVol / volSma;

        var score = 50m;
        if (volExpansion > 1.5m) score += 20; else if (volExpansion < 0.6m) score -= 15;
        score += obvRising ? 15 : -15;
        score = Math.Clamp(score, 0, 100);
        return new ScoreComponent("Volume", score, 0, $"Vol {volExpansion:F2}x avg, OBV {(obvRising ? "rising" : "falling")}");
    }

    // Market structure consensus across timeframes; the Bollinger mean-reversion nudge
    // (on the primary timeframe) only applies in a Ranging regime — inside a trend,
    // "price at the lower band" is the trend itself, and fading it fights the Trend
    // factor with a knife-catch.
    private static ScoreComponent ScorePriceAction(
        List<TimeframeVote> votes, IReadOnlyList<Candle> candles, MarketRegime regime)
    {
        var score = votes.Count == 0
            ? Math.Clamp(50m + TechnicalIndicators.MarketStructure(candles) * 20m, 0m, 100m)
            : Consensus(votes, v => v.Structure);

        var mrNote = "";
        if (regime == MarketRegime.Ranging)
        {
            var closes = candles.Select(c => c.Close).ToList();
            var (upper, _, lower) = TechnicalIndicators.BollingerBands(closes);
            var price = closes[^1];
            if (price <= lower) { score += 10; mrNote = ", at lower band (range MR long)"; }
            else if (price >= upper) { score -= 10; mrNote = ", at upper band (range MR short)"; }
        }
        score = Math.Clamp(score, 0, 100);
        return new ScoreComponent("PriceAction", score, 0,
            $"MTF structure votes [{VoteDetail(votes, v => v.Structure)}]{mrNote}");
    }

    private static ScoreComponent ScoreOrderFlow(DerivativesSnapshot d, IReadOnlyList<Candle> flowCandles)
    {
        var score = 50m + d.OrderBookImbalance * 40m;
        if (d.TakerBuySellRatio > 1.1m) score += 10; else if (d.TakerBuySellRatio < 0.9m) score -= 10;

        // Cumulative volume delta vs price over the last hour of 5m candles: aggressive
        // flow confirming the move strengthens it; price advancing against net selling
        // (absorption) is a leading reversal tell. Skipped when the candle source did not
        // supply taker volume (all zeros).
        var cvdNote = "";
        var (cvdRatio, priceMovePct, hasCvd) = ComputeCvd(flowCandles, window: 12);
        if (hasCvd)
        {
            var deltaBuys = cvdRatio > 0.05m;
            var deltaSells = cvdRatio < -0.05m;
            var priceUp = priceMovePct > 0.15m;
            var priceDown = priceMovePct < -0.15m;

            if (priceUp && deltaBuys) { score += 6; cvdNote = ", CVD confirms rally"; }
            else if (priceDown && deltaSells) { score -= 6; cvdNote = ", CVD confirms selloff"; }
            else if (priceUp && deltaSells) { score -= 10; cvdNote = ", CVD divergence: rally on net selling (absorption)"; }
            else if (priceDown && deltaBuys) { score += 10; cvdNote = ", CVD divergence: decline on net buying (accumulation)"; }
        }

        score = Math.Clamp(score, 0, 100);
        return new ScoreComponent("OrderFlow", score, 0,
            $"Book imbalance {d.OrderBookImbalance:F2}, taker buy/sell {d.TakerBuySellRatio:F2}{cvdNote}");
    }

    // Net taker delta over the window as a fraction of total volume (-1..+1), plus the price
    // move over the same window. hasData is false when taker volume is absent from the feed.
    internal static (decimal CvdRatio, decimal PriceMovePercent, bool HasData) ComputeCvd(
        IReadOnlyList<Candle> candles, int window)
    {
        if (candles.Count < window + 1) return (0m, 0m, false);
        var slice = candles.Skip(candles.Count - window).ToList();
        if (slice.All(c => c.TakerBuyVolume == 0m)) return (0m, 0m, false);

        decimal delta = 0m, total = 0m;
        foreach (var candle in slice)
        {
            delta += candle.TakerBuyVolume * 2m - candle.Volume; // buys - sells
            total += candle.Volume;
        }
        var startClose = candles[^(window + 1)].Close;
        var movePct = startClose == 0 ? 0m : (slice[^1].Close - startClose) / startClose * 100m;
        return (total == 0 ? 0m : delta / total, movePct, true);
    }

    private static ScoreComponent ScoreDerivatives(DerivativesSnapshot d, decimal priceChangePct)
    {
        var score = 50m;
        var notes = new List<string> { $"Funding {d.FundingRate * 100:F3}%" };

        // Negative funding supports longs; extreme positive funding warns of crowded longs
        if (d.FundingRate < -0.0003m) score += 12; else if (d.FundingRate > 0.0005m) score -= 12;

        // Cumulative funding over ~24h separates a persistent crowd from a single print.
        if (d.CumulativeFunding24h >= 0.0010m) { score -= 8; notes.Add($"24h funding {d.CumulativeFunding24h * 100:F3}% (longs crowded)"); }
        else if (d.CumulativeFunding24h <= -0.0003m) { score += 8; notes.Add($"24h funding {d.CumulativeFunding24h * 100:F3}% (shorts paying)"); }

        // OI x price matrix — the direction of OPEN INTEREST only means something together
        // with the price move that accompanied it.
        var oiUp = d.OpenInterestChangePercent > 1.5m;
        var oiDown = d.OpenInterestChangePercent < -1.5m;
        var priceUp = priceChangePct > 0.10m;
        var priceDown = priceChangePct < -0.10m;
        if (oiUp && priceUp) { score += 12; notes.Add($"OIΔ +{d.OpenInterestChangePercent:F1}% with price up (new longs)"); }
        else if (oiUp && priceDown) { score -= 12; notes.Add($"OIΔ +{d.OpenInterestChangePercent:F1}% with price down (new shorts)"); }
        else if (oiDown && priceUp) { score -= 5; notes.Add($"OIΔ {d.OpenInterestChangePercent:F1}% with price up (short covering — weak rally)"); }
        else if (oiDown && priceDown) { score += 5; notes.Add($"OIΔ {d.OpenInterestChangePercent:F1}% with price down (long capitulation)"); }
        else if (d.OpenInterestChangePercent > 2m) { score += 4; notes.Add($"OIΔ +{d.OpenInterestChangePercent:F1}% (price flat)"); }
        else if (d.OpenInterestChangePercent < -2m) { score -= 3; notes.Add($"OIΔ {d.OpenInterestChangePercent:F1}% (price flat)"); }

        if (d.LongShortRatio > 1.5m) score -= 8; else if (d.LongShortRatio < 0.7m) score += 8; // contrarian
        notes.Add($"L/S {d.LongShortRatio:F2}");

        // Liquidation pressure: a one-sided forced flush marks capitulation of that side —
        // fade it. Only significant notional counts; a quiet feed contributes nothing.
        var liqScore = ScoreLiquidationPressure(d.LongLiquidationNotional, d.ShortLiquidationNotional);
        if (liqScore != 0)
        {
            score += liqScore;
            notes.Add(liqScore > 0
                ? $"long flush ${d.LongLiquidationNotional / 1_000_000m:F1}M (capitulation — contrarian long)"
                : $"short squeeze ${d.ShortLiquidationNotional / 1_000_000m:F1}M (capitulation — contrarian short)");
        }

        score = Math.Clamp(score, 0, 100);
        return new ScoreComponent("Derivatives", score, 0, string.Join(", ", notes));
    }

    // Contrarian liquidation read: +10 after a dominant LONG flush (forced sellers are
    // spent), -10 after a dominant SHORT squeeze. Requires meaningful notional in the
    // window and a clearly one-sided cascade.
    internal static decimal ScoreLiquidationPressure(decimal longNotional, decimal shortNotional)
    {
        const decimal minNotional = 1_000_000m; // USD in the rolling window
        var total = longNotional + shortNotional;
        if (total < minNotional) return 0m;
        var longShare = longNotional / total;
        if (longShare >= 0.75m) return 10m;
        if (longShare <= 0.25m) return -10m;
        return 0m;
    }

    private static ScoreComponent ScoreNews(SentimentSnapshot s)
        => new("News", Math.Clamp(s.NewsScore, 0, 100), 0, $"News sentiment: {s.SentimentLabel}");

    // Crowd sentiment is momentum in the mid-band but CONTRARIAN at the extremes: euphoric
    // greed marks late positioning, panic fear marks capitulation. The provider blend is
    // capped/floored by a fold-back curve on the Fear & Greed index itself so the learning
    // layer never has to discover this domain prior on its own.
    private static ScoreComponent ScoreSocial(SentimentSnapshot s)
    {
        var score = Math.Clamp(s.SocialScore, 0, 100);
        var note = "";
        if (s.FearGreedIndex >= 75)
        {
            score = Math.Min(score, 75m - (s.FearGreedIndex - 75m) * 2m);
            note = " — extreme greed, contrarian cap";
        }
        else if (s.FearGreedIndex <= 25)
        {
            score = Math.Max(score, 25m + (25m - s.FearGreedIndex) * 2m);
            note = " — extreme fear, contrarian floor";
        }
        score = Math.Clamp(score, 0, 100);
        return new ScoreComponent("Social", score, 0, $"Fear & Greed: {s.FearGreedIndex} ({s.FearGreedLabel}){note}");
    }

    // Condition gauge (no directional vote): 100 = ideal tradability. Feeds the display
    // and the LLM payload; the directional blend uses ConditionDampener instead.
    private static ScoreComponent ScoreVolatility(IReadOnlyList<Candle> candles)
    {
        var atr = TechnicalIndicators.Atr(candles)[^1];
        var price = candles[^1].Close;
        var atrPct = price == 0 ? 0 : atr / price * 100m;
        var score = atrPct switch
        {
            > 3m => 30m,       // too volatile, risky
            < 0.4m => 45m,     // too quiet
            _ => 65m           // healthy range for trading
        };
        return new ScoreComponent("Volatility", score, 0, $"ATR {atrPct:F2}% of price (condition gauge)");
    }

    private static decimal Avg(params decimal[] values) => values.Length == 0 ? 50m : values.Average();

    // Condition gauge (no directional vote): book balance quality. A wide spread pulls the
    // score toward neutral; kept for display/LLM context only.
    private static decimal ScoreLiquidity(DerivativesSnapshot d, decimal price)
    {
        var score = 50m + d.OrderBookImbalance * 30m;
        var spreadPct = price <= 0 ? 0 : d.BidAskSpread / price;
        if (spreadPct > 0.0005m) score = 50m + (score - 50m) * 0.5m;
        return Math.Clamp(score, 0m, 100m);
    }

    // Symmetric directional bands around 50 (neutral). LONG actionable at >= 65, SHORT at <= 35.
    private static DecisionAction ToAction(decimal directional) => directional switch
    {
        >= 80 => DecisionAction.StrongBuy,
        >= 65 => DecisionAction.Buy,
        > 55 => DecisionAction.WeakBuy,
        >= 45 => DecisionAction.NoTrade, // Hold
        > 35 => DecisionAction.WeakSell,
        > 20 => DecisionAction.Sell,
        _ => DecisionAction.StrongSell
    };
}
