using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;
using CryptoHft.Infrastructure.Ai;
using Xunit;

namespace CryptoHft.Tests;

// With "AI Ikut Menentukan Arah" on, Claude stops being a sizing adviser and becomes the
// auditor of the engine's read. The engine remains the gate — Claude is never asked about a
// candidate the engine rejected — but its verdict then decides whether the position opens.
// Declining costs one setup, not the strategy: the engine keeps scanning every 30 seconds.
public sealed class AiAuditorModeTests
{
    private static AdvancedDecision Proposal(
        DecisionAction action = DecisionAction.WeakBuy, decimal confidenceBuy = 58m, bool used = true,
        bool confirmed = true, int? aligned = 4, int? blocking = 1)
        => new(
            "BTCUSDT", action, action >= DecisionAction.WeakBuy ? confidenceBuy : 100m - confidenceBuy,
            confidenceBuy, 100m - confidenceBuy, 20m, 55m,
            MarketRegime.Trending, 64_800m, 64_600m, 65_200m, 0m, 2m, 0.002m, 20, true, "",
            Array.Empty<string>(), new Dictionary<string, decimal>(), new Dictionary<string, decimal>(),
            Array.Empty<ScoreComponent>(), new List<string> { "engine reason" },
            new LlmValidation(confirmed, 62m, "read the 09:15 bar rejecting 64,650 support",
                Array.Empty<string>(), used, 0.38m, null, null, null, aligned, blocking),
            DateTimeOffset.UtcNow);

    // The mandate only changes when the owner granted it.
    [Fact]
    public void AuditorPromptGrantsTheVerdictRealAuthority()
    {
        Assert.Contains("decide whether it opens", ClaudeDecisionValidator.AuditorPrompt);
        Assert.Contains("false means no", ClaudeDecisionValidator.AuditorPrompt);
    }

    // The advisory prompt must keep saying the opposite, or an unchecked setting silently
    // gains veto power.
    [Fact]
    public void AdvisoryPromptStillForbidsVetoing()
        => Assert.Contains("You cannot reject or veto the trade", ClaudeDecisionValidator.SystemPrompt);

    // The failure mode this design has to survive: an auditor that refuses everything. The
    // prompt has to say so, because a model asked "are you sure?" will always answer "not
    // quite" unless told that declining has a cost too.
    [Fact]
    public void AuditorIsToldNotToRefuseByReflex()
    {
        Assert.Contains("Do NOT deny by reflex", ClaudeDecisionValidator.AuditorPrompt);
        Assert.Contains("must be confirmed", ClaudeDecisionValidator.AuditorPrompt);
    }

    // The verdict has to be grounded in the data the payload now carries, not in a vibe.
    [Fact]
    public void AuditorMustCiteABarAndALevel()
        => Assert.Contains("which specific bar and which", ClaudeDecisionValidator.AuditorPrompt);

    // A refusal arrives at the entry policy as an unactionable signal carrying Claude's reason.
    [Fact]
    public void RefusalBlocksTheEntryAndExplainsWhy()
    {
        var declined = Proposal(action: DecisionAction.NoTrade, confidenceBuy: 58m) with
        {
            ShouldTrade = false,
            NoTradeReason = "Claude did not confirm the long — waiting for the next setup"
        };

        var verdict = AutoEntryPolicy.EvaluateLlm(declined, TradeSide.Long, confidenceThreshold: 55m);

        Assert.False(verdict.Allowed);
        Assert.Contains("did not confirm", verdict.Reason);
    }

    [Fact]
    public void ConfirmationLetsTheEntryThrough()
    {
        var verdict = AutoEntryPolicy.EvaluateLlm(Proposal(), TradeSide.Long, confidenceThreshold: 55m);

        Assert.True(verdict.Allowed);
        Assert.Contains("confirmed", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // Claude rules only on the side the engine proposed, so a side change can only come from
    // the engine itself and still means an unstable read.
    [Fact]
    public void SideChangeIsStillTreatedAsInstability()
    {
        var verdict = AutoEntryPolicy.EvaluateLlm(Proposal(), TradeSide.Short, confidenceThreshold: 55m);

        Assert.False(verdict.Allowed);
        Assert.Contains("signal changed", verdict.Reason);
    }

    // Advisory mode: Claude has no authority to stop anything, so an outage correctly leaves
    // the rule-based signal standing.
    [Fact]
    public void UnavailableClaudeDoesNotBlockTheTradeInAdvisoryMode()
    {
        var verdict = AutoEntryPolicy.EvaluateLlm(
            Proposal(used: false, confirmed: true), TradeSide.Long, confidenceThreshold: 55m);

        Assert.True(verdict.Allowed);
        Assert.Contains("unavailable", verdict.Reason);
    }

    // The regression that cost real money. On 2026-08-07 Claude declined 34 setups in a row;
    // the 35th reply failed to parse, fail-open let that one alone through, and it closed at
    // the stop for -0.41. The only position opened was the only one never actually audited.
    // A verdict that could not be read is not an approval.
    [Fact]
    public void UnreadableClaudeBlocksTheTradeInAuditorMode()
    {
        var verdict = AutoEntryPolicy.EvaluateLlm(
            Proposal(used: false, confirmed: true), TradeSide.Long,
            confidenceThreshold: 55m, auditorMode: true);

        Assert.False(verdict.Allowed);
        Assert.Contains("explicit confirmation", verdict.Reason);
    }

    // Confirmed=true on an unused validation is the shape a passthrough takes, so the guard
    // must key on Used and not be fooled by the default flag.
    [Fact]
    public void AuditorModeIgnoresTheConfirmedFlagWhenTheReplyWasNotUsed()
    {
        var passthroughShaped = Proposal(used: false, confirmed: true, aligned: null, blocking: null);

        Assert.False(AutoEntryPolicy
            .EvaluateLlm(passthroughShaped, TradeSide.Long, 55m, auditorMode: true).Allowed);
    }

    // Advisory mode is unchanged: a hesitant verdict still trades when conviction is high
    // enough, because there Claude has no authority to stop it.
    [Fact]
    public void AdvisoryModeStillTradesOnAHesitantVerdictWithHighConfidence()
    {
        var hesitant = Proposal(confirmed: false, confidenceBuy: 80m);

        var verdict = AutoEntryPolicy.EvaluateLlm(hesitant, TradeSide.Long, confidenceThreshold: 55m);

        Assert.True(verdict.Allowed);
        Assert.Contains("hesitant", verdict.Reason);
    }
}
