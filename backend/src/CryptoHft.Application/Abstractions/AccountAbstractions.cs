using CryptoHft.Application.Account;

namespace CryptoHft.Application.Abstractions;

public interface IFuturesAccountClient
{
    Task<IReadOnlyList<FuturesWalletBalance>> GetWalletBalancesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<FuturesPositionInfo>> GetPositionsAsync(string? symbol, CancellationToken cancellationToken);
}
