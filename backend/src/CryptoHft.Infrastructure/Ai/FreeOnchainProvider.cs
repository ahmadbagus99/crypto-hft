using System.Text.Json;
using CryptoHft.Application.DecisionEngine;
using Microsoft.Extensions.Logging;

namespace CryptoHft.Infrastructure.Ai;

// Key-free on-chain network health for BTC, sourced from mempool.space (no API key).
// mempool.space exposes network/mempool data rather than valuation metrics (no MVRV/SOPR),
// so this is a network-demand / miner-confidence proxy, not a valuation oracle:
//   - Hashrate trend (1-month): rising hashrate = miner confidence & security up = bullish.
//   - Difficulty adjustment estimate: positive = network growing = bullish.
//   - Fee demand (sat/vB): higher = more on-chain demand = mildly bullish.
// Folded into a single 0-100 score (> 50 = bullish). Cached 20 min — these move slowly.
public sealed class FreeOnchainProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<FreeOnchainProvider> logger) : IOnchainDataProvider
{
    private const string Base = "https://mempool.space/api";

    private OnchainSnapshot? _cache;
    private DateTimeOffset _cacheTime = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<OnchainSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null && DateTimeOffset.UtcNow - _cacheTime < TimeSpan.FromMinutes(20))
            return _cache;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cache is not null && DateTimeOffset.UtcNow - _cacheTime < TimeSpan.FromMinutes(20))
                return _cache;

            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CryptoHFT/1.0)");

            var hashrateChange = await HashrateChangePercentAsync(client, cancellationToken);
            var difficultyChange = await DifficultyChangePercentAsync(client, cancellationToken);
            var fastestFee = await FastestFeeAsync(client, cancellationToken);

            var snapshot = Score(hashrateChange, difficultyChange, fastestFee);
            _cache = snapshot;
            _cacheTime = DateTimeOffset.UtcNow;
            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "On-chain fetch failed, returning neutral/unavailable");
            return new OnchainSnapshot(50m, "On-chain data unavailable", false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private OnchainSnapshot Score(decimal? hashrateChange, decimal? difficultyChange, int? fastestFee)
    {
        var available = hashrateChange.HasValue || difficultyChange.HasValue;
        if (!available)
            return new OnchainSnapshot(50m, "On-chain data unavailable", false);

        var score = 50m;
        // Hashrate trend: a 10% monthly rise nudges ~+15.
        if (hashrateChange.HasValue)
            score += Math.Clamp(hashrateChange.Value * 1.5m, -25m, 25m);
        // Difficulty retarget estimate: positive = growing network.
        if (difficultyChange.HasValue)
            score += Math.Clamp(difficultyChange.Value * 1.5m, -15m, 15m);
        // Fee demand: light nudge for congested vs idle mempool.
        if (fastestFee.HasValue)
            score += fastestFee.Value >= 30 ? 5m : fastestFee.Value <= 3 ? -5m : 0m;
        score = Math.Clamp(score, 0m, 100m);

        var parts = new List<string>();
        if (hashrateChange.HasValue) parts.Add($"hashrate {hashrateChange.Value:+0.0;-0.0}%");
        if (difficultyChange.HasValue) parts.Add($"diff {difficultyChange.Value:+0.0;-0.0}%");
        if (fastestFee.HasValue) parts.Add($"fee {fastestFee.Value} sat/vB");
        var bias = score >= 60 ? "bullish" : score <= 40 ? "bearish" : "neutral";
        var summary = $"{bias} ({string.Join(", ", parts)})";

        return new OnchainSnapshot(Math.Round(score, 1), summary, true);
    }

    // % change between the first and last ~7-day windows of the 1-month hashrate series.
    private async Task<decimal?> HashrateChangePercentAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var json = await client.GetStringAsync($"{Base}/v1/mining/hashrate/1m", cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var rates = doc.RootElement.GetProperty("hashrates")
                .EnumerateArray()
                .Select(e => e.GetProperty("avgHashrate").GetDouble())
                .Where(h => h > 0)
                .ToList();

            if (rates.Count < 4) return null;
            var window = Math.Min(7, rates.Count / 2);
            var older = rates.Take(window).Average();
            var recent = rates.Skip(rates.Count - window).Average();
            if (older == 0) return null;
            return (decimal)((recent - older) / older * 100.0);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Hashrate fetch failed");
            return null;
        }
    }

    private async Task<decimal?> DifficultyChangePercentAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var json = await client.GetStringAsync($"{Base}/v1/difficulty-adjustment", cancellationToken);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("difficultyChange", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetDecimal()
                : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Difficulty fetch failed");
            return null;
        }
    }

    private async Task<int?> FastestFeeAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var json = await client.GetStringAsync($"{Base}/v1/fees/recommended", cancellationToken);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("fastestFee", out var f) && f.ValueKind == JsonValueKind.Number
                ? f.GetInt32()
                : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Fee fetch failed");
            return null;
        }
    }
}
