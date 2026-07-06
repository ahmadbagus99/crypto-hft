using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;
using Xunit;

namespace CryptoHft.Tests;

// The ratcheting trailing stop: never touches the stop below +1R, moves it to breakeven+fees
// at +1R, trails a full R beyond that, only ever tightens (in MinStepR increments), and
// hands the exit over to the TP order once the mark is close to it.
// Base geometry: long entry 100k, initial SL 98k (R = 2000), TP 104k (2R).
public sealed class TrailingStopPolicyTests
{
    private const decimal Entry = 100_000m;
    private const decimal InitialSl = 98_000m;
    private const decimal Tp = 104_000m;

    [Fact]
    public void BelowActivation_StopIsNeverTouched()
    {
        var verdict = TrailingStopPolicy.Evaluate(
            TradeSide.Long, Entry, markPrice: 101_900m, InitialSl, currentStopLoss: InitialSl, Tp);
        Assert.Null(verdict.NewStopLoss);
    }

    [Fact]
    public void AtOneR_StopMovesToBreakevenPlusFees()
    {
        // Trailed stop (mark - 1R) equals entry exactly; the breakeven+fee floor wins.
        var verdict = TrailingStopPolicy.Evaluate(
            TradeSide.Long, Entry, markPrice: 102_000m, InitialSl, currentStopLoss: InitialSl, Tp);
        Assert.Equal(100_120m, verdict.NewStopLoss); // entry * (1 + 0.0012)
    }

    [Fact]
    public void DeeperProfit_TrailsOneRBehindMark()
    {
        var verdict = TrailingStopPolicy.Evaluate(
            TradeSide.Long, Entry, markPrice: 103_000m, InitialSl, currentStopLoss: 100_120m, Tp);
        Assert.Equal(101_000m, verdict.NewStopLoss);
    }

    [Fact]
    public void SmallImprovement_IsSkipped()
    {
        // Only +200 over the current stop (< 0.15R = 300): not worth an exchange amendment.
        var verdict = TrailingStopPolicy.Evaluate(
            TradeSide.Long, Entry, markPrice: 103_200m, InitialSl, currentStopLoss: 101_000m, Tp);
        Assert.Null(verdict.NewStopLoss);
    }

    [Fact]
    public void Retrace_NeverLoosensTheStop()
    {
        // Mark fell back after the stop reached 101k; the candidate is below it — no action.
        var verdict = TrailingStopPolicy.Evaluate(
            TradeSide.Long, Entry, markPrice: 102_500m, InitialSl, currentStopLoss: 101_000m, Tp);
        Assert.Null(verdict.NewStopLoss);
    }

    [Fact]
    public void NearTakeProfit_LeavesTheExitToTheTpOrder()
    {
        // 400 from TP (< 0.25R = 500): stop stops moving, the TP order owns the exit.
        var verdict = TrailingStopPolicy.Evaluate(
            TradeSide.Long, Entry, markPrice: 103_600m, InitialSl, currentStopLoss: 101_000m, Tp);
        Assert.Null(verdict.NewStopLoss);
    }

    [Fact]
    public void Short_MirrorsTheLongBehavior()
    {
        // Short entry 100k, SL 102k, TP 96k; at +1R the breakeven floor (entry - fees) wins.
        var verdict = TrailingStopPolicy.Evaluate(
            TradeSide.Short, Entry, markPrice: 98_000m,
            initialStopLoss: 102_000m, currentStopLoss: 102_000m, takeProfit: 96_000m);
        Assert.Equal(99_880m, verdict.NewStopLoss); // entry * (1 - 0.0012)
    }

    [Fact]
    public void MissingInitialStop_DerivesRiskFromTakeProfit()
    {
        // Restart mid-position: initial SL unknown; R falls back to |TP - entry| / 2 = 2000.
        var verdict = TrailingStopPolicy.Evaluate(
            TradeSide.Long, Entry, markPrice: 102_000m, initialStopLoss: null, currentStopLoss: null, Tp);
        Assert.Equal(100_120m, verdict.NewStopLoss);
    }

    [Fact]
    public void RatchetedInitialStop_AfterRestart_KeepsTrailing()
    {
        // The remembered "initial" stop already sits on the profit side (captured after a
        // restart): entry->SL distance is invalid, so R again derives from the TP.
        var verdict = TrailingStopPolicy.Evaluate(
            TradeSide.Long, Entry, markPrice: 103_000m,
            initialStopLoss: 100_500m, currentStopLoss: 100_500m, Tp);
        Assert.Equal(101_000m, verdict.NewStopLoss);
    }

    [Fact]
    public void NoRiskUnitResolvable_DoesNothing()
    {
        var verdict = TrailingStopPolicy.Evaluate(
            TradeSide.Long, Entry, markPrice: 103_000m, initialStopLoss: null, currentStopLoss: null, takeProfit: null);
        Assert.Null(verdict.NewStopLoss);
    }

    [Fact]
    public void DegeneratePrices_DoNothing()
    {
        Assert.Null(TrailingStopPolicy.Evaluate(
            TradeSide.Long, entryPrice: 0m, markPrice: 103_000m, InitialSl, InitialSl, Tp).NewStopLoss);
        Assert.Null(TrailingStopPolicy.Evaluate(
            TradeSide.Long, Entry, markPrice: 0m, InitialSl, InitialSl, Tp).NewStopLoss);
    }
}
