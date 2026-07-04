using CryptoHft.Application.Risk;
using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.DecisionEngine;

// The rule-based brain. Computes 0-100 factor scores across technical, order-flow,
// derivatives, and sentiment dimensions, applies regime-dependent dynamic weighting,
// and produces an explainable, risk-managed decision. The LLM layer validates on top.
public sealed class AdvancedDecisionEngine : IAdvancedDecisionEngine
{
    // Fixed category weights (institutional spec). Sum to 1.0. Adaptive multipliers
    // adjust these per category; regime no longer selects the weights but still drives
    // SL/TP/leverage and is reported for context.
    private static readonly IReadOnlyDictionary<string, decimal> CategoryWeights = new Dictionary<string, decimal>
    {
        ["technical"] = 0.20m,
        ["structure"] = 0.15m,
        ["orderbook"] = 0.15m,
        ["derivatives"] = 0.15m,
        ["onchain"] = 0.10m,
        ["macro"] = 0.10m,
        ["sentiment"] = 0.05m,
        ["news"] = 0.05m,
        ["liquidity"] = 0.05m,
        ["volatility"] = 0.05m
    };

    // macro and onchain are flagged dynamically only when their providers return no data.

    public AdvancedDecision Evaluate(
        AdvancedDecisionInput input, RiskProfile profile, decimal equity,
        IReadOnlyDictionary<string, decimal>? weightMultipliers = null,
        ExecutionTuning? tuning = null)
    {
        var exec = tuning ?? ExecutionTuning.Default;
        var primary = GetTimeframe(input, "1h") ?? input.Timeframes.OrderByDescending(t => t.Candles.Count).First();
        var candles = primary.Candles;
        // SMC runs on the entry timeframe (15m if available) for finer structure
        var smcTf = GetTimeframe(input, "15m") ?? primary;
        var regime = MarketRegimeDetector.Detect(candles);

        // Internal factor components (kept for transparency + adaptive learning detail)
        var components = new List<ScoreComponent>
        {
            ScoreTrend(input),
            ScoreMomentum(candles),
            ScoreVolume(candles),
            ScorePriceAction(candles),
            ScoreSmartMoney(smcTf.Candles),
            ScoreOrderFlow(input.Derivatives),
            ScoreDerivatives(input.Derivatives),
            ScoreNews(input.Sentiment),
            ScoreSocial(input.Sentiment),
            ScoreVolatility(candles)
        };
        var c = components.ToDictionary(x => x.Name, x => x.Score);

        // Roll the components up into the 10 institutional scoring categories (0-100, bullish > 50).
        var scores = new Dictionary<string, decimal>
        {
            ["technical"] = Avg(c["Trend"], c["Momentum"], c["Volume"]),
            ["structure"] = Avg(c["SmartMoney"], c["PriceAction"]),
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
        var weights = ApplyMultipliers(CategoryWeights, weightMultipliers);

        // Directional score D (0-100): weighted blend of category scores.
        decimal weightedSum = 0, weightTotal = 0;
        foreach (var (key, w) in weights)
        {
            if (!scores.TryGetValue(key, out var s)) continue;
            weightedSum += s * w;
            weightTotal += w;
        }
        var directional = weightTotal == 0 ? 50m : Math.Clamp(weightedSum / weightTotal, 0m, 100m);

        // Symmetric buy/sell/hold confidences derived from the directional score.
        var confidenceBuy = directional;
        var confidenceSell = 100m - directional;
        var confidenceHold = Math.Clamp(100m - Math.Abs(directional - 50m) * 2m, 0m, 100m);

        var action = ToAction(directional);
        var isBuy = action is DecisionAction.WeakBuy or DecisionAction.Buy or DecisionAction.StrongBuy;
        var isSell = action is DecisionAction.WeakSell or DecisionAction.Sell or DecisionAction.StrongSell;

        // Conviction of the recommended side — this is the value all gates compare to the threshold.
        var confidence = isBuy ? confidenceBuy : isSell ? confidenceSell : confidenceHold;

        var atr = TechnicalIndicators.Atr(candles)[^1];
        if (atr <= 0) atr = input.LastPrice * 0.003m;
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
        var mtfAgreement = MultiTimeframeAgreement(input, isBuy);
        var probability = Math.Clamp(confidence * 0.7m + mtfAgreement * 30m, 0m, 100m);

        // Entry gate. Per design, confidence is the SOLE hard gate that opens an order: an
        // actionable side whose conviction clears the threshold trades. The remaining quality
        // checks (RR, trend, funding, spread) are advisory "cautions" — they no longer block;
        // instead they are surfaced to the AI validator, which defensively downsizes leverage
        // and quantity when the backdrop is weak.
        var reasons = new List<string>();
        var noTradeReasons = new List<string>();
        var cautionReasons = new List<string>();

        var trendAligned = isBuy ? mtfAgreement >= 0.5m : isSell ? mtfAgreement >= 0.5m : false;
        if (!isBuy && !isSell) noTradeReasons.Add("Signal is neutral (Hold)");
        if (confidence < profile.AutoTradeConfidenceThreshold) noTradeReasons.Add($"Confidence {confidence:F0} below threshold {profile.AutoTradeConfidenceThreshold:F0}");

        if (riskReward < profile.MinimumRiskReward) cautionReasons.Add($"Risk/reward {riskReward:F2} below preferred {profile.MinimumRiskReward:F2}");
        if (!trendAligned && (isBuy || isSell)) cautionReasons.Add("Higher timeframe trend not aligned");
        if (Math.Abs(input.Derivatives.FundingRate) > 0.0010m) cautionReasons.Add("Funding rate unhealthy (crowded)");
        if (input.Derivatives.BidAskSpread > entry * 0.0005m) cautionReasons.Add("Spread too wide / thin liquidity");

        var shouldTrade = noTradeReasons.Count == 0;

        // Category scores ordered by weight, then the detailed factor breakdown.
        foreach (var (name, score) in scores.OrderByDescending(kv => weights.GetValueOrDefault(kv.Key, 0m)))
        {
            var note = name switch
            {
                "macro" => input.Macro.Available ? $" [{input.Macro.Summary}]" : " [no data source — neutral]",
                "onchain" => input.Onchain.Available ? $" [{input.Onchain.Summary}]" : " [no data source — neutral]",
                _ => ""
            };
            reasons.Add($"{name} ({score:F0}, w={weights.GetValueOrDefault(name, 0m):P0}){note}");
        }
        foreach (var comp in components)
            reasons.Add($"  · {comp.Name} ({comp.Score:F0}): {comp.Reason}");

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
            Time: DateTimeOffset.UtcNow);
    }

    private static TimeframeData? GetTimeframe(AdvancedDecisionInput input, string interval)
        => input.Timeframes.FirstOrDefault(t => t.Interval == interval);

    // Multiply regime base weights by learned multipliers, then renormalize to sum 1.
    private static IReadOnlyDictionary<string, decimal> ApplyMultipliers(
        IReadOnlyDictionary<string, decimal> baseWeights,
        IReadOnlyDictionary<string, decimal>? multipliers)
    {
        if (multipliers is null || multipliers.Count == 0) return baseWeights;
        var adjusted = new Dictionary<string, decimal>();
        foreach (var (k, w) in baseWeights)
        {
            var m = multipliers.TryGetValue(k, out var mult) ? Math.Clamp(mult, 0.5m, 1.5m) : 1m;
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

    private static decimal MultiTimeframeAgreement(AdvancedDecisionInput input, bool bullish)
    {
        var tfs = input.Timeframes.Where(t => t.Candles.Count >= 60).ToList();
        if (tfs.Count == 0) return 0.5m;
        var agree = 0;
        foreach (var tf in tfs)
        {
            var closes = tf.Candles.Select(c => c.Close).ToList();
            var ema20 = TechnicalIndicators.Ema(closes, 20)[^1];
            var ema50 = TechnicalIndicators.Ema(closes, 50)[^1];
            var up = ema20 > ema50;
            if (up == bullish) agree++;
        }
        return (decimal)agree / tfs.Count;
    }

    private static ScoreComponent ScoreTrend(AdvancedDecisionInput input)
    {
        var tf = GetTimeframe(input, "1h") ?? input.Timeframes.OrderByDescending(t => t.Candles.Count).First();
        var closes = tf.Candles.Select(c => c.Close).ToList();
        var ema9 = TechnicalIndicators.Ema(closes, 9)[^1];
        var ema20 = TechnicalIndicators.Ema(closes, 20)[^1];
        var ema50 = TechnicalIndicators.Ema(closes, 50)[^1];
        var ema200 = closes.Count >= 200 ? TechnicalIndicators.Ema(closes, 200)[^1] : ema50;

        if (ema9 > ema20 && ema20 > ema50 && ema50 > ema200)
            return new ScoreComponent("Trend", 90, 0, "Full bullish EMA stack (9>20>50>200)");
        if (ema9 < ema20 && ema20 < ema50 && ema50 < ema200)
            return new ScoreComponent("Trend", 10, 0, "Full bearish EMA stack (9<20<50<200)");
        if (ema9 > ema20 && ema20 > ema50)
            return new ScoreComponent("Trend", 70, 0, "Short-term bullish alignment");
        if (ema9 < ema20 && ema20 < ema50)
            return new ScoreComponent("Trend", 30, 0, "Short-term bearish alignment");
        return new ScoreComponent("Trend", 50, 0, "Mixed trend, no clear alignment");
    }

    private static ScoreComponent ScoreMomentum(IReadOnlyList<Candle> candles)
    {
        var closes = candles.Select(c => c.Close).ToList();
        var rsi = TechnicalIndicators.Rsi(closes)[^1];
        var (macd, signal, _) = TechnicalIndicators.Macd(closes);
        var macdBull = macd[^1] > signal[^1];
        var stoch = TechnicalIndicators.StochasticK(candles);

        var score = 50m;
        if (rsi > 50 && rsi < 70) score += 15; else if (rsi >= 70) score -= 10; else if (rsi < 30) score += 5; else score -= 10;
        score += macdBull ? 15 : -15;
        if (stoch > 50 && stoch < 80) score += 10; else if (stoch <= 20) score += 5; else score -= 5;
        score = Math.Clamp(score, 0, 100);
        return new ScoreComponent("Momentum", score, 0, $"RSI {rsi:F0}, MACD {(macdBull ? "bullish" : "bearish")}, Stoch {stoch:F0}");
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

    private static ScoreComponent ScorePriceAction(IReadOnlyList<Candle> candles)
    {
        var structure = TechnicalIndicators.MarketStructure(candles);
        var closes = candles.Select(c => c.Close).ToList();
        var (upper, middle, lower) = TechnicalIndicators.BollingerBands(closes);
        var price = closes[^1];

        var score = 50m + structure * 20m;
        if (price <= lower) score += 10;      // potential mean-reversion long
        else if (price >= upper) score -= 10; // potential mean-reversion short
        score = Math.Clamp(score, 0, 100);
        var structLabel = structure > 0 ? "higher highs/lows" : structure < 0 ? "lower highs/lows" : "ranging structure";
        return new ScoreComponent("PriceAction", score, 0, $"Market structure: {structLabel}");
    }

    private static ScoreComponent ScoreOrderFlow(DerivativesSnapshot d)
    {
        var score = 50m + d.OrderBookImbalance * 40m;
        if (d.TakerBuySellRatio > 1.1m) score += 10; else if (d.TakerBuySellRatio < 0.9m) score -= 10;
        score = Math.Clamp(score, 0, 100);
        return new ScoreComponent("OrderFlow", score, 0, $"Book imbalance {d.OrderBookImbalance:F2}, taker buy/sell {d.TakerBuySellRatio:F2}");
    }

    private static ScoreComponent ScoreDerivatives(DerivativesSnapshot d)
    {
        var score = 50m;
        // Negative funding supports longs; extreme positive funding warns of crowded longs
        if (d.FundingRate < -0.0003m) score += 12; else if (d.FundingRate > 0.0005m) score -= 12;
        if (d.OpenInterestChangePercent > 2m) score += 10; else if (d.OpenInterestChangePercent < -2m) score -= 8;
        if (d.LongShortRatio > 1.5m) score -= 8; else if (d.LongShortRatio < 0.7m) score += 8; // contrarian
        score = Math.Clamp(score, 0, 100);
        return new ScoreComponent("Derivatives", score, 0, $"Funding {d.FundingRate * 100:F3}%, OIΔ {d.OpenInterestChangePercent:F1}%, L/S {d.LongShortRatio:F2}");
    }

    private static ScoreComponent ScoreNews(SentimentSnapshot s)
        => new("News", Math.Clamp(s.NewsScore, 0, 100), 0, $"News sentiment: {s.SentimentLabel}");

    private static ScoreComponent ScoreSocial(SentimentSnapshot s)
        => new("Social", Math.Clamp(s.SocialScore, 0, 100), 0, $"Fear & Greed: {s.FearGreedIndex} ({s.FearGreedLabel})");

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
        return new ScoreComponent("Volatility", score, 0, $"ATR {atrPct:F2}% of price");
    }

    private static decimal Avg(params decimal[] values) => values.Length == 0 ? 50m : values.Average();

    // Liquidity quality + directional bias. Bid-heavy book is bullish; a wide spread
    // (thin book) pulls the score back toward neutral so it adds little conviction.
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
