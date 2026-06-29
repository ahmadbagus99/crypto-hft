using CryptoHft.Application.Ai;
using CryptoHft.Domain.Entities;
using CryptoHft.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoHft.Infrastructure.Ai;

public sealed class AiUsageTracker(
    IServiceScopeFactory scopeFactory,
    ILogger<AiUsageTracker> logger) : IAiUsageTracker
{
    // USD per 1,000,000 tokens (input, output). Matched on a model-id prefix so dated
    // suffixes (e.g. claude-haiku-4-5-20251001) still resolve.
    private static readonly (string Prefix, decimal Input, decimal Output)[] Pricing =
    [
        ("claude-opus", 5m, 25m),
        ("claude-sonnet", 3m, 15m),
        ("claude-haiku", 1m, 5m),
        ("claude-fable", 10m, 50m),
    ];

    private static decimal ComputeCost(string model, int inputTokens, int outputTokens)
    {
        var (inRate, outRate) = Pricing
            .Where(p => model.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => (p.Input, p.Output))
            .DefaultIfEmpty((5m, 25m)) // default to Opus pricing if unknown
            .First();

        return inputTokens / 1_000_000m * inRate + outputTokens / 1_000_000m * outRate;
    }

    public void Record(string model, int inputTokens, int outputTokens)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.AiUsage.Add(new AiUsageLog
            {
                Model = model,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CostUsd = ComputeCost(model, inputTokens, outputTokens),
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to record AI usage");
        }
    }

    public async Task<AiUsageSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var startOfDay = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);

        var rows = await db.AiUsage.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

        var today = rows.Where(r => r.CreatedAt >= startOfDay).ToList();
        var last = rows.FirstOrDefault();

        return new AiUsageSummary(
            CallsToday: today.Count,
            CallsTotal: rows.Count,
            CostTodayUsd: today.Sum(r => r.CostUsd),
            CostTotalUsd: rows.Sum(r => r.CostUsd),
            InputTokensTotal: rows.Sum(r => r.InputTokens),
            OutputTokensTotal: rows.Sum(r => r.OutputTokens),
            LastModel: last?.Model,
            LastCallAt: last?.CreatedAt);
    }
}
