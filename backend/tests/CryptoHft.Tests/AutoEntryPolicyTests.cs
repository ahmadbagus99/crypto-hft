using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;
using Xunit;

namespace CryptoHft.Tests;

public sealed class AutoEntryPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Confirmation_FirstMarginalSignalWaits()
    {
        var verdict = AutoEntryPolicy.EvaluateConfirmation(Decision(TradeSide.Long, 60m), 60m, null, T0);

        Assert.Equal(AutoEntryConfirmationAction.Wait, verdict.Action);
        Assert.Equal(TradeSide.Long, verdict.Candidate?.Side);
    }

    [Fact]
    public void Confirmation_SameDirectionAfterTwoMinutesIsReady()
    {
        var candidate = new AutoEntryCandidate(TradeSide.Long, T0);

        var verdict = AutoEntryPolicy.EvaluateConfirmation(
            Decision(TradeSide.Long, 61m), 60m, candidate, T0.AddMinutes(2));

        Assert.Equal(AutoEntryConfirmationAction.Ready, verdict.Action);
        Assert.Equal(TradeSide.Long, verdict.Side);
    }

    [Fact]
    public void Confirmation_SameDirectionBeforeTwoMinutesWaits()
    {
        var candidate = new AutoEntryCandidate(TradeSide.Long, T0);

        var verdict = AutoEntryPolicy.EvaluateConfirmation(
            Decision(TradeSide.Long, 61m), 60m, candidate, T0.AddSeconds(90));

        Assert.Equal(AutoEntryConfirmationAction.Wait, verdict.Action);
    }

    [Fact]
    public void Confirmation_OppositeDirectionRestartsWindow()
    {
        var candidate = new AutoEntryCandidate(TradeSide.Long, T0);

        var verdict = AutoEntryPolicy.EvaluateConfirmation(
            Decision(TradeSide.Short, 61m), 60m, candidate, T0.AddMinutes(6));

        Assert.Equal(AutoEntryConfirmationAction.Wait, verdict.Action);
        Assert.Equal(TradeSide.Short, verdict.Candidate?.Side);
        Assert.Equal(T0.AddMinutes(6), verdict.Candidate?.FirstSeenAt);
    }

    [Fact]
    public void Confirmation_StrongSignalIsReadyImmediately()
    {
        var verdict = AutoEntryPolicy.EvaluateConfirmation(Decision(TradeSide.Short, 64m), 60m, null, T0);

        Assert.Equal(AutoEntryConfirmationAction.Ready, verdict.Action);
        Assert.Equal(TradeSide.Short, verdict.Side);
    }

    [Fact]
    public void Confirmation_ExpiredCandidateDoesNotConfirm()
    {
        var candidate = new AutoEntryCandidate(TradeSide.Long, T0);

        var verdict = AutoEntryPolicy.EvaluateConfirmation(
            Decision(TradeSide.Long, 61m), 60m, candidate, T0.AddMinutes(16));

        Assert.Equal(AutoEntryConfirmationAction.Wait, verdict.Action);
        Assert.Equal(T0.AddMinutes(16), verdict.Candidate?.FirstSeenAt);
    }

    // A hesitant Claude no longer raises the entry bar (HesitantSignalOffset = 0): it
    // still downsizes the trade through its size multiplier, but any signal that clears
    // the normal threshold may proceed.
    [Fact]
    public void Llm_HesitantSignalAtNormalThresholdIsAllowed()
    {
        var decision = Decision(TradeSide.Long, 60m, llmUsed: true, llmConfirmed: false);

        var verdict = AutoEntryPolicy.EvaluateLlm(decision, TradeSide.Long, 60m);

        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void Llm_SignalBelowThresholdIsBlockedEvenWhenConfirmed()
    {
        var decision = Decision(TradeSide.Long, 59m, llmUsed: true, llmConfirmed: true);

        var verdict = AutoEntryPolicy.EvaluateLlm(decision, TradeSide.Long, 60m);

        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void Llm_ConfirmedSignalUsesNormalThreshold()
    {
        var decision = Decision(TradeSide.Long, 60m, llmUsed: true, llmConfirmed: true);

        var verdict = AutoEntryPolicy.EvaluateLlm(decision, TradeSide.Long, 60m);

        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void Llm_UnavailableRetainsConfirmedRuleBasedSignal()
    {
        var decision = Decision(TradeSide.Long, 60m, llmUsed: false);

        var verdict = AutoEntryPolicy.EvaluateLlm(decision, TradeSide.Long, 60m);

        Assert.True(verdict.Allowed);
    }

    [Fact]
    public void Llm_DirectionChangeIsBlocked()
    {
        var decision = Decision(TradeSide.Short, 70m, llmUsed: true, llmConfirmed: true);

        var verdict = AutoEntryPolicy.EvaluateLlm(decision, TradeSide.Long, 60m);

        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void Cooldown_StopLossIsLongerThanSuccessfulExit()
    {
        Assert.Equal(TimeSpan.FromMinutes(60), AutoEntryPolicy.CooldownFor(PositionCloseReason.StopLoss));
        Assert.Equal(TimeSpan.FromMinutes(60), AutoEntryPolicy.CooldownFor(PositionCloseReason.Unknown));
        Assert.Equal(TimeSpan.FromMinutes(30), AutoEntryPolicy.CooldownFor(PositionCloseReason.TakeProfit));
        Assert.Equal(TimeSpan.FromMinutes(30), AutoEntryPolicy.CooldownFor(PositionCloseReason.TrailingStop));
    }

    // ---- Scalper timing ------------------------------------------------------------------------

    [Fact]
    public void Scalper_ConfirmationReadyAfterThirtySeconds()
    {
        var candidate = new AutoEntryCandidate(TradeSide.Long, T0);

        var verdict = AutoEntryPolicy.EvaluateConfirmation(
            Decision(TradeSide.Long, 61m), 60m, candidate, T0.AddSeconds(30), AutoEntryTiming.Scalper);

        Assert.Equal(AutoEntryConfirmationAction.Ready, verdict.Action);
    }

    [Fact]
    public void Scalper_IntradayDelayWouldStillBeWaiting()
    {
        var candidate = new AutoEntryCandidate(TradeSide.Long, T0);

        var verdict = AutoEntryPolicy.EvaluateConfirmation(
            Decision(TradeSide.Long, 61m), 60m, candidate, T0.AddSeconds(30), AutoEntryTiming.Intraday);

        Assert.Equal(AutoEntryConfirmationAction.Wait, verdict.Action);
    }

    [Fact]
    public void Scalper_CooldownsAreCompressedButPresent()
    {
        Assert.Equal(TimeSpan.FromMinutes(20), AutoEntryPolicy.CooldownFor(PositionCloseReason.StopLoss, AutoEntryTiming.Scalper));
        Assert.Equal(TimeSpan.FromMinutes(10), AutoEntryPolicy.CooldownFor(PositionCloseReason.TakeProfit, AutoEntryTiming.Scalper));
    }

    [Fact]
    public void Scalper_TimingResolvesFromStyle()
    {
        Assert.Same(AutoEntryTiming.Scalper, AutoEntryTiming.For(TradingStyle.Scalper));
        Assert.Same(AutoEntryTiming.Intraday, AutoEntryTiming.For(TradingStyle.Intraday));
    }

    private static AdvancedDecision Decision(
        TradeSide side,
        decimal confidence,
        bool llmUsed = false,
        bool llmConfirmed = true)
    {
        var buy = side == TradeSide.Long ? confidence : 100m - confidence;
        var sell = side == TradeSide.Short ? confidence : 100m - confidence;
        return new AdvancedDecision(
            Symbol: "BTCUSDT",
            Action: side == TradeSide.Long ? DecisionAction.WeakBuy : DecisionAction.WeakSell,
            Confidence: confidence,
            ConfidenceBuy: buy,
            ConfidenceSell: sell,
            ConfidenceHold: 0m,
            ProbabilityOfSuccess: 0m,
            Regime: MarketRegime.Trending,
            EntryPrice: 100m,
            StopLoss: side == TradeSide.Long ? 90m : 110m,
            TakeProfit: side == TradeSide.Long ? 120m : 80m,
            TrailingStopPercent: 1m,
            RiskReward: 2m,
            PositionSizeQuantity: 1m,
            Leverage: 3,
            ShouldTrade: true,
            NoTradeReason: "",
            Cautions: Array.Empty<string>(),
            Scores: new Dictionary<string, decimal>(),
            Weights: new Dictionary<string, decimal>(),
            Components: Array.Empty<ScoreComponent>(),
            Reasons: Array.Empty<string>(),
            Llm: new LlmValidation(llmConfirmed, confidence, "", Array.Empty<string>(), llmUsed),
            Time: T0);
    }
}
