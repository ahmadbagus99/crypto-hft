namespace CryptoHft.Application.Ai;

public sealed record AiUsageSummary(
    int CallsToday,
    int CallsTotal,
    decimal CostTodayUsd,
    decimal CostTotalUsd,
    int InputTokensTotal,
    int OutputTokensTotal,
    string? LastModel,
    DateTimeOffset? LastCallAt);

public interface IAiUsageTracker
{
    // Records one Claude API call (fire-and-forget; failures are swallowed).
    void Record(string model, int inputTokens, int outputTokens);

    Task<AiUsageSummary> GetSummaryAsync(CancellationToken cancellationToken);
}
