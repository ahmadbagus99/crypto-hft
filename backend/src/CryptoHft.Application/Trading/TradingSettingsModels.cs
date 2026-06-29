namespace CryptoHft.Application.Trading;

public sealed record TradingSettingsDto(
    bool PaperTradingOnly,
    bool AutoTradingEnabled,
    decimal MaxDailyLossPercent,
    decimal RiskPerTradePercent,
    decimal MaxExposurePercent,
    int DefaultLeverage,
    bool HasApiKey,
    bool HasApiSecret,
    string ApiKeyPreview,
    bool HasAnthropicKey,
    string AnthropicKeyPreview,
    string AiModel,
    decimal ConfidenceThreshold,
    bool HasLunarCrushKey,
    string LunarCrushKeyPreview);

public sealed record UpdateTradingSettingsRequest(
    bool PaperTradingOnly,
    bool AutoTradingEnabled,
    decimal MaxDailyLossPercent,
    decimal RiskPerTradePercent,
    decimal MaxExposurePercent,
    int DefaultLeverage,
    string? ApiKey,
    string? ApiSecret,
    string? AnthropicApiKey,
    string? AiModel,
    decimal? ConfidenceThreshold,
    string? LunarCrushApiKey);

public sealed record ConnectionTestResult(bool Connected, string Message, string? Detail = null);

public interface IConnectionTester
{
    Task<ConnectionTestResult> TestBinanceAsync(CancellationToken cancellationToken);
    Task<ConnectionTestResult> TestAnthropicAsync(CancellationToken cancellationToken);
}

public interface IRuntimeTradingSettingsService
{
    TradingSettingsDto GetPublicSettings();
    RuntimeTradingSettings GetRuntimeSettings();
    TradingSettingsDto Update(UpdateTradingSettingsRequest request);

    // Load persisted settings from the database into memory (called once at startup).
    Task LoadAsync(CancellationToken cancellationToken);
}

public sealed record RuntimeTradingSettings(
    bool PaperTradingOnly,
    bool AutoTradingEnabled,
    decimal MaxDailyLossPercent,
    decimal RiskPerTradePercent,
    decimal MaxExposurePercent,
    int DefaultLeverage,
    string? ApiKey,
    string? ApiSecret,
    string? AnthropicApiKey,
    string? AiModel,
    decimal ConfidenceThreshold,
    string? LunarCrushApiKey);

