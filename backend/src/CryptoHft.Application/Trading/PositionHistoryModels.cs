using CryptoHft.Application.Account;

namespace CryptoHft.Application.Trading;

public interface IPositionHistoryService
{
    Task ObserveAsync(string symbol, FuturesPositionInfo? openPosition, CancellationToken cancellationToken);
}
