using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.DecisionEngine;

// Market regime classification — drives execution baselines and learning buckets.
// Values are persisted as ints in FactorStats/ExecutionStats/AiDecisionLogs, so new
// members are only ever appended. Trending (0) is legacy: the detector now always
// resolves trend direction so up- and down-trends learn in separate buckets.
public enum MarketRegime
{
    Trending,
    Ranging,
    HighVolatility,
    LowVolatility,
    TrendingUp,
    TrendingDown
}

// One OHLCV candle used by the analysis engine. TakerBuyVolume (kline field 9) enables
// cumulative volume delta; 0 when the source did not provide it.
public sealed record Candle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal TakerBuyVolume = 0m);

// Per-timeframe candle series
public sealed record TimeframeData(string Interval, IReadOnlyList<Candle> Candles);

// Derivatives + order-flow snapshot from Binance. The trailing fields default to 0 when
// their source is unavailable (no funding history / liquidation feed not warmed up yet).
public sealed record DerivativesSnapshot(
    decimal FundingRate,
    decimal OpenInterest,
    decimal OpenInterestChangePercent,
    decimal LongShortRatio,
    decimal TakerBuySellRatio,
    decimal OrderBookImbalance,
    decimal BidAskSpread,
    decimal CumulativeFunding24h = 0m,          // sum of the last 3 funding periods (8h each)
    decimal LongLiquidationNotional = 0m,       // USD notional of LONGs force-closed in the recent window
    decimal ShortLiquidationNotional = 0m);     // USD notional of SHORTs force-closed in the recent window

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

// Full input bundle for the advanced engine. ActiveEventWindow carries the caution label
// of a scheduled high-impact macro event window (from IEconomicCalendarProvider); null on
// a quiet tape — the engine itself stays clock-free.
public sealed record AdvancedDecisionInput(
    string Symbol,
    decimal LastPrice,
    IReadOnlyList<TimeframeData> Timeframes,
    DerivativesSnapshot Derivatives,
    SentimentSnapshot Sentiment,
    MacroSnapshot Macro,
    OnchainSnapshot Onchain,
    string? ActiveEventWindow = null);

// A single 0-100 factor score with explanation
public sealed record ScoreComponent(string Name, decimal Score, decimal Weight, string Reason);

// Learned per-category adjustment from realized outcomes. Multiplier scales the category's
// fixed weight (clamped 0.5-1.5x by the engine). Inverted marks a category whose raw score
// has proven consistently anti-predictive (directional accuracy < 40% over enough samples):
// the engine folds its score around 50 before blending, since weight scaling alone can
// never repair a factor that is reliably wrong-way.
public sealed record FactorAdjustment(decimal Multiplier, bool Inverted = false);

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
    decimal? TakeProfit = null,
    // The two tallies Claude's sizing band is derived from (null when the LLM was not consulted
    // or the model omitted them). Logged so the multiplier can be audited against its own counts.
    int? AlignedCount = null,
    int? BlockingCount = null,
    // Whether a bar has actually rejected the level the setup rests on, as opposed to price
    // still travelling into it. Timing rather than direction, so it is kept out of
    // BlockingCount and priced as a size haircut instead of a veto. Null when not reported.
    bool? LevelConfirmed = null);

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
    DateTimeOffset Time,
    // Volume-profile context (POC/VA/HVN/LVN + any TP/SL snap applied). Informational:
    // shown on the dashboard and in the LLM payload; empty when the profile could not build.
    string VolumeProfileNote = "");

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
        IReadOnlyDictionary<string, FactorAdjustment>? factorAdjustments = null,
        ExecutionTuning? tuning = null,
        TradingStyleProfile? styleProfile = null,
        IReadOnlyDictionary<string, decimal>? categoryBaselines = null);
}
