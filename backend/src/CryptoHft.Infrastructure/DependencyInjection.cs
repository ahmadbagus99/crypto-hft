using CryptoHft.Application.Abstractions;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Risk;
using CryptoHft.Application.Trading;
using CryptoHft.Infrastructure.Binance;
using CryptoHft.Infrastructure.Persistence;
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
        return services;
    }
}
