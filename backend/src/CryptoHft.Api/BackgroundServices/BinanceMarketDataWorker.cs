using CryptoHft.Application.Abstractions;

namespace CryptoHft.Api.BackgroundServices;

public sealed class BinanceMarketDataWorker(
    IMarketDataStream marketDataStream,
    ILogger<BinanceMarketDataWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting Binance realtime market data worker");
        await marketDataStream.RunAsync(stoppingToken);
    }
}
