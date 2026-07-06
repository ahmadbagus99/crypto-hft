using CryptoHft.Application.Account;

namespace CryptoHft.Application.Trading;

// Per-tick trade manager for the open position: ratchets the protective stop per
// TrailingStopPolicy (breakeven at +1R, then trail). Implementations must be safe to
// call every worker tick and must never throw — a guard failure may not break trading.
public interface ITrailingStopGuard
{
    Task ApplyAsync(string symbol, FuturesPositionInfo position, CancellationToken cancellationToken);
}
