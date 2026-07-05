using CryptoHft.Application.DecisionEngine;
using Xunit;

namespace CryptoHft.Tests;

// Volume profile: histogram construction (POC / value area / HVN / LVN) and the TP/SL
// geometry rules — TP pulled in front of a high-volume wall, SL tucked out of a thin
// LVN behind an HVN shelf, both with their guardrails. The profile never votes on
// direction; these tests only cover geometry and context.
public sealed class VolumeProfileTests
{
    private static Candle Bar(decimal low, decimal high, decimal volume)
        => new(DateTimeOffset.UtcNow, low, high, low, (low + high) / 2m, volume);

    // Two-node market: heavy trading around 99-101 (dominant), a thin fast leg up, and a
    // secondary but still heavy node around 109-111. The traversed middle stays LVN.
    private static List<Candle> TwoNodeMarket()
    {
        var candles = new List<Candle>();
        for (var i = 0; i < 60; i++) candles.Add(Bar(99m, 101m, 300m));   // dominant node ~100
        for (var i = 0; i < 5; i++)                                       // thin leg 101 -> 109
            candles.Add(Bar(101m + i * 1.6m, 102.6m + i * 1.6m, 20m));
        for (var i = 0; i < 15; i++) candles.Add(Bar(109m, 111m, 900m));  // secondary node ~110
        return candles;
    }

    [Fact]
    public void Build_FindsPocInsideDominantNode()
    {
        var profile = VolumeProfile.Build(TwoNodeMarket());
        Assert.NotNull(profile);
        Assert.InRange(profile!.Poc, 98.5m, 101.5m);
    }

    [Fact]
    public void Build_MarksDominantNodeAsHvn_AndThinLegAsLvn()
    {
        var profile = VolumeProfile.Build(TwoNodeMarket())!;
        Assert.Contains(profile.HvnPrices, h => h is >= 99m and <= 101m);   // dominant node
        Assert.Contains(profile.HvnPrices, h => h is >= 109m and <= 111m);  // secondary node
        Assert.Contains(profile.LvnPrices, l => l is >= 103m and <= 107m);  // thin traverse
    }

    [Fact]
    public void Build_ValueAreaSurroundsPoc()
    {
        var profile = VolumeProfile.Build(TwoNodeMarket())!;
        Assert.True(profile.Val <= profile.Poc);
        Assert.True(profile.Vah >= profile.Poc);
    }

    [Fact]
    public void Build_RequiresEnoughCandles()
        => Assert.Null(VolumeProfile.Build(TwoNodeMarket().Take(10).ToList()));

    // ---- TP snapping ---------------------------------------------------------------------

    [Fact]
    public void LongTp_BeyondHvnWall_SnapsInFrontOfIt()
    {
        var profile = VolumeProfile.Build(TwoNodeMarket())!;
        // Long from the thin middle targeting just past the ~109-111 wall.
        var (snapped, note) = VolumeProfile.SnapTakeProfit(profile, isBuy: true, entry: 104m, takeProfit: 111m, atr: 1m);

        Assert.NotNull(snapped);
        Assert.True(snapped < 109.5m, $"snapped TP {snapped} should sit in front of the ~109-111 wall");
        Assert.True(snapped > 104m);
        Assert.Contains("HVN wall", note);
    }

    [Fact]
    public void LongTp_MostlyBlockedByEarlyWall_IsNotSnapped_ButFlagged()
    {
        var profile = VolumeProfile.Build(TwoNodeMarket())!;
        // The ~109 wall kills most of a 104 -> 125 run: keep the TP, surface a caution.
        var (snapped, note) = VolumeProfile.SnapTakeProfit(profile, isBuy: true, entry: 104m, takeProfit: 125m, atr: 1m);

        Assert.Null(snapped);
        Assert.NotNull(note);
        Assert.Contains("optimistic", note);
    }

    [Fact]
    public void Tp_WithClearPath_IsUntouched()
    {
        var profile = VolumeProfile.Build(TwoNodeMarket())!;
        // Short through the thin middle: no HVN between entry and target.
        var (snapped, note) = VolumeProfile.SnapTakeProfit(profile, isBuy: false, entry: 104m, takeProfit: 102.5m, atr: 1m);

        Assert.Null(snapped);
        Assert.Null(note);
    }

    // ---- SL adjustment -------------------------------------------------------------------

    [Fact]
    public void LongSl_InThinLvn_TucksBehindHvnShelf()
    {
        var profile = VolumeProfile.Build(TwoNodeMarket())!;
        // Long from the upper node with the stop parked in the thin 102 zone; the 100
        // shelf is within the 1.3x widening cap (risk 8 -> ~9.7).
        var (adjusted, note) = VolumeProfile.AdjustStopLoss(profile, isBuy: true, entry: 110m, stopLoss: 102m, atr: 1m);

        Assert.NotNull(adjusted);
        Assert.True(adjusted < 101m, $"adjusted SL {adjusted} should sit behind the ~100 shelf");
        Assert.Contains("HVN shelf", note);
    }

    [Fact]
    public void LongSl_WhenSheltersExceedWideningCap_KeepsSl_ButWarns()
    {
        var profile = VolumeProfile.Build(TwoNodeMarket())!;
        // Stop in the thin 105 zone: risk 5, cap 6.5 — the 100 shelf (risk ~10) is too far.
        var (adjusted, note) = VolumeProfile.AdjustStopLoss(profile, isBuy: true, entry: 110m, stopLoss: 105m, atr: 1m);

        Assert.Null(adjusted);
        Assert.NotNull(note);
        Assert.Contains("sweep risk", note);
    }

    [Fact]
    public void Sl_InsideHealthyVolume_IsUntouched()
    {
        var profile = VolumeProfile.Build(TwoNodeMarket())!;
        // Stop inside the dominant node itself: defended by volume, nothing to fix.
        var (adjusted, note) = VolumeProfile.AdjustStopLoss(profile, isBuy: true, entry: 110m, stopLoss: 100m, atr: 1m);

        Assert.Null(adjusted);
        Assert.Null(note);
    }

    // ---- Confluence context -----------------------------------------------------------------

    [Fact]
    public void LongEntry_NearVal_GetsConfluenceNote()
    {
        var profile = VolumeProfile.Build(TwoNodeMarket())!;
        var note = VolumeProfile.ConfluenceNote(profile, isBuy: true, entry: profile.Val + 0.5m, atr: 1m);
        Assert.NotNull(note);
        Assert.Contains("VAL", note);
    }

    [Fact]
    public void LongEntry_FarFromVal_GetsNoNote()
    {
        var profile = VolumeProfile.Build(TwoNodeMarket())!;
        Assert.Null(VolumeProfile.ConfluenceNote(profile, isBuy: true, entry: profile.Val + 8m, atr: 1m));
    }

    // ---- Engine wiring ---------------------------------------------------------------------

    [Fact]
    public void Engine_PopulatesVolumeProfileNote()
    {
        var engine = new AdvancedDecisionEngine();
        var candles = Enumerable.Range(0, 250)
            .Select(i => new Candle(
                DateTimeOffset.UtcNow.AddHours(-250 + i),
                100_000m + i * 20m, 100_000m + i * 20m + 60m, 100_000m + i * 20m - 60m, 100_000m + i * 20m + 20m, 100m))
            .ToList();
        var input = new AdvancedDecisionInput(
            "BTCUSDT", candles[^1].Close,
            new[] { new TimeframeData("1h", candles) },
            new DerivativesSnapshot(0.0001m, 1000m, 0m, 1m, 1m, 0m, 1m),
            new SentimentSnapshot(50m, 50m, "Neutral", 50, "Neutral", Array.Empty<string>()),
            new MacroSnapshot(50m, "", false),
            new OnchainSnapshot(50m, "", false));

        var decision = engine.Evaluate(input, Profile, 10_000m);

        Assert.Contains("POC", decision.VolumeProfileNote);
        Assert.Contains(decision.Reasons, r => r.Contains("VolumeProfile:"));
    }

    private static readonly CryptoHft.Application.Risk.RiskProfile Profile = new(
        MaxDailyLoss: 5m,
        MaxConsecutiveLosses: 3,
        MaxOpenPositions: 1,
        MaxExposure: 1m,
        RiskPerTrade: 0.01m,
        MinimumRiskReward: 2m,
        AutoTradeConfidenceThreshold: 62m);
}
