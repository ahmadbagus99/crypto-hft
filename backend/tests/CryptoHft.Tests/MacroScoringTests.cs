using CryptoHft.Infrastructure.Ai;
using Xunit;

namespace CryptoHft.Tests;

// The macro score pinned at exactly 100 in production on an ordinary risk-on week
// (SPX +3.1%, NDX +5.5%, DXY -1.5%, Gold +2.8% put the raw score at 105). Coefficients
// are now sized against two years of measured 5-day moves, and each input carries its
// own budget so no single series can pin the category.
public sealed class MacroScoringTests
{
    // Measured over ~500 sessions: p90 of the 5-day absolute change.
    private const decimal EquityP90 = 4.1m;   // average of SPX 3.43 and NDX 4.83
    private const decimal DollarP90 = 1.49m;
    private const decimal GoldP90 = 5.02m;

    private static decimal Equity(decimal pct) =>
        FreeMacroProvider.Contribution(pct, FreeMacroProvider.EquityCoefficient, FreeMacroProvider.EquityBudget);

    private static decimal Dollar(decimal pct) =>
        FreeMacroProvider.Contribution(pct, FreeMacroProvider.DollarCoefficient, FreeMacroProvider.DollarBudget);

    private static decimal Gold(decimal pct) =>
        FreeMacroProvider.Contribution(pct, FreeMacroProvider.GoldCoefficient, FreeMacroProvider.GoldBudget);

    // A p90 week is strong but not once-in-two-years; it should approach the budget
    // without hitting it, leaving headroom for genuinely extreme readings to score higher.
    [Fact]
    public void P90Move_LandsNearBudgetWithoutSaturating()
    {
        Assert.InRange(Equity(EquityP90), FreeMacroProvider.EquityBudget * 0.7m, FreeMacroProvider.EquityBudget * 0.95m);
        Assert.InRange(Dollar(DollarP90), FreeMacroProvider.DollarBudget * 0.7m, FreeMacroProvider.DollarBudget * 0.95m);
        Assert.InRange(Gold(GoldP90), FreeMacroProvider.GoldBudget * 0.7m, FreeMacroProvider.GoldBudget * 0.95m);
    }

    // The exact production reading that produced a flat 100 must now land inside the
    // range, so the category still carries information when conditions are merely strong.
    [Fact]
    public void TheReadingThatPinnedTheScore_NoLongerSaturates()
    {
        var equity = (3.1m + 5.5m) / 2m;
        var score = 50m + Equity(equity) - Dollar(-1.5m) + Gold(2.8m);

        Assert.True(score < 95m, $"score {score} should stay clear of the ceiling");
        Assert.True(score > 70m, $"score {score} should still read clearly risk-on");
    }

    // No single series may pin the category on its own — that was the saturation defect.
    [Theory]
    [InlineData(50)]
    [InlineData(-50)]
    public void ExtremeSingleInput_IsBounded(decimal absurdPercent)
    {
        Assert.Equal(FreeMacroProvider.EquityBudget, Math.Abs(Equity(absurdPercent)));
        Assert.Equal(FreeMacroProvider.DollarBudget, Math.Abs(Dollar(absurdPercent)));
        Assert.Equal(FreeMacroProvider.GoldBudget, Math.Abs(Gold(absurdPercent)));

        // Even with every input maxed the same way, the score stays inside 0-100.
        var maxed = 50m + FreeMacroProvider.EquityBudget + FreeMacroProvider.DollarBudget + FreeMacroProvider.GoldBudget;
        Assert.True(maxed <= 100m, $"combined budgets {maxed - 50m} must fit the 50-point headroom");
    }

    [Fact]
    public void QuietWeek_ReadsNeutral()
    {
        var score = 50m + Equity(0.2m) - Dollar(0.1m) + Gold(0.3m);
        Assert.InRange(score, 48m, 52m);
    }
}
