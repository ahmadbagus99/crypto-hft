namespace CryptoHft.Application.Account;

// The dashboard and sizing logic treat the futures account in USD terms. Binance keeps
// the margin split across several 1:1 USD stablecoins (USDT, USDC, ...), so we aggregate
// them rather than reading a single asset — otherwise a USDC-funded account shows 0.
public static class WalletBalanceExtensions
{
    private static readonly HashSet<string> UsdStableAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        "USDT", "USDC", "BUSD", "FDUSD", "TUSD", "USD1", "BFUSD", "DAI"
    };

    public static decimal UsdBalance(this IEnumerable<FuturesWalletBalance> wallets)
        => wallets.Where(w => UsdStableAssets.Contains(w.Asset)).Sum(w => w.Balance);

    public static decimal UsdAvailableBalance(this IEnumerable<FuturesWalletBalance> wallets)
        => wallets.Where(w => UsdStableAssets.Contains(w.Asset)).Sum(w => w.AvailableBalance);

    public static decimal UsdUnrealizedPnl(this IEnumerable<FuturesWalletBalance> wallets)
        => wallets.Where(w => UsdStableAssets.Contains(w.Asset)).Sum(w => w.CrossUnrealizedPnl);

    // Equity = total USD stablecoin balance + cross unrealized PnL.
    public static decimal UsdEquity(this IEnumerable<FuturesWalletBalance> wallets)
    {
        var list = wallets as IReadOnlyCollection<FuturesWalletBalance> ?? wallets.ToList();
        return list.UsdBalance() + list.UsdUnrealizedPnl();
    }
}
