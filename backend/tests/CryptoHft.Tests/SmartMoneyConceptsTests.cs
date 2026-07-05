using CryptoHft.Application.DecisionEngine;
using Xunit;

namespace CryptoHft.Tests;

// Covers the deepened SMC module: fractal swing detection, break of structure (BOS),
// change of character (CHoCH), order-block mitigation/relevance, and premium/discount
// positioning inside the dealing range.
public sealed class SmartMoneyConceptsTests
{
    // Candle whose wick extends 0.5 beyond the close side but only 0.2 beyond the open
    // side. Adjacent candles share open/close prices, so symmetric wicks would produce
    // EQUAL highs at every peak and defeat the strict fractal comparison — the asymmetry
    // keeps the extreme candle's wick unique.
    private static Candle Bar(decimal open, decimal close)
        => new(DateTimeOffset.UtcNow,
            open,
            Math.Max(open, close) + (close >= open ? 0.5m : 0.2m),
            Math.Min(open, close) - (close <= open ? 0.5m : 0.2m),
            close,
            100m);

    // Flat filler bars that produce no swings (equal highs/lows fail the strict fractal test).
    private static List<Candle> Flat(int count, decimal price)
        => Enumerable.Range(0, count).Select(_ => Bar(price, price)).ToList();

    // Sequence of closes as candles; each opens at the previous close.
    private static List<Candle> FromCloses(decimal start, params decimal[] closes)
    {
        var candles = new List<Candle>();
        var prev = start;
        foreach (var close in closes)
        {
            candles.Add(Bar(prev, close));
            prev = close;
        }
        return candles;
    }

    // Uptrend with clean structure: swing high 116 area, higher low, higher high 118
    // area, higher low — then the final candle either breaks out (BOS) or collapses
    // through the last higher low (CHoCH). The two candles between the higher low and
    // the resolution confirm the swing (a fractal needs `wing` closes on each side).
    private static List<Candle> UptrendStructure(decimal finalClose)
        => FromCloses(100,
            104, 108, 112, 116,   // leg up -> swing high near 116.5
            112, 108, 106,        // pullback -> swing low near 105.5
            110, 114, 118,        // higher high near 118.5
            114, 110,             // higher low near 109.5
            112, 113,             // swing confirmation
            finalClose);          // resolution candle

    [Fact]
    public void Swings_DetectFractalHighsAndLows()
    {
        var candles = UptrendStructure(112);
        var swings = SmartMoneyConcepts.DetectSwings(candles);

        Assert.Contains(swings, s => s.IsHigh && s.Price == 118.5m); // higher high
        Assert.Contains(swings, s => !s.IsHigh && s.Price == 105.5m); // first low
        Assert.Contains(swings, s => !s.IsHigh && s.Price == 109.5m); // higher low
    }

    [Fact]
    public void BreakAboveHigherHigh_InUptrend_IsBullishBos()
    {
        var candles = UptrendStructure(121);
        var (bullBos, bearBos, bullChoch, bearChoch) =
            SmartMoneyConcepts.DetectStructureBreaks(candles, SmartMoneyConcepts.DetectSwings(candles));

        Assert.True(bullBos);
        Assert.False(bearBos);
        Assert.False(bullChoch);
        Assert.False(bearChoch);
    }

    [Fact]
    public void BreakBelowHigherLow_InUptrend_IsBearishChoch()
    {
        var candles = UptrendStructure(104); // below the 109.5 higher low
        var (bullBos, _, _, bearChoch) =
            SmartMoneyConcepts.DetectStructureBreaks(candles, SmartMoneyConcepts.DetectSwings(candles));

        Assert.True(bearChoch);
        Assert.False(bullBos);
    }

    [Fact]
    public void HoldingInsideStructure_BreaksNothing()
    {
        var candles = UptrendStructure(112); // between higher low and higher high
        var (bullBos, bearBos, bullChoch, bearChoch) =
            SmartMoneyConcepts.DetectStructureBreaks(candles, SmartMoneyConcepts.DetectSwings(candles));

        Assert.False(bullBos);
        Assert.False(bearBos);
        Assert.False(bullChoch);
        Assert.False(bearChoch);
    }

    [Fact]
    public void ReclaimingLowerHigh_InDowntrend_IsBullishChoch()
    {
        // Downtrend: LH + LL, then the final close reclaims the last lower high.
        var candles = FromCloses(120,
            116, 112, 108,
            112, 116,             // first swing high near 116.5
            112, 108, 104,        // swing low near 103.5
            108, 112, 114,        // bounce -> lower high near 114.5
            110, 106, 102,        // lower low near 101.5
            106, 110,             // fade
            108, 117);            // reclaim above 114.5
        var (_, _, bullChoch, _) =
            SmartMoneyConcepts.DetectStructureBreaks(candles, SmartMoneyConcepts.DetectSwings(candles));

        Assert.True(bullChoch);
    }

    // ---- Order block mitigation ------------------------------------------------------------

    private static List<Candle> ImpulseWithOrderBlock(bool mitigate)
    {
        var candles = Flat(14, 100m);
        candles.Add(Bar(100m, 99m));   // the down candle: bullish OB zone 99..100
        candles.Add(Bar(99m, 106m));   // strong up impulse away from the zone
        candles.Add(Bar(106m, 105m));
        if (mitigate)
            candles.Add(new Candle(DateTimeOffset.UtcNow, 105m, 105.5m, 99.5m, 104m, 100m)); // dips back into the zone
        else
            candles.Add(Bar(105m, 104m));
        candles.Add(Bar(104m, 104m));
        return candles;
    }

    [Fact]
    public void FreshOrderBlock_Counts()
    {
        var candles = ImpulseWithOrderBlock(mitigate: false);
        var atr = TechnicalIndicators.Atr(candles)[^1];
        var (bull, _) = SmartMoneyConcepts.DetectOrderBlocks(candles, atr);
        Assert.True(bull);
    }

    [Fact]
    public void MitigatedOrderBlock_IsSpent()
    {
        var candles = ImpulseWithOrderBlock(mitigate: true);
        var atr = TechnicalIndicators.Atr(candles)[^1];
        var (bull, _) = SmartMoneyConcepts.DetectOrderBlocks(candles, atr);
        Assert.False(bull);
    }

    [Fact]
    public void FarAwayOrderBlock_IsIrrelevant()
    {
        // Same fresh OB, but price has run far beyond the 5xATR reach.
        var candles = ImpulseWithOrderBlock(mitigate: false);
        candles.Add(Bar(104m, 140m));
        candles.Add(Bar(140m, 141m));
        var atr = TechnicalIndicators.Atr(candles)[^1];
        var (bull, _) = SmartMoneyConcepts.DetectOrderBlocks(candles, atr);
        Assert.False(bull);
    }

    // ---- Premium / discount -------------------------------------------------------------------

    [Fact]
    public void CloseNearRangeLow_IsDiscount()
    {
        var candles = FromCloses(100, 110, 120, 130, 128, 118, 108, 102);
        Assert.True(SmartMoneyConcepts.RangePosition(candles) < 0.4m);
    }

    [Fact]
    public void CloseNearRangeHigh_IsPremium()
    {
        var candles = FromCloses(100, 104, 96, 108, 112, 120, 126, 129);
        Assert.True(SmartMoneyConcepts.RangePosition(candles) > 0.6m);
    }

    [Fact]
    public void FlatRange_IsEquilibrium()
        => Assert.Equal(0.5m, SmartMoneyConcepts.RangePosition(Flat(30, 100m)));

    // ---- End-to-end smoke -----------------------------------------------------------------------

    [Fact]
    public void Detect_BullishBosStructure_ScoresBullish()
    {
        var candles = Flat(10, 100m).Concat(UptrendStructure(121)).ToList();
        var signals = SmartMoneyConcepts.Detect(candles);

        Assert.True(signals.BullishBos);
        Assert.True(signals.Score > 50m, $"expected bullish score, got {signals.Score}");
        Assert.Contains("bullish BOS", signals.Summary);
    }
}
