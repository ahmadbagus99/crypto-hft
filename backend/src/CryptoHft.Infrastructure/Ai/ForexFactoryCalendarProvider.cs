using System.Globalization;
using System.Text.Json;
using CryptoHft.Application.DecisionEngine;
using Microsoft.Extensions.Logging;

namespace CryptoHft.Infrastructure.Ai;

// Live economic calendar from the public Forex Factory weekly feed (no API key).
// High-impact USD events (FOMC, CPI, NFP, ...) become caution windows for the engine.
// The feed is refreshed every few hours and cached; while the feed is healthy its
// (possibly empty) week is authoritative. Only when no successful fetch exists within
// the trust horizon does the static EconomicEventCalendar list take over, so the
// caution never silently disappears just because a fetch failed.
public sealed class ForexFactoryCalendarProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<ForexFactoryCalendarProvider> logger) : IEconomicCalendarProvider
{
    private const string FeedUrl = "https://nfs.faireconomy.media/ff_calendar_thisweek.json";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FeedTrustHorizon = TimeSpan.FromHours(24);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile IReadOnlyList<(DateTimeOffset TimeUtc, string Label)>? _events;
    private DateTimeOffset _lastSuccessfulFetch = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public async Task<string?> GetActiveEventWindowAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastAttempt >= RefreshInterval)
            await RefreshAsync(now, cancellationToken);

        var events = _events;
        if (events is not null && now - _lastSuccessfulFetch <= FeedTrustHorizon)
            return EconomicEventCalendar.ActiveLabel(events, now);

        // Feed cold or stale: fail-safe to the static FOMC/CPI list.
        return EconomicEventCalendar.GetActiveEventLabel(now);
    }

    private async Task RefreshAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken)) return; // someone else is refreshing
        try
        {
            if (now - _lastAttempt < RefreshInterval) return; // refreshed while waiting
            _lastAttempt = now;

            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var json = await client.GetStringAsync(FeedUrl, cancellationToken);

            _events = ParseHighImpactUsdEvents(json);
            _lastSuccessfulFetch = now;
            logger.LogInformation(
                "Economic calendar refreshed: {Count} high-impact USD events this week", _events!.Count);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "economic calendar feed fetch failed — static fallback stays active");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // Feed items look like:
    //   { "title":"CPI m/m", "country":"USD", "date":"2026-07-14T08:30:00-04:00",
    //     "impact":"High", "forecast":"0.2%", "previous":"0.3%" }
    // Only High-impact USD prints move BTC reliably enough to warrant a caution window.
    internal static List<(DateTimeOffset TimeUtc, string Label)> ParseHighImpactUsdEvents(string json)
    {
        var events = new List<(DateTimeOffset, string)>();
        using var doc = JsonDocument.Parse(json);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!TryGetString(item, "impact", out var impact)
                || !impact.Equals("High", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryGetString(item, "country", out var country)
                || !country.Equals("USD", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryGetString(item, "date", out var date)) continue;
            if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;

            var title = TryGetString(item, "title", out var t) ? t : "US high-impact event";
            events.Add((time.ToUniversalTime(), $"{title} (US high-impact)"));
        }
        return events;
    }

    private static bool TryGetString(JsonElement item, string property, out string value)
    {
        value = "";
        if (!item.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString() ?? "";
        return value.Length > 0;
    }
}
