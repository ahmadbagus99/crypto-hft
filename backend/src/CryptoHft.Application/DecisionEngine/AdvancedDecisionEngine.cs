using CryptoHft.Application.Risk;
using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.DecisionEngine;

// The rule-based brain. Computes 0-100 factor scores across technical, order-flow,
// derivatives, and sentiment dimensions, applies regime-dependent dynamic weighting,
// and produces an explainable, risk-managed decision. The LLM layer validates on top.
public sealed class AdvancedDecisionEngine : IAdvancedDecisionEngine
{
    public AdvancedDecision Evaluate(AdvancedDecisionInput input, RiskProfile profile, decimal equity)
    {
        var primary = GetTimeframe(input, "1h") ?? input.Timeframes.OrderByDescending(t => t.Candles.Count).First();
        var candles = primary.Candles;
        var regime = MarketRegimeDetector.Detect(candles);
        var weights = MarketRegimeDetector.WeightsFor(regime);

        var components = new List<ScoreComponent>
        {
            ScoreTrend(input),
            ScoreMomentum(candles),
            ScoreVolume(candles),
            ScorePriceAction(candles),
            ScoreOrderFlow(input.Derivatives),
            ScoreDerivatives(input.Derivatives),
            ScoreNews(input.Sentiment),
            ScoreSocial(input.Sentiment),
            ScoreVolatility(candles)
        };

        var scores = components.ToDictionary(c => c.Name, c => c.Score);

        // Weighted confidence over the regime's active factors
        decimal weightedSum = 0, weightTotal = 0;
        foreach (var (key, w) in weights)
        {
            if (!scores.TryGetValue(key, out var s)) continue;
            weightedSum += s * w;
            weightTotal += w;
        }
        var confidence = weightTotal == 0 ? 50m : Math.Clamp(weightedSum / weightTotal, 0m, 100m);

        var action = ToAction(confidence);
        var isBuy = action is DecisionAction.WeakBuy or DecisionAction.Buy or DecisionAction.StrongBuy;
        var isSell = action is DecisionAction.WeakSell or DecisionAction.Sell or DecisionAction.StrongSell;

        var atr = TechnicalIndicators.Atr(candles)[^1];
        if (atr <= 0) atr = input.LastPrice * 0.003m;
        var entry = input.LastPrice;

        decimal stopLoss, takeProfit;
        if (isSell)
        {
            stopLoss = entry + atr * 2m;
            takeProfit = entry - atr * 4m;
        }
        else
        {
            stopLoss = entry - atr * 2m;
            takeProfit = entry + atr * 4m;
        }

        var riskDistance = Math.Abs(entry - stopLoss);
        var rewardDistance = Math.Abs(takeProfit - entry);
        var riskReward = riskDistance == 0 ? 0 : rewardDistance / riskDistance;

        // Position sizing from risk-per-trade budget
        var riskBudget = equity * profile.RiskPerTrade;
        var quantity = riskDistance <= 0 ? 0 : Math.Round(riskBudget / riskDistance, 3);
        var leverage = confidence >= 90 ? 10 : confidence >= 80 ? 5 : 3;

        // Probability of success: blend of confidence and multi-timeframe trend agreement
        var mtfAgreement = MultiTimeframeAgreement(input, isBuy);
        var probability = Math.Clamp(confidence * 0.7m + mtfAgreement * 30m, 0m, 100m);

        // Entry gate checks
        var reasons = new List<string>();
        var noTradeReasons = new List<string>();

        var trendAligned = isBuy ? mtfAgreement >= 0.5m : isSell ? mtfAgreement >= 0.5m : false;
        if (!isBuy && !isSell) noTradeReasons.Add("Signal is neutral (Hold)");
        if (riskReward < profile.MinimumRiskReward) noTradeReasons.Add($"Risk/reward {riskReward:F2} below minimum {profile.MinimumRiskReward:F2}");
        if (confidence < profile.AutoTradeConfidenceThreshold) noTradeReasons.Add($"Confidence {confidence:F0} below threshold {profile.AutoTradeConfidenceThreshold:F0}");
        if (!trendAligned && (isBuy || isSell)) noTradeReasons.Add("Higher timeframe trend not aligned");
        if (Math.Abs(input.Derivatives.FundingRate) > 0.0010m) noTradeReasons.Add("Funding rate unhealthy (crowded)");
        if (input.Derivatives.BidAskSpread > entry * 0.0005m) noTradeReasons.Add("Spread too wide / thin liquidity");

        var shouldTrade = noTradeReasons.Count == 0;

        foreach (var c in components.OrderByDescending(c => weights.GetValueOrDefault(c.Name, 0m)))
            reasons.Add($"{c.Name} ({c.Score:F0}): {c.Reason}");

        return new AdvancedDecision(
            Symbol: input.Symbol,
            Action: action,
            Confidence: Math.Round(confidence, 1),
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
            Scores: scores,
            Weights: weights,
            Components: components,
            Reasons: reasons,
            Llm: new LlmValidation(true, confidence, "", Array.Empty<string>(), false),
            Time: DateTimeOffset.UtcNow);
    }

    private static TimeframeData? GetTimeframe(AdvancedDecisionInput input, string interval)
        => input.Timeframes.FirstOrDefault(t => t.Interval == interval);

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

    private static DecisionAction ToAction(decimal confidence) => confidence switch
    {
        >= 95 => DecisionAction.StrongBuy,
        >= 85 => DecisionAction.Buy,
        >= 70 => DecisionAction.WeakBuy,
        >= 55 => DecisionAction.NoTrade, // Hold
        >= 40 => DecisionAction.WeakSell,
        >= 25 => DecisionAction.Sell,
        _ => DecisionAction.StrongSell
    };
}
