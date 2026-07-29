using CryptoHft.Application.Trading;
using CryptoHft.Domain.Entities;
using CryptoHft.Infrastructure.Binance;
using CryptoHft.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Trading;

public sealed class RuntimeTradingSettingsService : IRuntimeTradingSettingsService
{
    private const int RowId = 1;

    private readonly object _lock = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RuntimeTradingSettingsService> _logger;
    private RuntimeTradingSettings _settings;

    public RuntimeTradingSettingsService(
        IOptions<BinanceOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<RuntimeTradingSettingsService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settings = new RuntimeTradingSettings(
            PaperTradingOnly: options.Value.PaperTradingOnly,
            AutoTradingEnabled: false,
            MaxDailyLossPercent: 0.30m,
            RiskPerTradePercent: 0.01m,
            MaxExposurePercent: 0.25m,
            DefaultLeverage: 5,
            AutoSizingMode: 0,
            TargetLeverage: 20,
            ApiKey: options.Value.ApiKey,
            ApiSecret: options.Value.ApiSecret,
            AnthropicApiKey: null,
            AiModel: null,
            ConfidenceThreshold: 80m,
            PositionCheckIntervalMinutes: 30,
            TrailingStopDistanceR: 1.0m,
            LunarCrushApiKey: null);
    }

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

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var row = await db.TradingSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == RowId, cancellationToken);
            if (row is null) return;

            lock (_lock)
            {
                _settings = new RuntimeTradingSettings(
                    PaperTradingOnly: row.PaperTradingOnly,
                    AutoTradingEnabled: row.AutoTradingEnabled,
                    MaxDailyLossPercent: row.MaxDailyLossPercent,
                    RiskPerTradePercent: row.RiskPerTradePercent,
                    MaxExposurePercent: row.MaxExposurePercent,
                    DefaultLeverage: row.DefaultLeverage,
                    AutoSizingMode: NormalizeAutoSizingMode(row.AutoSizingMode),
                    TargetLeverage: ClampTargetLeverage(row.TargetLeverage),
                    // Fall back to env-provided keys if the DB row never stored one.
                    ApiKey: string.IsNullOrWhiteSpace(row.ApiKey) ? _settings.ApiKey : row.ApiKey,
                    ApiSecret: string.IsNullOrWhiteSpace(row.ApiSecret) ? _settings.ApiSecret : row.ApiSecret,
                    AnthropicApiKey: row.AnthropicApiKey,
                    AiModel: row.AiModel,
                    ConfidenceThreshold: row.ConfidenceThreshold > 0 ? row.ConfidenceThreshold : 80m,
                    PositionCheckIntervalMinutes: ClampPositionCheckInterval(row.PositionCheckIntervalMinutes),
                    TrailingStopDistanceR: NormalizeTrailingStopDistance(row.TrailingStopDistanceR, 1.0m),
                    LunarCrushApiKey: row.LunarCrushApiKey,
                    TargetMarginUsdt: row.TargetMarginUsdt > 0 ? row.TargetMarginUsdt : 3m,
                    TradingStyle: NormalizeTradingStyle(row.TradingStyle));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted trading settings; using defaults");
        }
    }

    public TradingSettingsDto Update(UpdateTradingSettingsRequest request)
    {
        RuntimeTradingSettings updated;
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
                AutoSizingMode = request.AutoSizingMode is not null ? NormalizeAutoSizingMode(request.AutoSizingMode.Value) : _settings.AutoSizingMode,
                TargetLeverage = request.TargetLeverage is > 0 ? ClampTargetLeverage(request.TargetLeverage.Value) : _settings.TargetLeverage,
                ApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? _settings.ApiKey : request.ApiKey.Trim(),
                ApiSecret = string.IsNullOrWhiteSpace(request.ApiSecret) ? _settings.ApiSecret : request.ApiSecret.Trim(),
                AnthropicApiKey = string.IsNullOrWhiteSpace(request.AnthropicApiKey) ? _settings.AnthropicApiKey : request.AnthropicApiKey.Trim(),
                AiModel = string.IsNullOrWhiteSpace(request.AiModel) ? _settings.AiModel : request.AiModel.Trim(),
                ConfidenceThreshold = request.ConfidenceThreshold is > 0 ? Math.Clamp(request.ConfidenceThreshold.Value, 1m, 100m) : _settings.ConfidenceThreshold,
                PositionCheckIntervalMinutes = request.PositionCheckIntervalMinutes is > 0 ? ClampPositionCheckInterval(request.PositionCheckIntervalMinutes.Value) : _settings.PositionCheckIntervalMinutes,
                TrailingStopDistanceR = request.TrailingStopDistanceR is > 0 ? NormalizeTrailingStopDistance(request.TrailingStopDistanceR.Value, _settings.TrailingStopDistanceR) : _settings.TrailingStopDistanceR,
                LunarCrushApiKey = string.IsNullOrWhiteSpace(request.LunarCrushApiKey) ? _settings.LunarCrushApiKey : request.LunarCrushApiKey.Trim(),
                TargetMarginUsdt = request.TargetMarginUsdt is > 0 ? Math.Clamp(request.TargetMarginUsdt.Value, 1m, 1000m) : _settings.TargetMarginUsdt,
                TradingStyle = request.TradingStyle is not null ? NormalizeTradingStyle(request.TradingStyle.Value) : _settings.TradingStyle
            };
            updated = _settings;
        }

        Persist(updated);
        return ToDto(updated);
    }

    private void Persist(RuntimeTradingSettings s)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var row = db.TradingSettings.FirstOrDefault(x => x.Id == RowId);
            if (row is null)
            {
                row = new PersistedTradingSettings { Id = RowId };
                db.TradingSettings.Add(row);
            }

            row.PaperTradingOnly = s.PaperTradingOnly;
            row.AutoTradingEnabled = s.AutoTradingEnabled;
            row.MaxDailyLossPercent = s.MaxDailyLossPercent;
            row.RiskPerTradePercent = s.RiskPerTradePercent;
            row.MaxExposurePercent = s.MaxExposurePercent;
            row.DefaultLeverage = s.DefaultLeverage;
            row.AutoSizingMode = s.AutoSizingMode;
            row.TargetLeverage = s.TargetLeverage;
            row.ApiKey = s.ApiKey;
            row.ApiSecret = s.ApiSecret;
            row.AnthropicApiKey = s.AnthropicApiKey;
            row.AiModel = s.AiModel;
            row.ConfidenceThreshold = s.ConfidenceThreshold;
            row.PositionCheckIntervalMinutes = s.PositionCheckIntervalMinutes;
            row.TrailingStopDistanceR = s.TrailingStopDistanceR;
            row.LunarCrushApiKey = s.LunarCrushApiKey;
            row.TargetMarginUsdt = s.TargetMarginUsdt;
            row.TradingStyle = s.TradingStyle;

            db.SaveChanges();
        }
        catch (Exception ex)
        {
            // Best-effort: in-memory settings remain authoritative for this process.
            _logger.LogWarning(ex, "Failed to persist trading settings");
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
            settings.TargetMarginUsdt,
            settings.AutoSizingMode,
            settings.TargetLeverage,
            !string.IsNullOrWhiteSpace(settings.ApiKey),
            !string.IsNullOrWhiteSpace(settings.ApiSecret),
            Mask(settings.ApiKey),
            !string.IsNullOrWhiteSpace(settings.AnthropicApiKey),
            Mask(settings.AnthropicApiKey),
            settings.AiModel ?? "claude-opus-4-8",
            settings.ConfidenceThreshold,
            settings.PositionCheckIntervalMinutes,
            settings.TrailingStopDistanceR,
            !string.IsNullOrWhiteSpace(settings.LunarCrushApiKey),
            Mask(settings.LunarCrushApiKey),
            settings.TradingStyle);
    }

    private static decimal ClampPercent(decimal value, decimal min, decimal max)
    {
        if (value <= 0) return min;
        return Math.Min(Math.Max(value, min), max);
    }

    private static int ClampPositionCheckInterval(int minutes)
    {
        return Math.Clamp(minutes <= 0 ? 30 : minutes, 5, 120);
    }

    private static int NormalizeAutoSizingMode(int value)
    {
        return value == 1 ? 1 : 0;
    }

    // Only two styles exist: 0 = Intraday, 1 = Scalper. Anything else falls back to Intraday.
    private static int NormalizeTradingStyle(int value)
    {
        return value == 1 ? 1 : 0;
    }

    private static int ClampTargetLeverage(int leverage)
    {
        return Math.Clamp(leverage <= 0 ? 20 : leverage, 1, 20);
    }

    private static decimal NormalizeTrailingStopDistance(decimal value, decimal fallback)
    {
        return value switch
        {
            0.50m => 0.50m,
            0.75m => 0.75m,
            1.00m => 1.00m,
            1.25m => 1.25m,
            _ => fallback is 0.50m or 0.75m or 1.00m or 1.25m ? fallback : 1.00m
        };
    }

    private static string Mask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Length <= 8 ? "********" : $"{value[..4]}...{value[^4..]}";
    }
}
