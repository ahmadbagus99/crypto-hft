using System.Text.Json;
using System.Xml.Linq;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Trading;
using Microsoft.Extensions.Logging;

namespace CryptoHft.Infrastructure.Ai;

// Sentiment: CoinDesk/Cointelegraph RSS headlines + alternative.me Fear & Greed Index.
// If a LunarCrush API key is configured, its social sentiment is blended into the
// SocialScore. Cached for 5 minutes to avoid hammering the sources on every tick.
public sealed class FreeSentimentProvider(
    IHttpClientFactory httpClientFactory,
    IRuntimeTradingSettingsService settingsService,
    ILogger<FreeSentimentProvider> logger)
    : ISentimentProvider
{
    private static readonly string[] RssFeeds =
    [
        "https://www.coindesk.com/arc/outboundfeeds/rss/",
        "https://cointelegraph.com/rss"
    ];

    private static readonly string[] Bullish =
    [
        "surge", "rally", "soar", "jump", "gain", "bullish", "breakout", "inflow", "adoption",
        "approval", "etf", "record", "high", "buy", "accumulate", "support", "upgrade"
    ];

    private static readonly string[] Bearish =
    [
        "crash", "plunge", "drop", "fall", "bearish", "selloff", "outflow", "hack", "ban",
        "lawsuit", "liquidation", "fear", "dump", "decline", "warning", "risk", "down"
    ];

    private SentimentSnapshot? _cache;
    private DateTimeOffset _cacheTime = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<SentimentSnapshot> GetSentimentAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null && DateTimeOffset.UtcNow - _cacheTime < TimeSpan.FromMinutes(5))
            return _cache;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cache is not null && DateTimeOffset.UtcNow - _cacheTime < TimeSpan.FromMinutes(5))
                return _cache;

            var headlines = await FetchHeadlinesAsync(cancellationToken);
            var (newsScore, label) = ScoreHeadlines(headlines);
            var (fgIndex, fgLabel) = await FetchFearGreedAsync(cancellationToken);

            // SocialScore is Fear & Greed by default; blend in LunarCrush social
            // sentiment (50/50) when an API key is configured.
            decimal socialScore = fgIndex;
            var lunar = await FetchLunarCrushAsync(cancellationToken);
            if (lunar is not null)
                socialScore = Math.Round((fgIndex + lunar.Value) / 2m, 1);

            var snapshot = new SentimentSnapshot(
                NewsScore: newsScore,
                SocialScore: socialScore,
                SentimentLabel: label,
                FearGreedIndex: fgIndex,
                FearGreedLabel: fgLabel,
                Headlines: headlines.Take(8).ToList());

            _cache = snapshot;
            _cacheTime = DateTimeOffset.UtcNow;
            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sentiment fetch failed, returning neutral");
            return new SentimentSnapshot(50, 50, "Neutral", 50, "Neutral", Array.Empty<string>());
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<string>> FetchHeadlinesAsync(CancellationToken cancellationToken)
    {
        var headlines = new List<string>();
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CryptoHFT/1.0)");

        foreach (var feed in RssFeeds)
        {
            try
            {
                var xml = await client.GetStringAsync(feed, cancellationToken);
                var doc = XDocument.Parse(xml);
                var titles = doc.Descendants("item")
                    .Select(i => i.Element("title")?.Value)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Take(15)
                    .Select(t => t!.Trim());
                headlines.AddRange(titles);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "RSS feed {Feed} unavailable", feed);
            }
        }
        return headlines;
    }

    private static (decimal score, string label) ScoreHeadlines(IReadOnlyList<string> headlines)
    {
        if (headlines.Count == 0) return (50m, "Neutral");
        int bull = 0, bear = 0;
        foreach (var h in headlines)
        {
            var lower = h.ToLowerInvariant();
            if (!lower.Contains("btc") && !lower.Contains("bitcoin") && !lower.Contains("crypto") && !lower.Contains("market"))
                continue;
            if (Bullish.Any(lower.Contains)) bull++;
            if (Bearish.Any(lower.Contains)) bear++;
        }
        var total = bull + bear;
        if (total == 0) return (50m, "Neutral");
        var score = 50m + (bull - bear) / (decimal)total * 50m;
        score = Math.Clamp(score, 0m, 100m);
        var label = score >= 60 ? "Bullish" : score <= 40 ? "Bearish" : "Neutral";
        return (score, label);
    }

    // LunarCrush v4 social sentiment for BTC (0-100, % bullish). Returns null if no
    // key is set or the request fails — caller falls back to Fear & Greed only.
    private async Task<decimal?> FetchLunarCrushAsync(CancellationToken cancellationToken)
    {
        var key = settingsService.GetRuntimeSettings().LunarCrushApiKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

            var json = await client.GetStringAsync(
                "https://lunarcrush.com/api4/public/coins/BTC/v1", cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            // Prefer `sentiment` (% bullish 0-100); fall back to `galaxy_score` (0-100).
            if (data.TryGetProperty("sentiment", out var s) && s.ValueKind == JsonValueKind.Number)
                return Math.Clamp(s.GetDecimal(), 0m, 100m);
            if (data.TryGetProperty("galaxy_score", out var g) && g.ValueKind == JsonValueKind.Number)
                return Math.Clamp(g.GetDecimal(), 0m, 100m);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "LunarCrush fetch failed");
            return null;
        }
    }

    private async Task<(int index, string label)> FetchFearGreedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var json = await client.GetStringAsync("https://api.alternative.me/fng/?limit=1", cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var item = doc.RootElement.GetProperty("data")[0];
            var value = int.Parse(item.GetProperty("value").GetString() ?? "50");
            var label = item.GetProperty("value_classification").GetString() ?? "Neutral";
            return (value, label);
        }
        catch
        {
            return (50, "Neutral");
        }
    }
}
