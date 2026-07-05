namespace CryptoHft.Application.DecisionEngine;

// Classifies the current market regime from the primary (1h) timeframe.
// ADX measures trend strength; ATR% measures volatility. Directional trends are split
// into TrendingUp/TrendingDown so per-regime learning (factor weights, SL/TP geometry,
// leverage) never pools up-trend outcomes with down-trend outcomes — a factor that only
// works on one side of the market would otherwise average out to "mediocre" in both.
public static class MarketRegimeDetector
{
    public static MarketRegime Detect(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 30) return MarketRegime.Ranging;

        var closes = candles.Select(c => c.Close).ToList();
        var adx = TechnicalIndicators.Adx(candles)[^1];
        var atr = TechnicalIndicators.Atr(candles)[^1];
        var lastPrice = closes[^1];
        var atrPercent = lastPrice == 0 ? 0 : atr / lastPrice * 100m;

        // Volatility regimes take precedence when extreme
        if (atrPercent >= 2.5m) return MarketRegime.HighVolatility;
        if (atrPercent <= 0.4m) return MarketRegime.LowVolatility;

        if (adx < 25m) return MarketRegime.Ranging;

        // ADX confirms a directional trend; EMA order resolves which direction.
        var ema20 = TechnicalIndicators.Ema(closes, 20)[^1];
        var ema50 = TechnicalIndicators.Ema(closes, 50)[^1];
        return ema20 >= ema50 ? MarketRegime.TrendingUp : MarketRegime.TrendingDown;
    }
}
