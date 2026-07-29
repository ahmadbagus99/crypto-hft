using CryptoHft.Application.DecisionEngine;
using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.Trading;

public enum AutoEntryConfirmationAction
{
    Ignore,
    Wait,
    Ready
}

public sealed record AutoEntryCandidate(TradeSide Side, DateTimeOffset FirstSeenAt);

public sealed record AutoEntryConfirmationVerdict(
    AutoEntryConfirmationAction Action,
    AutoEntryCandidate? Candidate,
    TradeSide? Side,
    string Reason);

public sealed record AutoEntryLlmVerdict(bool Allowed, string Reason);

// Pure entry rules. Direction remains owned by the deterministic engine; this policy only
// requires persistence before spending an LLM call. It never lets Claude reverse a signal
// or increase risk. Tuned for responsiveness (owner call, 2026-07-29): the original
// 5-minute double confirmation plus the hesitant +2 hurdle blocked nearly every entry in
// production, so confirmation is now a 2-minute persistence check, a moderately strong
// signal skips it, and a hesitant Claude verdict downsizes (via its size multiplier)
// instead of raising the entry bar. The post-close cooldown stays: re-entries minutes
// after a stop-out were the single worst-performing pattern in Position History.
public static class AutoEntryPolicy
{
    public static readonly TimeSpan ConfirmationDelay = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan CandidateLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan StandardCooldown = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan StopLossCooldown = TimeSpan.FromMinutes(60);
    public const decimal StrongSignalOffset = 4m;
    public const decimal HesitantSignalOffset = 0m;

    public static AutoEntryConfirmationVerdict EvaluateConfirmation(
        AdvancedDecision decision,
        decimal confidenceThreshold,
        AutoEntryCandidate? candidate,
        DateTimeOffset now)
    {
        if (candidate is not null && now - candidate.FirstSeenAt > CandidateLifetime)
            candidate = null;

        if (!TryGetActionableSide(decision, confidenceThreshold, out var side))
        {
            return candidate is null
                ? new(AutoEntryConfirmationAction.Ignore, null, null, decision.NoTradeReason)
                : new(
                    AutoEntryConfirmationAction.Wait,
                    candidate,
                    candidate.Side,
                    $"waiting for second {candidate.Side} confirmation; candidate expires at " +
                    $"{candidate.FirstSeenAt.Add(CandidateLifetime):u}");
        }

        if (decision.Confidence >= confidenceThreshold + StrongSignalOffset)
        {
            return new(
                AutoEntryConfirmationAction.Ready,
                null,
                side,
                $"strong {side} signal {decision.Confidence:F1} >= {confidenceThreshold + StrongSignalOffset:F1}");
        }

        if (candidate is null || candidate.Side != side)
        {
            var next = new AutoEntryCandidate(side, now);
            return new(
                AutoEntryConfirmationAction.Wait,
                next,
                side,
                $"first {side} confirmation {decision.Confidence:F1}; confirm again after " +
                $"{now.Add(ConfirmationDelay):u}");
        }

        var elapsed = now - candidate.FirstSeenAt;
        if (elapsed < ConfirmationDelay)
        {
            return new(
                AutoEntryConfirmationAction.Wait,
                candidate,
                side,
                $"{side} confirmation pending for {(ConfirmationDelay - elapsed).TotalMinutes:F1} more minutes");
        }

        return new(
            AutoEntryConfirmationAction.Ready,
            null,
            side,
            $"{side} confirmed twice over {elapsed.TotalMinutes:F1} minutes");
    }

    public static AutoEntryLlmVerdict EvaluateLlm(
        AdvancedDecision decision,
        TradeSide confirmedSide,
        decimal confidenceThreshold)
    {
        if (!TryGetActionableSide(decision, confidenceThreshold, out var finalSide))
            return new(false, decision.NoTradeReason.Length > 0 ? decision.NoTradeReason : "final signal is not actionable");

        if (finalSide != confirmedSide)
            return new(false, $"signal changed from {confirmedSide} to {finalSide} during Claude validation");

        if (!decision.Llm.Used)
            return new(true, "Claude unavailable; confirmed rule-based signal retained");

        if (decision.Llm.Confirmed)
            return new(true, "Claude confirmed the persisted rule-based signal");

        var required = confidenceThreshold + HesitantSignalOffset;
        return decision.Confidence >= required
            ? new(true, $"Claude hesitant, but confidence {decision.Confidence:F1} >= {required:F1}")
            : new(false, $"Claude hesitant: confidence {decision.Confidence:F1} below defensive threshold {required:F1}");
    }

    public static TimeSpan CooldownFor(PositionCloseReason closeReason)
        => closeReason is PositionCloseReason.StopLoss or PositionCloseReason.Unknown
            ? StopLossCooldown
            : StandardCooldown;

    private static bool TryGetActionableSide(
        AdvancedDecision decision,
        decimal confidenceThreshold,
        out TradeSide side)
    {
        side = default;
        if (!decision.ShouldTrade || decision.Confidence < confidenceThreshold)
            return false;

        if (decision.Action is DecisionAction.WeakBuy or DecisionAction.Buy or DecisionAction.StrongBuy)
        {
            side = TradeSide.Long;
            return true;
        }

        if (decision.Action is DecisionAction.WeakSell or DecisionAction.Sell or DecisionAction.StrongSell)
        {
            side = TradeSide.Short;
            return true;
        }

        return false;
    }
}
