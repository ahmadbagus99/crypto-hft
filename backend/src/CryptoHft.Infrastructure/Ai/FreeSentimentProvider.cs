using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Trading;
using Microsoft.Extensions.Logging;

namespace CryptoHft.Infrastructure.Ai;

// Sentiment: CoinDesk/Cointelegraph RSS headlines + alternative.me Fear & Greed Index.
// Headlines are filtered to BTC/crypto-market relevance, scored with word-boundary
// keyword/event patterns (high-impact events weigh 2-3x a generic keyword), weighted by
// recency (12h half-life), and dampened when only one headline drives the signal so a
// single clickbait title cannot swing the score. Output includes a 0-100 confidence
// (volume + agreement of the evidence) and the top reasons behind the score.
// If a LunarCrush API key is configured, its social sentiment is blended into the
// SocialScore. Cached for 5 minutes (1 minute after a failure) to avoid hammering sources.
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

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromMinutes(1);

    // Only headlines about BTC / the crypto market / macro drivers count toward the score.
    internal static readonly Regex Relevance = new(
        @"\b(btc|bitcoin|crypto\w*|blockchain|ethereum|stablecoin\w*|defi|binance|coinbase|altcoin\w*|digital assets?|spot etf|fomc|fed|rate (cut|hike)s?|inflation|cpi|sec|treasury)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Clickbait/speculation guard: question headlines and "could/might" predictions carry half weight.
    private static readonly Regex Speculative = new(
        @"\?|^why |\b(could|might|may|would|prediction|predicts?|forecasts?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record EventPattern(Regex Regex, decimal Weight, string Reason);

    private static EventPattern P(string pattern, decimal weight, string reason)
        => new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), weight, reason);

    // High-impact events (|weight| >= 2) dominate generic keywords (|weight| = 1).
    // Phrase-level patterns avoid the old substring traps ("etf" counting outflow
    // headlines as bullish, "high" matching "highlights", "down" matching "download").
    private static readonly EventPattern[] EventPatterns =
    [
        // High-impact bearish
        P(@"\bhack(ed|er|ers|s)?\b|\bexploit(ed|s)?\b|\bbreach(ed)?\b|\bstolen\b|\bdrain(ed|s)\b", -3m, "security incident (hack/exploit)"),
        P(@"\bbankrupt\w*\b|\binsolven\w*\b|\bcollapse[sd]?\b|halt(s|ed)? withdrawal", -3m, "exchange/firm collapse or distress"),
        P(@"etf[^.]{0,30}outflow|outflow[^.]{0,30}etf", -3m, "ETF outflows"),
        P(@"\bsec\b[^.]{0,40}\b(sue[sd]?|lawsuit|charge[sd]?|subpoena|crackdown)|\bban(s|ned)?\b[^.]{0,25}crypto|crypto[^.]{0,25}\bban(s|ned)?\b|\bregulat\w+[^.]{0,25}crackdown", -3m, "regulatory action"),
        P(@"liquidation[s]? (cascade|wave)|mass liquidations?|\blongs? (were )?liquidated|billion[^.]{0,20}liquidat", -3m, "liquidation cascade"),
        P(@"rate hikes?|hawkish|hotter[- ]than[- ]expected|inflation (surge|spike|jump)\w*", -2m, "macro tightening shock"),
        // High-impact bullish
        P(@"etf[^.]{0,30}inflow|inflow[^.]{0,30}etf|record inflows?", 3m, "ETF inflows"),
        P(@"\bapprov(es?|al|ed)\b[^.]{0,30}(etf|bitcoin|crypto)|(etf|bitcoin|crypto)[^.]{0,30}\bapprov(es?|al|ed)\b", 3m, "regulatory approval"),
        P(@"institutional[^.]{0,25}(buy|adopt|accumulat|demand)|treasur\w+[^.]{0,25}(buys?|adds?|holds?) bitcoin|strategic (bitcoin )?reserve", 3m, "institutional buying"),
        P(@"rate cuts?|dovish|quantitative easing|\bqe\b|liquidity injection", 2m, "monetary easing"),
        P(@"\badopt(s|ion|ed)?\b|legal tender|accepts? (bitcoin|btc|crypto)|integrat\w+ (bitcoin|btc|crypto)", 2m, "adoption"),
        // Generic price-action keywords
        P(@"\b(surge[sd]?|rall(y|ies|ied)|soar(s|ed)?|jump(s|ed)?|breakout|all[- ]time high|accumulat\w+|bullish)\b", 1m, "bullish price action"),
        P(@"\b(crash(es|ed)?|plunge[sd]?|tumble[sd]?|slump[sd]?|sink(s|ing)?|slide[sd]?|sell[- ]?off|dump(s|ed)?|bearish)\b", -1m, "bearish price action"),
    ];

    internal sealed record Headline(string Title, DateTimeOffset? Published);

    private SentimentSnapshot? _cache;
    private DateTimeOffset _cacheTime = DateTimeOffset.MinValue;
    private TimeSpan _cacheFor = CacheDuration;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<SentimentSnapshot> GetSentimentAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null && DateTimeOffset.UtcNow - _cacheTime < _cacheFor)
            return _cache;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cache is not null && DateTimeOffset.UtcNow - _cacheTime < _cacheFor)
                return _cache;

            var headlines = await FetchHeadlinesAsync(cancellationToken);
            var relevant = headlines.Where(h => Relevance.IsMatch(h.Title)).ToList();
            var (newsScore, label, confidence, reasons) = ScoreHeadlines(relevant);
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
                Headlines: relevant.Take(8).Select(h => h.Title).ToList(),
                NewsConfidence: confidence,
                NewsReasons: reasons);

            _cache = snapshot;
            _cacheTime = DateTimeOffset.UtcNow;
            _cacheFor = CacheDuration;
            return snapshot;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sentiment fetch failed, returning neutral");
            // Neutral fallback with zero confidence — never a fabricated signal. Cache it
            // briefly so a dead source doesn't get hammered on every tick.
            var neutral = new SentimentSnapshot(
                50, 50, "Neutral", 50, "Neutral", Array.Empty<string>(),
                NewsConfidence: 0m,
                NewsReasons: ["sentiment sources unavailable — neutral fallback"]);
            _cache = neutral;
            _cacheTime = DateTimeOffset.UtcNow;
            _cacheFor = FailureCacheDuration;
            return neutral;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<Headline>> FetchHeadlinesAsync(CancellationToken cancellationToken)
    {
        var headlines = new List<Headline>();
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CryptoHFT/1.0)");

        foreach (var feed in RssFeeds)
        {
            try
            {
                var xml = await client.GetStringAsync(feed, cancellationToken);
                var doc = XDocument.Parse(xml);
                var items = doc.Descendants("item")
                    .Select(i => new Headline(
                        i.Element("title")?.Value.Trim() ?? "",
                        DateTimeOffset.TryParse(i.Element("pubDate")?.Value, out var dt) ? dt : null))
                    .Where(h => h.Title.Length > 0)
                    .Take(20);
                headlines.AddRange(items);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "RSS feed {Feed} unavailable", feed);
            }
        }
        return headlines;
    }

    // Aggregates per-headline scores into (score 0-100, label, confidence 0-100, reasons).
    internal static (decimal Score, string Label, decimal Confidence, List<string> Reasons) ScoreHeadlines(
        IReadOnlyList<Headline> relevant)
    {
        if (relevant.Count == 0)
            return (50m, "Neutral", 0m, ["no relevant BTC/crypto headlines — neutral"]);

        var now = DateTimeOffset.UtcNow;
        decimal signedSum = 0, absSum = 0;
        int bullCount = 0, bearCount = 0;
        // Track the strongest contributions for the reasons list.
        var contributions = new List<(decimal Impact, string Reason, string Title, double AgeHours)>();

        foreach (var h in relevant)
        {
            // Recency: exponential decay, 12h half-life. Undated headlines count half.
            var ageHours = h.Published is DateTimeOffset p ? Math.Max(0, (now - p).TotalHours) : 24;
            var recency = (decimal)Math.Max(0.05, Math.Pow(0.5, ageHours / 12.0));

            decimal headlineScore = 0;
            string? topReason = null;
            decimal topImpact = 0;
            foreach (var ep in EventPatterns)
            {
                if (!ep.Regex.IsMatch(h.Title)) continue;
                headlineScore += ep.Weight;
                if (Math.Abs(ep.Weight) > Math.Abs(topImpact))
                {
                    topImpact = ep.Weight;
                    topReason = ep.Reason;
                }
            }
            if (headlineScore == 0) continue;

            // Cap per headline so keyword pile-ups can't dominate; halve speculative titles.
            headlineScore = Math.Clamp(headlineScore, -3m, 3m);
            if (Speculative.IsMatch(h.Title)) headlineScore *= 0.5m;

            var weighted = headlineScore * recency;
            signedSum += weighted;
            absSum += Math.Abs(weighted);
            if (weighted > 0) bullCount++; else bearCount++;
            contributions.Add((weighted, topReason!, h.Title, ageHours));
        }

        var signedCount = bullCount + bearCount;
        if (signedCount == 0 || absSum == 0)
            return (50m, "Neutral", 10m, [$"{relevant.Count} relevant headlines but no scored signal — neutral"]);

        // Normalized direction in [-1, 1], then dampened when evidence is thin so a single
        // headline can move the score at most halfway to the extreme.
        var normalized = signedSum / absSum;
        var evidenceFactor = signedCount switch { 1 => 0.5m, 2 => 0.75m, _ => 1m };
        var score = Math.Clamp(Math.Round(50m + normalized * 50m * evidenceFactor, 1), 0m, 100m);
        var label = score >= 60 ? "Bullish" : score <= 40 ? "Bearish" : "Neutral";

        // Confidence: evidence volume (up to 60) + directional agreement (up to 40).
        var volume = Math.Min(signedCount, 6) / 6m * 60m;
        var agreement = Math.Abs(normalized) * 40m;
        var confidence = Math.Round(volume + agreement, 0);

        var reasons = new List<string>
        {
            $"{relevant.Count} relevant headlines: {bullCount} bullish, {bearCount} bearish"
        };
        reasons.AddRange(contributions
            .OrderByDescending(c => Math.Abs(c.Impact))
            .Take(4)
            .Select(c => $"{c.Reason} ({(c.Impact > 0 ? "+" : "")}{c.Impact:F1}, {c.AgeHours:F0}h ago): {Truncate(c.Title, 90)}"));

        return (score, label, confidence, reasons);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

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
