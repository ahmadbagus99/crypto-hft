namespace CryptoHft.Application.Trading;

public sealed record TradingSettingsDto(
    bool PaperTradingOnly,
    bool AutoTradingEnabled,
    decimal MaxDailyLossPercent,
    decimal RiskPerTradePercent,
    decimal MaxExposurePercent,
    int DefaultLeverage,
    decimal TargetMarginUsdt,
    int AutoSizingMode,
    int TargetLeverage,
    bool HasApiKey,
    bool HasApiSecret,
    string ApiKeyPreview,
    bool HasAnthropicKey,
    string AnthropicKeyPreview,
    string AiModel,
    decimal ConfidenceThreshold,
    int PositionCheckIntervalMinutes,
    decimal TrailingStopDistanceR,
    bool HasLunarCrushKey,
    string LunarCrushKeyPreview,
    int TradingStyle,
    bool AccountRiskGuardEnabled);

public sealed record UpdateTradingSettingsRequest(
    bool PaperTradingOnly,
    bool AutoTradingEnabled,
    decimal MaxDailyLossPercent,
    decimal RiskPerTradePercent,
    decimal MaxExposurePercent,
    int DefaultLeverage,
    int? AutoSizingMode,
    int? TargetLeverage,
    string? ApiKey,
    string? ApiSecret,
    string? AnthropicApiKey,
    string? AiModel,
    decimal? ConfidenceThreshold,
    int? PositionCheckIntervalMinutes,
    decimal? TrailingStopDistanceR,
    string? LunarCrushApiKey,
    decimal? TargetMarginUsdt = null,
    int? TradingStyle = null,
    bool? AccountRiskGuardEnabled = null);

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
    int AutoSizingMode,
    int TargetLeverage,
    string? ApiKey,
    string? ApiSecret,
    string? AnthropicApiKey,
    string? AiModel,
    decimal ConfidenceThreshold,
    int PositionCheckIntervalMinutes,
    decimal TrailingStopDistanceR,
    string? LunarCrushApiKey,
    // Margin target (USDT) per posisi saat leverage harus dinaikkan agar order minimum
    // exchange muat di saldo kecil. Menentukan leverage efektif: ceil(notional / target).
    decimal TargetMarginUsdt = 3m,
    // 0 = Intraday (perilaku asli, geometri 1h), 1 = Scalper (geometri 15m, target
    // lebih dekat, konfirmasi & cooldown lebih cepat).
    int TradingStyle = 0,
    // Guard akun (pause daily-loss, pause consecutive-loss, exposure clamp).
    // True = aktif memblok/memotong; false = dilewati (trading tetap jalan).
    bool AccountRiskGuardEnabled = true);
