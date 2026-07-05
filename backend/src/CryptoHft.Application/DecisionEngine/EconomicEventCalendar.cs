namespace CryptoHft.Application.DecisionEngine;

// Static calendar of scheduled high-impact US macro events (FOMC rate decisions, CPI
// releases). BTC routinely whipsaws through both sides of a spread in the minutes around
// these prints, so the engine surfaces an advisory caution inside the event window — the
// trade is never blocked (confidence stays the sole gate), but the AI validator sizes
// defensively and the learned SL/TP geometry handles the rest.
//
// Dates are known months in advance and hardcoded in UTC (ET converted per US DST).
// MAINTENANCE: extend this list once a year from federalreserve.gov and bls.gov.
public static class EconomicEventCalendar
{
    // Caution window around the event time: pre-positioning churn starts before the print
    // and the first hour after is the violent repricing.
    public static readonly TimeSpan WindowBefore = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan WindowAfter = TimeSpan.FromMinutes(60);

    private sealed record ScheduledEvent(DateTimeOffset TimeUtc, string Label);

    // FOMC statements 14:00 ET; CPI releases 08:30 ET. US DST 2026: Mar 8 – Nov 1.
    private static readonly ScheduledEvent[] Events =
    [
        // FOMC 2026 rate decisions (second meeting day)
        new(new DateTimeOffset(2026, 1, 28, 19, 0, 0, TimeSpan.Zero), "FOMC rate decision"),
        new(new DateTimeOffset(2026, 3, 18, 18, 0, 0, TimeSpan.Zero), "FOMC rate decision"),
        new(new DateTimeOffset(2026, 4, 29, 18, 0, 0, TimeSpan.Zero), "FOMC rate decision"),
        new(new DateTimeOffset(2026, 6, 17, 18, 0, 0, TimeSpan.Zero), "FOMC rate decision"),
        new(new DateTimeOffset(2026, 7, 29, 18, 0, 0, TimeSpan.Zero), "FOMC rate decision"),
        new(new DateTimeOffset(2026, 9, 16, 18, 0, 0, TimeSpan.Zero), "FOMC rate decision"),
        new(new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.Zero), "FOMC rate decision"),
        new(new DateTimeOffset(2026, 12, 9, 19, 0, 0, TimeSpan.Zero), "FOMC rate decision"),

        // US CPI 2026 releases (BLS schedule)
        new(new DateTimeOffset(2026, 1, 13, 13, 30, 0, TimeSpan.Zero), "US CPI release"),
        new(new DateTimeOffset(2026, 2, 11, 13, 30, 0, TimeSpan.Zero), "US CPI release"),
        new(new DateTimeOffset(2026, 3, 11, 12, 30, 0, TimeSpan.Zero), "US CPI release"),
        new(new DateTimeOffset(2026, 4, 10, 12, 30, 0, TimeSpan.Zero), "US CPI release"),
        new(new DateTimeOffset(2026, 5, 12, 12, 30, 0, TimeSpan.Zero), "US CPI release"),
        new(new DateTimeOffset(2026, 6, 10, 12, 30, 0, TimeSpan.Zero), "US CPI release"),
        new(new DateTimeOffset(2026, 7, 14, 12, 30, 0, TimeSpan.Zero), "US CPI release"),
        new(new DateTimeOffset(2026, 8, 11, 12, 30, 0, TimeSpan.Zero), "US CPI release"),
        new(new DateTimeOffset(2026, 9, 11, 12, 30, 0, TimeSpan.Zero), "US CPI release"),
        new(new DateTimeOffset(2026, 10, 13, 12, 30, 0, TimeSpan.Zero), "US CPI release"),
        new(new DateTimeOffset(2026, 11, 10, 13, 30, 0, TimeSpan.Zero), "US CPI release"),
        new(new DateTimeOffset(2026, 12, 10, 13, 30, 0, TimeSpan.Zero), "US CPI release"),
    ];

    // The active event window at nowUtc from the STATIC list, or null. Used as the
    // fail-safe when the live calendar feed (Forex Factory) is unavailable.
    public static string? GetActiveEventLabel(DateTimeOffset nowUtc)
        => ActiveLabel(Events.Select(e => (e.TimeUtc, e.Label)), nowUtc);

    // Shared window logic for any event source (live feed or static list). When two
    // windows overlap the nearest event wins.
    public static string? ActiveLabel(
        IEnumerable<(DateTimeOffset TimeUtc, string Label)> events, DateTimeOffset nowUtc)
        => events
            .Where(e => nowUtc >= e.TimeUtc - WindowBefore && nowUtc <= e.TimeUtc + WindowAfter)
            .OrderBy(e => (e.TimeUtc - nowUtc).Duration())
            .Select(e => $"{e.Label} at {e.TimeUtc:HH:mm} UTC — scheduled-event volatility window")
            .FirstOrDefault();
}
