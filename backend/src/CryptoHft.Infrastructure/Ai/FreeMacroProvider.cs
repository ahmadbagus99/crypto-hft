using System.Text.Json;
using CryptoHft.Application.DecisionEngine;
using Microsoft.Extensions.Logging;

namespace CryptoHft.Infrastructure.Ai;

// Key-free macro context for BTC, sourced from the Yahoo Finance chart endpoint
// (no API key required). We pull daily closes for the S&P 500, NASDAQ, the US Dollar
// Index (DXY) and gold, measure ~5-day momentum, and fold them into a single 0-100
// risk-on score (> 50 = bullish for BTC). Cached 15 min — macro data moves slowly and
// the decision loop ticks every 30s.
public sealed class FreeMacroProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<FreeMacroProvider> logger) : IMacroDataProvider
{
    // Yahoo symbols. ^ and = are URL-encoded when the request is built.
    private const string Sp500 = "^GSPC";
    private const string Nasdaq = "^IXIC";
    private const string Dxy = "DX-Y.NYB";
    private const string Gold = "GC=F";

    private MacroSnapshot? _cache;
    private DateTimeOffset _cacheTime = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<MacroSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null && DateTimeOffset.UtcNow - _cacheTime < TimeSpan.FromMinutes(15))
            return _cache;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cache is not null && DateTimeOffset.UtcNow - _cacheTime < TimeSpan.FromMinutes(15))
                return _cache;

            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CryptoHFT/1.0)");

            var sp500 = await ChangePercentAsync(client, Sp500, cancellationToken);
            var nasdaq = await ChangePercentAsync(client, Nasdaq, cancellationToken);
            var dxy = await ChangePercentAsync(client, Dxy, cancellationToken);
            var gold = await ChangePercentAsync(client, Gold, cancellationToken);

            var snapshot = Score(sp500, nasdaq, dxy, gold);
            _cache = snapshot;
            _cacheTime = DateTimeOffset.UtcNow;
            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Macro fetch failed, returning neutral/unavailable");
            return new MacroSnapshot(50m, "Macro data unavailable", false);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Risk-on score: rising equities are bullish for BTC, a rising dollar is bearish,
    // and rising gold is a mild debasement-hedge tailwind. Scaling constants are
    // heuristic — a ~2% equity move shifts the score by ~16 points.
    private MacroSnapshot Score(decimal? sp500, decimal? nasdaq, decimal? dxy, decimal? gold)
    {
        var available = sp500.HasValue || nasdaq.HasValue || dxy.HasValue;
        if (!available)
            return new MacroSnapshot(50m, "Macro data unavailable", false);

        var equity = Average(sp500, nasdaq);
        var score = 50m;
        if (equity.HasValue) score += equity.Value * 8m;
        if (dxy.HasValue) score -= dxy.Value * 10m;
        if (gold.HasValue) score += gold.Value * 2m;
        score = Math.Clamp(score, 0m, 100m);

        var parts = new List<string>();
        if (sp500.HasValue) parts.Add($"SPX {sp500.Value:+0.0;-0.0}%");
        if (nasdaq.HasValue) parts.Add($"NDX {nasdaq.Value:+0.0;-0.0}%");
        if (dxy.HasValue) parts.Add($"DXY {dxy.Value:+0.0;-0.0}%");
        if (gold.HasValue) parts.Add($"Gold {gold.Value:+0.0;-0.0}%");
        var bias = score >= 60 ? "risk-on" : score <= 40 ? "risk-off" : "neutral";
        var summary = $"{bias} (5d: {string.Join(", ", parts)})";

        return new MacroSnapshot(Math.Round(score, 1), summary, true);
    }

    private static decimal? Average(decimal? a, decimal? b)
    {
        if (a.HasValue && b.HasValue) return (a.Value + b.Value) / 2m;
        return a ?? b;
    }

    // ~5-trading-day percentage change of daily closes. Returns null if the symbol
    // could not be fetched/parsed (caller treats it as missing, not zero).
    private async Task<decimal?> ChangePercentAsync(HttpClient client, string symbol, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}?range=1mo&interval=1d";
            var json = await client.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var closeArray = doc.RootElement
                .GetProperty("chart").GetProperty("result")[0]
                .GetProperty("indicators").GetProperty("quote")[0]
                .GetProperty("close");

            var closes = closeArray.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Number)
                .Select(e => e.GetDecimal())
                .ToList();

            if (closes.Count < 2) return null;
            var last = closes[^1];
            var prior = closes[Math.Max(0, closes.Count - 6)]; // ~5 sessions back
            if (prior == 0) return null;
            return (last - prior) / prior * 100m;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Macro symbol {Symbol} unavailable", symbol);
            return null;
        }
    }
}
