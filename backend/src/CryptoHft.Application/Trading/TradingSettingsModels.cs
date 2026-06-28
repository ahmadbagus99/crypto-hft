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
    string ApiKeyPreview);

public sealed record UpdateTradingSettingsRequest(
    bool PaperTradingOnly,
    bool AutoTradingEnabled,
    decimal MaxDailyLossPercent,
    decimal RiskPerTradePercent,
    decimal MaxExposurePercent,
    int DefaultLeverage,
    string? ApiKey,
    string? ApiSecret);

public interface IRuntimeTradingSettingsService
{
    TradingSettingsDto GetPublicSettings();
    RuntimeTradingSettings GetRuntimeSettings();
    TradingSettingsDto Update(UpdateTradingSettingsRequest request);
}

public sealed record RuntimeTradingSettings(
    bool PaperTradingOnly,
    bool AutoTradingEnabled,
    decimal MaxDailyLossPercent,
    decimal RiskPerTradePercent,
    decimal MaxExposurePercent,
    int DefaultLeverage,
    string? ApiKey,
    string? ApiSecret);

