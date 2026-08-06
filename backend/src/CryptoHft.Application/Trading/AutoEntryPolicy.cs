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

// Style-dependent entry timings. Intraday keeps the batch-18 values; scalper compresses
// everything: a 30-second persistence check (one extra scan tick), a short candidate
// life, and 10/20-minute cooldowns — a scalper that sits out a full hour after one
// stop-out isn't scalping, but the cooldown itself stays because instant re-entry after
// a stop was the single worst-performing pattern in Position History for BOTH styles.
public sealed record AutoEntryTiming(
    TimeSpan ConfirmationDelay,
    TimeSpan CandidateLifetime,
    TimeSpan StandardCooldown,
    TimeSpan StopLossCooldown,
    decimal StrongSignalOffset)
{
    // Intraday spacing is raised for the same reason as scalper below: fees are paid per
    // round trip regardless of how the trade goes, so with a break-even direction call the
    // only way to lose less is to trade less. 90/180 minutes caps the burn at roughly a
    // third of what 30/60 permitted.
    public static readonly AutoEntryTiming Intraday = new(
        ConfirmationDelay: TimeSpan.FromMinutes(2),
        CandidateLifetime: TimeSpan.FromMinutes(15),
        StandardCooldown: TimeSpan.FromMinutes(90),
        StopLossCooldown: TimeSpan.FromMinutes(180),
        StrongSignalOffset: 4m);

    // Scalper cooldowns were 10/20 minutes, which allowed roughly four round trips a day.
    // Measured over 24 trades: gross PnL before fees was -0.11 USDT — the direction calls
    // net to zero — while fees came to -4.97. The account lost 5.07 USDT and essentially
    // all of it was transaction cost. With no demonstrated directional edge the expected
    // value of a trade is exactly minus the fee, so trade COUNT is the loss, and spacing
    // entries out is the only lever that reliably moves PnL toward zero. Raised to 45/90
    // minutes: about a quarter of the previous frequency, and therefore about a quarter of
    // the burn, until the engine can show an edge that outruns the fee.
    public static readonly AutoEntryTiming Scalper = new(
        ConfirmationDelay: TimeSpan.FromSeconds(30),
        CandidateLifetime: TimeSpan.FromMinutes(5),
        StandardCooldown: TimeSpan.FromMinutes(45),
        StopLossCooldown: TimeSpan.FromMinutes(90),
        StrongSignalOffset: 4m);

    public static AutoEntryTiming For(TradingStyle style)
        => style == TradingStyle.Scalper ? Scalper : Intraday;
}

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
    public const decimal HesitantSignalOffset = 0m;

    public static AutoEntryConfirmationVerdict EvaluateConfirmation(
        AdvancedDecision decision,
        decimal confidenceThreshold,
        AutoEntryCandidate? candidate,
        DateTimeOffset now,
        AutoEntryTiming? timing = null)
    {
        var t = timing ?? AutoEntryTiming.Intraday;
        if (candidate is not null && now - candidate.FirstSeenAt > t.CandidateLifetime)
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
                    $"{candidate.FirstSeenAt.Add(t.CandidateLifetime):u}");
        }

        if (decision.Confidence >= confidenceThreshold + t.StrongSignalOffset)
        {
            return new(
                AutoEntryConfirmationAction.Ready,
                null,
                side,
                $"strong {side} signal {decision.Confidence:F1} >= {confidenceThreshold + t.StrongSignalOffset:F1}");
        }

        if (candidate is null || candidate.Side != side)
        {
            var next = new AutoEntryCandidate(side, now);
            return new(
                AutoEntryConfirmationAction.Wait,
                next,
                side,
                $"first {side} confirmation {decision.Confidence:F1}; confirm again after " +
                $"{now.Add(t.ConfirmationDelay):u}");
        }

        var elapsed = now - candidate.FirstSeenAt;
        if (elapsed < t.ConfirmationDelay)
        {
            return new(
                AutoEntryConfirmationAction.Wait,
                candidate,
                side,
                $"{side} confirmation pending for {(t.ConfirmationDelay - elapsed).TotalMinutes:F1} more minutes");
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
        // Auditor mode declines by turning the decision into a NoTrade, so a refusal arrives
        // here as an unactionable signal carrying Claude's own reason.
        if (!TryGetActionableSide(decision, confidenceThreshold, out var finalSide))
            return new(false, decision.NoTradeReason.Length > 0 ? decision.NoTradeReason : "final signal is not actionable");

        // A side that changes between confirmation and validation means the read is unstable.
        // Claude only ever rules on the side the engine proposed, so it cannot cause this.
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

    public static TimeSpan CooldownFor(PositionCloseReason closeReason, AutoEntryTiming? timing = null)
    {
        var t = timing ?? AutoEntryTiming.Intraday;
        return closeReason is PositionCloseReason.StopLoss or PositionCloseReason.Unknown
            ? t.StopLossCooldown
            : t.StandardCooldown;
    }

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
