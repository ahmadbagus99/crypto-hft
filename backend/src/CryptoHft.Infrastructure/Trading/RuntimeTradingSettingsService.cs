using CryptoHft.Application.Trading;
using CryptoHft.Infrastructure.Binance;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Trading;

public sealed class RuntimeTradingSettingsService(IOptions<BinanceOptions> options) : IRuntimeTradingSettingsService
{
    private readonly object _lock = new();
    private RuntimeTradingSettings _settings = new(
        PaperTradingOnly: options.Value.PaperTradingOnly,
        AutoTradingEnabled: false,
        MaxDailyLossPercent: 0.30m,
        RiskPerTradePercent: 0.01m,
        MaxExposurePercent: 0.25m,
        DefaultLeverage: 5,
        ApiKey: options.Value.ApiKey,
        ApiSecret: options.Value.ApiSecret);

    public TradingSettingsDto GetPublicSettings()
    {
        lock (_lock)
        {
            return ToDto(_settings);
        }
    }

    public RuntimeTradingSettings GetRuntimeSettings()
    {
        lock (_lock)
        {
            return _settings;
        }
    }

    public TradingSettingsDto Update(UpdateTradingSettingsRequest request)
    {
        lock (_lock)
        {
            _settings = _settings with
            {
                PaperTradingOnly = request.PaperTradingOnly,
                AutoTradingEnabled = request.AutoTradingEnabled,
                MaxDailyLossPercent = ClampPercent(request.MaxDailyLossPercent, 0.01m, 1m),
                RiskPerTradePercent = ClampPercent(request.RiskPerTradePercent, 0.001m, 0.20m),
                MaxExposurePercent = ClampPercent(request.MaxExposurePercent, 0.01m, 5m),
                DefaultLeverage = Math.Clamp(request.DefaultLeverage, 1, 125),
                ApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? _settings.ApiKey : request.ApiKey.Trim(),
                ApiSecret = string.IsNullOrWhiteSpace(request.ApiSecret) ? _settings.ApiSecret : request.ApiSecret.Trim()
            };

            return ToDto(_settings);
        }
    }

    private static TradingSettingsDto ToDto(RuntimeTradingSettings settings)
    {
        return new TradingSettingsDto(
            settings.PaperTradingOnly,
            settings.AutoTradingEnabled,
            settings.MaxDailyLossPercent,
            settings.RiskPerTradePercent,
            settings.MaxExposurePercent,
            settings.DefaultLeverage,
            !string.IsNullOrWhiteSpace(settings.ApiKey),
            !string.IsNullOrWhiteSpace(settings.ApiSecret),
            Mask(settings.ApiKey));
    }

    private static decimal ClampPercent(decimal value, decimal min, decimal max)
    {
        if (value <= 0) return min;
        return Math.Min(Math.Max(value, min), max);
    }

    private static string Mask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Length <= 8 ? "********" : $"{value[..4]}...{value[^4..]}";
    }
}

