using CryptoHft.Application.Account;
using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.Trading;

public interface IPositionHistoryService
{
    Task ObserveAsync(string symbol, FuturesPositionInfo? openPosition, CancellationToken cancellationToken);
    Task<LatestClosedPosition?> GetLatestClosedAsync(string symbol, CancellationToken cancellationToken);
}

public sealed record LatestClosedPosition(DateTimeOffset ClosedAt, PositionCloseReason CloseReason);
