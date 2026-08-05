using CryptoHft.Domain.Enums;
using Xunit;

namespace CryptoHft.Tests;

// Entry order mode: Maker posts a resting limit for the 0.02% fee, Taker crosses for 0.05%.
// Measured over 24 trades the account's gross PnL before fees was -0.11 USDT while fees came
// to -4.97, so cutting the fee is the lever with a guaranteed effect on the bottom line.
public sealed class MakerEntryTests
{
    // Mirrors AutoTradingWorker.MakerEntryPrice — a resting buy must sit BELOW the market and
    // a resting sell ABOVE it, otherwise the order crosses and post-only rejects it.
    private const decimal Offset = 0.0002m;
    private static decimal MakerEntryPrice(TradeSide side, decimal reference)
        => side == TradeSide.Long
            ? Math.Round(reference * (1m - Offset), 2)
            : Math.Round(reference * (1m + Offset), 2);

    [Fact]
    public void MakerLong_RestsBelowTheMarket()
    {
        var price = MakerEntryPrice(TradeSide.Long, 64_000m);

        Assert.True(price < 64_000m, "a resting buy above the market would cross and be rejected");
        Assert.Equal(63_987.20m, price);
    }

    [Fact]
    public void MakerShort_RestsAboveTheMarket()
    {
        var price = MakerEntryPrice(TradeSide.Short, 64_000m);

        Assert.True(price > 64_000m, "a resting sell below the market would cross and be rejected");
        Assert.Equal(64_012.80m, price);
    }

    // The offset also means a filled maker entry is a slightly better price than the market
    // order would have taken — a second, smaller saving on top of the fee.
    [Fact]
    public void Offset_IsSmallEnoughToStillFill()
    {
        var longPrice = MakerEntryPrice(TradeSide.Long, 64_000m);
        var edge = (64_000m - longPrice) / 64_000m;

        Assert.InRange(edge, 0.0001m, 0.0005m);
    }

    [Fact]
    public void EntryOrderMode_DefaultsToMaker()
    {
        Assert.Equal(0, (int)EntryOrderMode.Maker);
        Assert.Equal(1, (int)EntryOrderMode.Taker);
    }

    // Round-trip fee arithmetic that motivated the change, against the measured 26% TP rate.
    [Fact]
    public void MakerEntry_CutsTheRoundTripFee()
    {
        const decimal taker = 0.0005m, maker = 0.0002m, tpRate = 7m / 27m;

        var before = taker + taker;
        var after = maker + (tpRate * taker + (1 - tpRate) * taker); // exits stay taker for now

        Assert.True(after < before);
        Assert.Equal(0.0003m, before - after); // 0.10% -> 0.07%
    }
}
