using CryptoHft.Application.Account;
using CryptoHft.Application.MarketData;

namespace CryptoHft.Application.Abstractions;

public interface IFuturesAccountClient
{
    Task<IReadOnlyList<FuturesWalletBalance>> GetWalletBalancesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<FuturesPositionInfo>> GetPositionsAsync(string? symbol, CancellationToken cancellationToken);
    Task<IReadOnlyList<OrderUpdateEvent>> GetOrderUpdatesAsync(string? symbol, CancellationToken cancellationToken);
}
