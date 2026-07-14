using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Ai;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Notifications;
using CryptoHft.Application.Risk;
using CryptoHft.Application.Trading;
using CryptoHft.Infrastructure.Ai;
using CryptoHft.Infrastructure.Binance;
using CryptoHft.Infrastructure.Persistence;
using CryptoHft.Infrastructure.Notifications;
using CryptoHft.Infrastructure.Trading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoHft.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BinanceOptions>(configuration.GetSection("Binance"));
        services.Configure<ApnsOptions>(configuration.GetSection("Apns"));
        services.Configure<BarkOptions>(configuration.GetSection("Bark"));
        services.AddDbContext<TradingDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Postgres")));
        services.AddScoped<ITradingExecutor, BinanceFuturesTradingExecutor>();
        services.AddSingleton<IRuntimeTradingSettingsService, RuntimeTradingSettingsService>();
        services.AddSingleton<IFuturesExchangeInfoClient, BinanceFuturesExchangeInfoClient>();
        services.AddScoped<IExchangeRuleValidator, BinanceExchangeRuleValidator>();
        services.AddSingleton<IKillSwitchService, BinanceFuturesKillSwitchService>();
        services.AddSingleton<IMarketDataStream, BinanceFuturesWebSocketStream>();
        services.AddSingleton<IUserDataStream, BinanceFuturesUserDataStream>();
        services.AddSingleton<IFuturesAccountClient, BinanceFuturesAccountWebSocketApiClient>();
        services.AddSingleton<IMultiFactorDecisionEngine, MultiFactorDecisionEngine>();
        services.AddSingleton<IRiskManager, RiskManager>();
        services.AddSingleton<IAutoTradeRiskGate, BinanceAutoTradeRiskGate>();
        services.AddSingleton<IOpenPositionRevalidationStore, OpenPositionRevalidationStore>();
        services.AddSingleton<IPositionHistoryService, PositionHistoryService>();
        services.AddSingleton<ITrailingStopGuard, TrailingStopGuardService>();
        services.AddSingleton<ITrailingStopActivityStore, TrailingStopActivityStore>();
        services.AddSingleton<ApnsPushNotificationService>();
        services.AddSingleton<BarkPushNotificationService>();
        services.AddSingleton<IPushNotificationService, CompositePushNotificationService>();

        // AI decision engine (Phase 1): rule-based scoring + dynamic weighting + hybrid LLM validation
        services.Configure<AiOptions>(configuration.GetSection("Ai"));
        services.AddSingleton<IAdvancedDecisionEngine, AdvancedDecisionEngine>();
        services.AddSingleton<IMultiTimeframeProvider, BinanceMultiTimeframeProvider>();
        services.AddSingleton<ILiquidationFeed, LiquidationTracker>();
        services.AddSingleton<IDerivativesDataProvider, BinanceDerivativesProvider>();
        services.AddSingleton<ISentimentProvider, FreeSentimentProvider>();
        services.AddSingleton<IMacroDataProvider, FreeMacroProvider>();
        services.AddSingleton<IOnchainDataProvider, FreeOnchainProvider>();
        services.AddSingleton<IEconomicCalendarProvider, ForexFactoryCalendarProvider>();
        services.AddSingleton<ILlmDecisionValidator, ClaudeDecisionValidator>();
        services.AddSingleton<IAdaptiveWeightService, AdaptiveWeightService>();
        services.AddSingleton<IConnectionTester, ConnectionTester>();
        services.AddSingleton<ILatestDecisionStore, LatestDecisionStore>();
        services.AddSingleton<IAiUsageTracker, AiUsageTracker>();
        services.AddSingleton<IAiDecisionService, AiDecisionService>();
        return services;
    }
}
