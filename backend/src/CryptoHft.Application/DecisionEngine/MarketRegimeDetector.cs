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
            ["Trend"] = 0.30m, ["Momentum"] = 0.20m, ["OrderFlow"] = 0.15m,
            ["Volume"] = 0.10m, ["News"] = 0.08m, ["Derivatives"] = 0.07m,
            ["Volatility"] = 0.05m, ["Social"] = 0.05m
        },
        MarketRegime.Ranging => new Dictionary<string, decimal>
        {
            ["PriceAction"] = 0.22m, ["OrderFlow"] = 0.20m, ["Volume"] = 0.16m,
            ["Momentum"] = 0.14m, ["Volatility"] = 0.10m, ["News"] = 0.08m,
            ["Derivatives"] = 0.06m, ["Social"] = 0.04m
        },
        MarketRegime.HighVolatility => new Dictionary<string, decimal>
        {
            ["OrderFlow"] = 0.22m, ["Volatility"] = 0.18m, ["Trend"] = 0.15m,
            ["Momentum"] = 0.13m, ["News"] = 0.12m, ["Derivatives"] = 0.10m,
            ["Volume"] = 0.06m, ["Social"] = 0.04m
        },
        _ => new Dictionary<string, decimal> // LowVolatility — breakout anticipation
        {
            ["Trend"] = 0.22m, ["Volume"] = 0.20m, ["Momentum"] = 0.18m,
            ["OrderFlow"] = 0.14m, ["PriceAction"] = 0.12m, ["News"] = 0.08m,
            ["Derivatives"] = 0.04m, ["Social"] = 0.02m
        }
    };
}
