using CryptoHft.Application.Abstractions;

namespace CryptoHft.Api.BackgroundServices;

public sealed class BinanceUserDataWorker(
    IUserDataStream userDataStream,
    ILogger<BinanceUserDataWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting Binance user data worker");
        await userDataStream.RunAsync(stoppingToken);
    }
}
