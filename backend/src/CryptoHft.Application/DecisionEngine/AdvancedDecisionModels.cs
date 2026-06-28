using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.DecisionEngine;

// Market regime classification — drives dynamic weighting
public enum MarketRegime
{
    Trending,
    Ranging,
    HighVolatility,
    LowVolatility
}

// One OHLCV candle used by the analysis engine
public sealed record Candle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

// Per-timeframe candle series
public sealed record TimeframeData(string Interval, IReadOnlyList<Candle> Candles);

// Derivatives + order-flow snapshot from Binance
public sealed record DerivativesSnapshot(
    decimal FundingRate,
    decimal OpenInterest,
    decimal OpenInterestChangePercent,
    decimal LongShortRatio,
    decimal TakerBuySellRatio,
    decimal OrderBookImbalance,
    decimal BidAskSpread);

// News + social sentiment snapshot (free sources)
public sealed record SentimentSnapshot(
    decimal NewsScore,        // 0-100
    decimal SocialScore,      // 0-100 (Fear & Greed based)
    string SentimentLabel,    // Bullish / Bearish / Neutral
    int FearGreedIndex,       // 0-100
    string FearGreedLabel,
    IReadOnlyList<string> Headlines);

// Full input bundle for the advanced engine
public sealed record AdvancedDecisionInput(
    string Symbol,
    decimal LastPrice,
    IReadOnlyList<TimeframeData> Timeframes,
    DerivativesSnapshot Derivatives,
    SentimentSnapshot Sentiment);

// A single 0-100 factor score with explanation
public sealed record ScoreComponent(string Name, decimal Score, decimal Weight, string Reason);

// Result of an LLM (Claude) validation pass
public sealed record LlmValidation(
    bool Confirmed,
    decimal AdjustedConfidence,
    string Narrative,
    IReadOnlyList<string> Risks,
    bool Used);

// The full explainable decision output
public sealed record AdvancedDecision(
    string Symbol,
    DecisionAction Action,
    decimal Confidence,           // 0-100 final
    decimal ProbabilityOfSuccess, // 0-100
    MarketRegime Regime,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,
    decimal TrailingStopPercent,
    decimal RiskReward,
    decimal PositionSizeQuantity,
    int Leverage,
    bool ShouldTrade,
    string NoTradeReason,
    IReadOnlyDictionary<string, decimal> Scores,
    IReadOnlyDictionary<string, decimal> Weights,
    IReadOnlyList<ScoreComponent> Components,
    IReadOnlyList<string> Reasons,
    LlmValidation Llm,
    DateTimeOffset Time);

public interface IAdvancedDecisionEngine
{
    AdvancedDecision Evaluate(
        AdvancedDecisionInput input,
        Risk.RiskProfile profile,
        decimal equity,
        IReadOnlyDictionary<string, decimal>? weightMultipliers = null);
}
