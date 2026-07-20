namespace CryptoHft.Application.DecisionEngine;

// Pure rules for the factor-learning layer (kept out of the DB service so they are unit
// testable). Factors are judged on THEIR OWN directional call versus the realized outcome —
// not on whether they happened to agree with the trade — so a consistently wrong-way factor
// accumulates evidence instead of silently never being scored.
public static class AdaptiveLearningPolicy
{
    // A factor "votes" bullish at >= 55 and bearish at <= 45; the middle band abstains.
    public const decimal BullVoteThreshold = 55m;
    public const decimal BearVoteThreshold = 45m;

    // Inversion: with enough samples, a factor whose own directional accuracy is under 40%
    // is anti-predictive — the engine folds its score around 50. Weight scaling alone can
    // only mute such a factor (0.5x clamp), never repair it.
    public const decimal InversionAccuracyThreshold = 0.40m;
    public const decimal MinInversionSamples = 15m;

    // Recency decay: the excess evidence above the Beta(1,1) prior halves every 30 days,
    // so the learner tracks the current market instead of being anchored to a stale one.
    public const decimal DecayHalfLifeDays = 30m;

    // Price-fallback evaluations inside this dead band teach nothing: a flat hour is noise,
    // not evidence about direction. Realized trades always teach.
    public const decimal FallbackTeachDeadBandPercent = 0.15m;

    // A rule-only decision can be logged every auto-trading tick. Those snapshots are highly
    // correlated, so unmatched price-fallback evidence is sampled at most once per direction,
    // regime, symbol, and one-hour horizon. It stays useful as weak market feedback without
    // overpowering the independent realized outcomes in Position History.
    public const decimal FallbackEvidenceWeight = 0.25m;
    public const int FallbackIndependenceMinutes = 60;

    // Four half-lives retain 93.75% of the mathematically relevant evidence while bounding the
    // deterministic rebuild query as the decision log grows over time.
    public const int EvidenceRetentionDays = 120;

    // True when the factor took a side; factorWin then says whether that side matched the
    // realized direction of the market.
    public static bool TryScoreVote(decimal score, bool outcomeUp, out bool factorWin)
    {
        if (score >= BullVoteThreshold) { factorWin = outcomeUp; return true; }
        if (score <= BearVoteThreshold) { factorWin = !outcomeUp; return true; }
        factorWin = false;
        return false;
    }

    public static (decimal Alpha, decimal Beta) Decay(decimal alpha, decimal beta, decimal elapsedDays)
    {
        if (elapsedDays <= 0) return (alpha, beta);
        var factor = (decimal)Math.Pow(0.5, (double)(elapsedDays / DecayHalfLifeDays));
        return (1m + (alpha - 1m) * factor, 1m + (beta - 1m) * factor);
    }

    public static bool ShouldInvert(decimal posteriorMean, decimal samples)
        => samples >= MinInversionSamples && posteriorMean < InversionAccuracyThreshold;

    public static decimal RecencyMultiplier(DateTimeOffset occurredAt, DateTimeOffset now)
    {
        var elapsedDays = Math.Max(0m, (decimal)(now - occurredAt).TotalDays);
        return (decimal)Math.Pow(0.5, (double)(elapsedDays / DecayHalfLifeDays));
    }
}
