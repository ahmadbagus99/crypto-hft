using CryptoHft.Domain.Enums;

namespace CryptoHft.Domain.Entities;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Exchange { get; set; } = "BinanceFutures";
    public string Environment { get; set; } = "Testnet";
    public string EncryptedKey { get; set; } = string.Empty;
    public string EncryptedSecret { get; set; } = string.Empty;
    public bool CanTrade { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Symbol { get; set; } = "BTCUSDT";
    public TradeSide Side { get; set; }
    public OrderKind Kind { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.New;
    public decimal Quantity { get; set; }
    public decimal? Price { get; set; }
    public decimal? StopPrice { get; set; }
    public decimal? TakeProfit { get; set; }
    public decimal? StopLoss { get; set; }
    public bool ReduceOnly { get; set; }
    public bool IsPaper { get; set; } = true;
    public string? ExchangeOrderId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Position
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Symbol { get; set; } = "BTCUSDT";
    public TradeSide Side { get; set; }
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal MarkPrice { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public decimal RealizedPnl { get; set; }
    public decimal Roi { get; set; }
    public int Leverage { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }
}

public sealed class Signal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Symbol { get; set; } = "BTCUSDT";
    public DecisionAction Action { get; set; }
    public decimal Confidence { get; set; }
    public decimal ExpectedRiskReward { get; set; }
    public decimal SuggestedQuantity { get; set; }
    public int SuggestedLeverage { get; set; }
    public decimal SuggestedStopLoss { get; set; }
    public decimal SuggestedTakeProfit { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WalletSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public decimal WalletBalance { get; set; }
    public decimal AvailableBalance { get; set; }
    public decimal MarginBalance { get; set; }
    public decimal DailyPnl { get; set; }
    public decimal WeeklyPnl { get; set; }
    public decimal MonthlyPnl { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RiskManagementSetting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public decimal MaxDailyLoss { get; set; } = 0.30m;
    public int MaxConsecutiveLosses { get; set; } = 3;
    public int MaxOpenPositions { get; set; } = 1;
    public decimal MaxExposure { get; set; } = 0.25m;
    public decimal RiskPerTrade { get; set; } = 0.01m;
    public decimal MinimumRiskReward { get; set; } = 2.0m;
    public decimal AutoTradeConfidenceThreshold { get; set; } = 80m;
    public bool AutoTradingEnabled { get; set; }
}

// One row per Claude API call. Records token usage and the computed USD cost so the
// dashboard can show estimated spend (Anthropic has no balance/credit API).
public sealed class AiUsageLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Model { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// Single-row table (Id = 1) holding the runtime trading settings + API keys so they
// survive container restarts instead of living only in memory.
public sealed class PersistedTradingSettings
{
    public int Id { get; set; } = 1;
    public bool PaperTradingOnly { get; set; } = true;
    public bool AutoTradingEnabled { get; set; }
    public decimal MaxDailyLossPercent { get; set; } = 0.30m;
    public decimal RiskPerTradePercent { get; set; } = 0.01m;
    public decimal MaxExposurePercent { get; set; } = 0.25m;
    public int DefaultLeverage { get; set; } = 5;
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
    public string? AnthropicApiKey { get; set; }
    public string? AiModel { get; set; }
    public decimal ConfidenceThreshold { get; set; } = 80m;
    public string? LunarCrushApiKey { get; set; }
}

public sealed class NewsItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Source { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTimeOffset PublishedAt { get; set; }
}

public sealed class NewsSentiment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid NewsItemId { get; set; }
    public decimal BullishScore { get; set; }
    public decimal BearishScore { get; set; }
    public decimal ImpactScore { get; set; }
    public decimal ConfidenceScore { get; set; }
    public bool IgnoreAsLikelyFake { get; set; }
}

// One logged AI decision, evaluated later against realized price movement (online learning).
public sealed class AiDecisionLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Symbol { get; set; } = "BTCUSDT";
    public int Regime { get; set; }
    public DecisionAction Action { get; set; }
    public decimal Confidence { get; set; }
    public decimal EntryPrice { get; set; }
    public string ScoresJson { get; set; } = "{}"; // factor -> 0-100 score at decision time
    public bool Evaluated { get; set; }
    public bool? Win { get; set; }
    public decimal PriceMovePercent { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EvaluatedAt { get; set; }
}

// Per-factor, per-regime Bayesian performance (Beta distribution). Weight multiplier
// is derived from the posterior mean Alpha/(Alpha+Beta).
public sealed class FactorStat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Regime { get; set; }
    public string Factor { get; set; } = string.Empty;
    public decimal Alpha { get; set; } = 1m; // prior wins
    public decimal Beta { get; set; } = 1m;  // prior losses
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
