namespace CryptoHft.Application.DecisionEngine;

// Classifies the current market regime from the primary (1h) timeframe.
// ADX measures trend strength; ATR% measures volatility.
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

        // ADX > 25 indicates a directional trend; otherwise ranging
        return adx >= 25m ? MarketRegime.Trending : MarketRegime.Ranging;
    }

    // Dynamic factor weights per regime. Keys must match the score keys produced by the engine.
    public static IReadOnlyDictionary<string, decimal> WeightsFor(MarketRegime regime) => regime switch
    {
        MarketRegime.Trending => new Dictionary<string, decimal>
        {
            ["Trend"] = 0.28m, ["Momentum"] = 0.18m, ["OrderFlow"] = 0.14m,
            ["SmartMoney"] = 0.10m, ["Volume"] = 0.09m, ["News"] = 0.07m,
            ["Derivatives"] = 0.06m, ["Volatility"] = 0.04m, ["Social"] = 0.04m
        },
        MarketRegime.Ranging => new Dictionary<string, decimal>
        {
            ["SmartMoney"] = 0.22m, ["PriceAction"] = 0.18m, ["OrderFlow"] = 0.18m,
            ["Volume"] = 0.13m, ["Momentum"] = 0.11m, ["Volatility"] = 0.08m,
            ["News"] = 0.05m, ["Derivatives"] = 0.03m, ["Social"] = 0.02m
        },
        MarketRegime.HighVolatility => new Dictionary<string, decimal>
        {
            ["OrderFlow"] = 0.20m, ["SmartMoney"] = 0.16m, ["Volatility"] = 0.15m,
            ["Trend"] = 0.13m, ["Momentum"] = 0.11m, ["News"] = 0.10m,
            ["Derivatives"] = 0.08m, ["Volume"] = 0.04m, ["Social"] = 0.03m
        },
        _ => new Dictionary<string, decimal> // LowVolatility — breakout anticipation
        {
            ["Trend"] = 0.20m, ["Volume"] = 0.18m, ["Momentum"] = 0.16m,
            ["SmartMoney"] = 0.14m, ["OrderFlow"] = 0.12m, ["PriceAction"] = 0.10m,
            ["News"] = 0.06m, ["Derivatives"] = 0.02m, ["Social"] = 0.02m
        }
    };
}
