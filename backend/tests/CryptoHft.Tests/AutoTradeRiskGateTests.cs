using CryptoHft.Application.Trading;
using CryptoHft.Infrastructure.Trading;
using Xunit;

namespace CryptoHft.Tests;

public sealed class AutoTradeRiskGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(120);

    private static RealizedPnlEntry E(int minutesAfterT0, decimal pnl)
        => new(T0.AddMinutes(minutesAfterT0), pnl);

    private static RuntimeTradingSettings Settings(
        decimal maxDailyLossPercent = 0.10m,
        bool accountRiskGuardEnabled = true)
        => new(
            PaperTradingOnly: false,
            AutoTradingEnabled: true,
            MaxDailyLossPercent: maxDailyLossPercent,
            RiskPerTradePercent: 0.01m,
            MaxExposurePercent: 0.50m,
            DefaultLeverage: 20,
            AutoSizingMode: 1,
            TargetLeverage: 20,
            ApiKey: "key",
            ApiSecret: "secret",
            AnthropicApiKey: null,
            AiModel: null,
            ConfidenceThreshold: 60m,
            PositionCheckIntervalMinutes: 10,
            TrailingStopDistanceR: 0.5m,
            LunarCrushApiKey: null,
            TargetMarginUsdt: 7m,
            AccountRiskGuardEnabled: accountRiskGuardEnabled);

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

    [Fact]
    public void NextUtcDay_ReturnsNextMidnightUtc()
    {
        var now = new DateTimeOffset(2026, 7, 21, 18, 42, 0, TimeSpan.Zero);

        var reset = BinanceAutoTradeRiskGate.NextUtcDay(now);

        Assert.Equal(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero), reset);
    }

    [Fact]
    public void ResolveAccountStatus_PausesUntilNextUtcDay_WhenDailyLossLimitReached()
    {
        var checkedAt = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

        var status = BinanceAutoTradeRiskGate.ResolveAccountStatus(
            Settings(),
            equity: 10m,
            todaysPnl: new[] { E(0, -1.25m) },
            checkedAt: checkedAt);

        Assert.False(status.TradingAllowed);
        Assert.Equal("daily-loss", status.Status);
        Assert.Equal(1.25m, status.DailyLoss);
        Assert.Equal(1m, status.DailyLossLimit);
        Assert.Equal(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero), status.ResetsAt);
    }

    // A run of losses no longer stops the day: the daily limit already bounds what a bad run
    // can cost, and it bounds it in money. A count of trades halts on three scratches worth
    // -0.05 each while a single -1.50 passes untouched — same number, nothing like the same
    // damage. The streak is still counted and surfaced; it just decides nothing.
    [Fact]
    public void ResolveAccountStatus_KeepsTrading_ThroughALosingStreakWithDailyRoomLeft()
    {
        var entries = new[] { E(0, 5m), E(10, -1m), E(20, -1m), E(30, -1m) };

        var status = BinanceAutoTradeRiskGate.ResolveAccountStatus(
            Settings(maxDailyLossPercent: 0.50m),
            equity: 10m,
            todaysPnl: entries,
            checkedAt: T0);

        Assert.True(status.TradingAllowed);
        Assert.Equal("active", status.Status);
        Assert.Equal(3, status.ConsecutiveLosses);   // still reported for the dashboard
        Assert.Null(status.ResetsAt);
    }

    // The money limit is the one that stops the day, streak or no streak.
    [Fact]
    public void ResolveAccountStatus_StopsOnTheDailyLimit_EvenWithoutAStreak()
    {
        var entries = new[] { E(0, -6m), E(10, 1m) };

        var status = BinanceAutoTradeRiskGate.ResolveAccountStatus(
            Settings(maxDailyLossPercent: 0.50m),
            equity: 10m,
            todaysPnl: entries,
            checkedAt: T0);

        Assert.False(status.TradingAllowed);
        Assert.Equal("daily-loss", status.Status);
        Assert.Equal(0, status.ConsecutiveLosses);   // last trade was a win
        Assert.NotNull(status.ResetsAt);
    }

    [Fact]
    public void ResolveAccountStatus_IsActive_WhenLimitsHaveRoom()
    {
        var status = BinanceAutoTradeRiskGate.ResolveAccountStatus(
            Settings(),
            equity: 10m,
            todaysPnl: new[] { E(0, -0.25m) },
            checkedAt: T0);

        Assert.True(status.TradingAllowed);
        Assert.Equal("active", status.Status);
        Assert.Null(status.ResetsAt);
    }

    // Owner switch: with the guard disabled, tripped limits report but never block.
    [Fact]
    public void ResolveAccountStatus_GuardOff_AllowsTradingPastDailyLossLimit()
    {
        var status = BinanceAutoTradeRiskGate.ResolveAccountStatus(
            Settings(accountRiskGuardEnabled: false),
            equity: 10m,
            todaysPnl: new[] { E(0, -1.25m) },
            checkedAt: T0);

        Assert.True(status.TradingAllowed);
        Assert.Equal("guard-off", status.Status);
        // Stats still surface so the dashboard shows what the guard WOULD do.
        Assert.Equal(1.25m, status.DailyLoss);
    }

    // With the guard off the account reads are best-effort, so equity can be absent.
    // That must still not block: an unreadable balance is exactly the situation the
    // owner disabled the brakes for, and it is reported as null rather than a fake 0.
    [Fact]
    public void ResolveAccountStatus_GuardOff_AllowsTradingWhenEquityIsUnknown()
    {
        var status = BinanceAutoTradeRiskGate.ResolveAccountStatus(
            Settings(accountRiskGuardEnabled: false),
            equity: null,
            todaysPnl: Array.Empty<RealizedPnlEntry>(),
            checkedAt: T0);

        Assert.True(status.TradingAllowed);
        Assert.Equal("guard-off", status.Status);
        Assert.Null(status.Equity);
    }

    [Fact]
    public void ResolveAccountStatus_GuardOff_AllowsTradingPastConsecutiveLosses()
    {
        var entries = new[] { E(0, 5m), E(10, -1m), E(20, -1m), E(30, -1m) };

        var status = BinanceAutoTradeRiskGate.ResolveAccountStatus(
            Settings(maxDailyLossPercent: 0.50m, accountRiskGuardEnabled: false),
            equity: 10m,
            todaysPnl: entries,
            checkedAt: T0);

        Assert.True(status.TradingAllowed);
        Assert.Equal("guard-off", status.Status);
        Assert.Equal(3, status.ConsecutiveLosses);
    }
}
