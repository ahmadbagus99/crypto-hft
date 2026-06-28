using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.DecisionEngine;

public interface IMultiFactorDecisionEngine
{
    DecisionResult Evaluate(DecisionInput input);
}

public sealed class MultiFactorDecisionEngine : IMultiFactorDecisionEngine
{
    public DecisionResult Evaluate(DecisionInput input)
    {
        var factors = new List<FactorScore>
        {
            ScoreTrend(input),
            ScoreMomentum(input),
            ScoreVolume(input),
            ScoreFunding(input),
            ScoreNews(input),
            ScoreVolatility(input)
        };

        var weightedScore = factors.Sum(x => x.Score * x.Weight) / factors.Sum(x => x.Weight);
        var confidence = Math.Clamp(weightedScore, 0m, 100m);
        var action = ToAction(confidence);
        var atr = Math.Max(input.Atr, input.LastPrice * 0.003m);
        var isBuy = action is DecisionAction.WeakBuy or DecisionAction.Buy or DecisionAction.StrongBuy;
        var stopLoss = isBuy ? input.LastPrice - atr * 2 : input.LastPrice + atr * 2;
        var takeProfit = isBuy ? input.LastPrice + atr * 4 : input.LastPrice - atr * 4;
        var reason = string.Join("; ", factors.OrderByDescending(x => x.Weight).Select(x => $"{x.Name}: {x.Reason}"));

        return new DecisionResult(
            input.Symbol,
            action,
            confidence,
            2.0m,
            Decimal.Round(stopLoss, 2),
            Decimal.Round(takeProfit, 2),
            0,
            confidence >= 85 ? 5 : 3,
            factors,
            reason,
            DateTimeOffset.UtcNow);
    }

    private static FactorScore ScoreTrend(DecisionInput input)
    {
        var bullishStack = input.Ema9 > input.Ema20 && input.Ema20 > input.Ema50 && input.Ema50 > input.Ema200;
        var bearishStack = input.Ema9 < input.Ema20 && input.Ema20 < input.Ema50 && input.Ema50 < input.Ema200;
        if (bullishStack) return new FactorScore("Trend", 88, 1.4m, "EMA stack bullish across fast to slow trend");
        if (bearishStack) return new FactorScore("Trend", 12, 1.4m, "EMA stack bearish across fast to slow trend");
        return new FactorScore("Trend", 50, 1.4m, "Trend is mixed, wait for structure confirmation");
    }

    private static FactorScore ScoreMomentum(DecisionInput input)
    {
        if (input.Rsi > 52 && input.Rsi < 72 && input.Macd > input.MacdSignal)
            return new FactorScore("Momentum", 80, 1.2m, "RSI healthy and MACD confirms upside momentum");
        if (input.Rsi < 48 && input.Rsi > 28 && input.Macd < input.MacdSignal)
            return new FactorScore("Momentum", 20, 1.2m, "RSI weak and MACD confirms downside momentum");
        return new FactorScore("Momentum", 50, 1.2m, "Momentum does not confirm direction");
    }

    private static FactorScore ScoreVolume(DecisionInput input)
    {
        var score = 50m + input.OrderBookImbalance * 50m + input.OpenInterestChange * 10m;
        return new FactorScore("Orderflow", Math.Clamp(score, 0, 100), 1.1m, "Order book imbalance and open interest are folded into liquidity score");
    }

    private static FactorScore ScoreFunding(DecisionInput input)
    {
        if (input.FundingRate > 0.0005m) return new FactorScore("Funding", 40, 0.7m, "Positive funding is elevated, avoid crowded longs");
        if (input.FundingRate < -0.0005m) return new FactorScore("Funding", 60, 0.7m, "Negative funding supports mean-reversion long bias");
        return new FactorScore("Funding", 50, 0.7m, "Funding is neutral");
    }

    private static FactorScore ScoreNews(DecisionInput input)
    {
        return new FactorScore("News", Math.Clamp(input.NewsScore, 0, 100), 0.9m, "Latest news sentiment is included in final confidence");
    }

    private static FactorScore ScoreVolatility(DecisionInput input)
    {
        var score = input.VolatilityScore switch
        {
            > 80 => 35,
            < 25 => 45,
            _ => 62
        };
        return new FactorScore("Volatility", score, 0.8m, "Risk is reduced when volatility is too compressed or too expanded");
    }

    private static DecisionAction ToAction(decimal confidence) => confidence switch
    {
        >= 85 => DecisionAction.StrongBuy,
        >= 70 => DecisionAction.Buy,
        >= 55 => DecisionAction.WeakBuy,
        > 45 => DecisionAction.NoTrade,
        > 30 => DecisionAction.WeakSell,
        > 15 => DecisionAction.Sell,
        _ => DecisionAction.StrongSell
    };
}
