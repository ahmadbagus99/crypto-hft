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
}

public sealed record FactorPerformance(string Factor, int Regime, decimal WinRate, int Samples, decimal Multiplier);

// Online learning: stores decisions, evaluates them against realized price, and derives
// per-factor weight multipliers (Bayesian) that the engine applies to regime weights.
public interface IAdaptiveWeightService
{
    Task<IReadOnlyDictionary<string, decimal>> GetMultipliersAsync(MarketRegime regime, CancellationToken cancellationToken);
    Task LogDecisionAsync(AdvancedDecision decision, CancellationToken cancellationToken);
    Task<int> EvaluatePendingAsync(decimal currentPrice, CancellationToken cancellationToken);
    Task<IReadOnlyList<FactorPerformance>> GetPerformanceAsync(CancellationToken cancellationToken);
}
