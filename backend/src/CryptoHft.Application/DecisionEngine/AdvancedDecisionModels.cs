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
    IReadOnlyList<string> Headlines,
    decimal NewsConfidence = 0m,                   // 0-100: how much the news score can be trusted (volume + agreement)
    IReadOnlyList<string>? NewsReasons = null)     // top drivers behind the news score (high-impact events first)
{
    public IReadOnlyList<string> Reasons => NewsReasons ?? Array.Empty<string>();
}

// Macro snapshot from key-free sources (equities, DXY, gold). Score is 0-100,
// > 50 = risk-on / bullish for BTC. Available is false when no source responded.
public sealed record MacroSnapshot(
    decimal Score,
    string Summary,
    bool Available);

// On-chain network health from key-free sources (mempool.space): hashrate trend,
// difficulty adjustment, fee demand. Score 0-100, > 50 = bullish. Available is false
// when no source responded.
public sealed record OnchainSnapshot(
    decimal Score,
    string Summary,
    bool Available);

// Full input bundle for the advanced engine
public sealed record AdvancedDecisionInput(
    string Symbol,
    decimal LastPrice,
    IReadOnlyList<TimeframeData> Timeframes,
    DerivativesSnapshot Derivatives,
    SentimentSnapshot Sentiment,
    MacroSnapshot Macro,
    OnchainSnapshot Onchain);

// A single 0-100 factor score with explanation
public sealed record ScoreComponent(string Name, decimal Score, decimal Weight, string Reason);

// Result of an LLM (Claude) validation pass. When Claude does not fully confirm but the
// trade still clears the confidence threshold, it may resize the trade defensively via the
// optional override fields (applied within hard safety caps). Nulls / defaults = keep baseline.
public sealed record LlmValidation(
    bool Confirmed,
    decimal AdjustedConfidence,
    string Narrative,
    IReadOnlyList<string> Risks,
    bool Used,
    decimal SizeMultiplier = 1m,
    int? Leverage = null,
    decimal? StopLoss = null,
    decimal? TakeProfit = null);

// The full explainable decision output
public sealed record AdvancedDecision(
    string Symbol,
    DecisionAction Action,
    decimal Confidence,           // 0-100 conviction of the recommended side (buy if long, sell if short, hold otherwise)
    decimal ConfidenceBuy,        // 0-100 directional bullish score
    decimal ConfidenceSell,       // 0-100 directional bearish score (100 - buy)
    decimal ConfidenceHold,       // 0-100 peaks when the read is neutral
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
    IReadOnlyList<string> Cautions,   // advisory quality warnings (weak RR/trend/funding/spread); do not block, drive AI downsizing
    IReadOnlyDictionary<string, decimal> Scores,
    IReadOnlyDictionary<string, decimal> Weights,
    IReadOnlyList<ScoreComponent> Components,
    IReadOnlyList<string> Reasons,
    LlmValidation Llm,
    DateTimeOffset Time);

// Learned execution baselines (per regime). Defaults reproduce the original fixed
// geometry (2xATR SL, 4xATR TP, confidence-tier leverage untouched); values come from
// ExecutionTuningPolicy over realized exits in Position History.
public sealed record ExecutionTuning(
    decimal SlAtrMultiplier,
    decimal TpAtrMultiplier,
    decimal LeverageFactor)
{
    public static readonly ExecutionTuning Default = new(2m, 4m, 1m);
}

public interface IAdvancedDecisionEngine
{
    AdvancedDecision Evaluate(
        AdvancedDecisionInput input,
        Risk.RiskProfile profile,
        decimal equity,
        IReadOnlyDictionary<string, decimal>? weightMultipliers = null,
        ExecutionTuning? tuning = null);
}
