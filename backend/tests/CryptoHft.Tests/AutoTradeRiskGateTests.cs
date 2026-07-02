using CryptoHft.Infrastructure.Trading;
using Xunit;

namespace CryptoHft.Tests;

public sealed class AutoTradeRiskGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(120);

    private static RealizedPnlEntry E(int minutesAfterT0, decimal pnl)
        => new(T0.AddMinutes(minutesAfterT0), pnl);

    [Fact]
    public void DailyLoss_IsZero_WhenNetPositive()
    {
        var entries = new[] { E(0, 10m), E(10, -4m) };
        Assert.Equal(0m, BinanceAutoTradeRiskGate.DailyLoss(entries));
    }

    [Fact]
    public void DailyLoss_ReturnsPositiveLoss_WhenNetNegative()
    {
        var entries = new[] { E(0, -10m), E(10, 4m), E(20, -6m) };
        Assert.Equal(12m, BinanceAutoTradeRiskGate.DailyLoss(entries));
    }

    [Fact]
    public void CountTrailingLosses_CountsConsecutiveLossesFromTail()
    {
        var entries = new[] { E(0, 5m), E(10, -1m), E(20, -2m), E(30, -3m) };
        Assert.Equal(3, BinanceAutoTradeRiskGate.CountTrailingLosses(entries, Gap));
    }

    [Fact]
    public void CountTrailingLosses_ResetsOnWin()
    {
        var entries = new[] { E(0, -1m), E(10, -2m), E(20, 5m), E(30, -3m) };
        Assert.Equal(1, BinanceAutoTradeRiskGate.CountTrailingLosses(entries, Gap));
    }

    [Fact]
    public void CountTrailingLosses_GroupsPartialFillsIntoOneTrade()
    {
        // Three fills within 120s of each other = one closing trade, not three losses.
        var entries = new[]
        {
            new RealizedPnlEntry(T0, -1m),
            new RealizedPnlEntry(T0.AddSeconds(30), -1m),
            new RealizedPnlEntry(T0.AddSeconds(60), -1m)
        };
        Assert.Equal(1, BinanceAutoTradeRiskGate.CountTrailingLosses(entries, Gap));
    }

    [Fact]
    public void CountTrailingLosses_GroupedTradeUsesNetPnl()
    {
        // A close whose fills net out positive is a win even if one fill was negative.
        var entries = new[]
        {
            new RealizedPnlEntry(T0, -1m),
            new RealizedPnlEntry(T0.AddSeconds(30), 3m)
        };
        Assert.Equal(0, BinanceAutoTradeRiskGate.CountTrailingLosses(entries, Gap));
    }

    [Fact]
    public void CountTrailingLosses_EmptyHistory_IsZero()
    {
        Assert.Equal(0, BinanceAutoTradeRiskGate.CountTrailingLosses(Array.Empty<RealizedPnlEntry>(), Gap));
    }

    [Fact]
    public void ParseIncome_ReadsStringAndNumberIncome()
    {
        var json = """
        [
          {"symbol":"BTCUSDT","incomeType":"REALIZED_PNL","income":"-1.25","time":1770000000000},
          {"symbol":"BTCUSDT","incomeType":"REALIZED_PNL","income":2.50,"time":1770000600000},
          {"symbol":"BTCUSDT","incomeType":"REALIZED_PNL","time":1770000700000}
        ]
        """;
        var entries = BinanceAutoTradeRiskGate.ParseIncome(json);
        Assert.Equal(2, entries.Count);
        Assert.Equal(-1.25m, entries[0].Pnl);
        Assert.Equal(2.50m, entries[1].Pnl);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1770000000000), entries[0].Time);
    }
}
