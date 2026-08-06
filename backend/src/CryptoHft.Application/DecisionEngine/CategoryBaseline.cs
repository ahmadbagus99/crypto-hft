namespace CryptoHft.Application.DecisionEngine;

// Category scores are blended against a fixed neutral of 50, which assumes every input
// actually sits near 50 when it has nothing to say. Measured over 254 independent hours
// (2026-08-06) three of them do not:
//
//   sentiment    median 27, stdev 5.4   -> -0.95 points of D, every hour, at 4% weight
//   derivatives  median 45, stdev 5.0   -> -0.61 points
//   onchain      median 44, stdev 9.8   -> -0.56 points
//
// Together they pushed D down ~2.1 points permanently while BTC rose 9.8% over the same
// window. sentiment is the clearest case: it is the raw Fear & Greed index, which never
// left 11-35 across the whole sample, so "23 points below neutral" was a constant, not a
// reading. A constant cannot inform a direction — it can only tilt one.
//
// The correction shifts each category onto its own observed centre so that a typical
// reading contributes nothing and only a DEVIATION from its own habit votes. It
// deliberately does not rescale: dividing by the spread would turn a 5-point wobble in a
// near-constant input into a full-sized directional vote, which is the opposite of what
// the evidence supports.
public static class CategoryBaseline
{
    public const decimal Neutral = 50m;

    // Below this the input is centred well enough that correcting it would be noise-fitting.
    public const decimal MinOffset = 3m;

    // Bounds the shift so a provider stuck at 0 or 100 cannot silently rewrite the whole
    // score. Set above the largest offset actually measured (sentiment, -23) so the real
    // cases are corrected in full and the cap only catches genuine breakage.
    public const decimal MaxCorrection = 25m;

    // Fewer observations than this and the median is not yet a description of the input.
    public const int MinSamples = 200;

    // Shifts a raw category score onto its own centre. Returns it untouched when the
    // baseline is absent, immaterial, or the input is already near neutral.
    public static decimal Recenter(decimal score, decimal? baseline)
    {
        if (baseline is not decimal b) return score;

        var offset = b - Neutral;
        if (Math.Abs(offset) < MinOffset) return score;

        var correction = Math.Clamp(offset, -MaxCorrection, MaxCorrection);
        return Math.Clamp(score - correction, 0m, 100m);
    }

    // The centre of an input's own distribution. Median rather than mean so a handful of
    // provider spikes cannot drag the correction with them.
    public static decimal? ComputeBaseline(IReadOnlyList<decimal> history)
    {
        if (history.Count < MinSamples) return null;

        var sorted = history.OrderBy(x => x).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2m;
    }
}
