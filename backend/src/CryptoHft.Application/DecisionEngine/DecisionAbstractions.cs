namespace CryptoHft.Application.DecisionEngine;

// Pulls derivatives + order-flow data from the exchange.
public interface IDerivativesDataProvider
{
    Task<DerivativesSnapshot> GetSnapshotAsync(string symbol, CancellationToken cancellationToken);
}

// Pulls free news + Fear & Greed sentiment.
public interface ISentimentProvider
{
    Task<SentimentSnapshot> GetSentimentAsync(CancellationToken cancellationToken);
}

// Pulls key-free macro data (equities, DXY, gold) and scores risk-on/off for BTC.
public interface IMacroDataProvider
{
    Task<MacroSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

// Pulls key-free on-chain network data (mempool.space) and scores bullish/bearish.
public interface IOnchainDataProvider
{
    Task<OnchainSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

// Pulls multi-timeframe candle data.
public interface IMultiTimeframeProvider
{
    Task<IReadOnlyList<TimeframeData>> GetTimeframesAsync(string symbol, CancellationToken cancellationToken);
    Task<decimal> GetLastPriceAsync(string symbol, CancellationToken cancellationToken);
}

// Hybrid LLM validation of a rule-based decision (Claude).
public interface ILlmDecisionValidator
{
    Task<LlmValidation> ValidateAsync(AdvancedDecision decision, AdvancedDecisionInput input, CancellationToken cancellationToken);
}

// Orchestrates: gather data -> rule engine -> LLM validation -> final decision.
public interface IAiDecisionService
{
    Task<AdvancedDecision> AnalyzeAsync(string symbol, CancellationToken cancellationToken);
    Task<AdvancedDecision> AnalyzeRuleBasedAsync(string symbol, CancellationToken cancellationToken);
}

public sealed record FactorPerformance(string Factor, int Regime, decimal WinRate, int Samples, decimal Multiplier);

// Realized-trade outcomes sliced by Claude's advisory verdict at decision time
// ("confirmed" / "hesitant" / "no-validation"). Only decisions matched to a closed
// position count, so these numbers reflect actual PnL, not price snapshots.
public sealed record ValidationOutcome(
    string Verdict,
    int Samples,
    int Wins,
    decimal WinRate,
    decimal AvgRoi,
    decimal TotalRealizedPnl,
    decimal AvgSizeMultiplier);

// Compact realized-history digest injected into the LLM validator's prompt so Claude's
// sizing judgment is informed by actual results instead of being stateless. Null until
// the first realized trades exist — the prompt is then unchanged (fail-safe).
public sealed record LearningSnapshot(
    int RegimeTrades,
    decimal RegimeWinRate,
    int TakeProfitHits,
    int StopLossHits,
    decimal SlAtrMultiplier,
    decimal TpAtrMultiplier,
    decimal LeverageFactor,
    IReadOnlyList<ValidationOutcome> ValidationOutcomes);

// Online learning: stores decisions, evaluates them against realized trade outcomes (preferred)
// or price movement (fallback), and derives per-factor weight multipliers (Bayesian) that the
// engine applies to regime weights.
public interface IAdaptiveWeightService
{
    Task<IReadOnlyDictionary<string, decimal>> GetMultipliersAsync(MarketRegime regime, CancellationToken cancellationToken);
    Task LogDecisionAsync(AdvancedDecision decision, CancellationToken cancellationToken);
    // Matches closed positions (Position History) to the decisions that opened them and updates
    // factor stats from realized PnL. Returns the number of positions matched.
    Task<int> EvaluateClosedPositionsAsync(CancellationToken cancellationToken);
    // Price-movement fallback for decisions that never became a trade.
    Task<int> EvaluatePendingAsync(decimal currentPrice, CancellationToken cancellationToken);
    Task<IReadOnlyList<FactorPerformance>> GetPerformanceAsync(CancellationToken cancellationToken);
    // Realized outcomes grouped by Claude's verdict — the empirical answer to whether
    // hesitancy/defensive sizing predicts trade results.
    Task<IReadOnlyList<ValidationOutcome>> GetValidationPerformanceAsync(CancellationToken cancellationToken);
    // Learned execution baselines (SL/TP geometry + leverage scaling) for the regime.
    Task<ExecutionTuning> GetExecutionTuningAsync(MarketRegime regime, CancellationToken cancellationToken);
    // Realized-history digest for the LLM prompt; null when no realized data exists yet.
    Task<LearningSnapshot?> GetLearningSnapshotAsync(MarketRegime regime, CancellationToken cancellationToken);
}
