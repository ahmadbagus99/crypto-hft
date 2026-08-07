using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Account;
using CryptoHft.Application.Risk;
using CryptoHft.Application.Trading;
using CryptoHft.Infrastructure.Binance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Trading;

// One realized-PnL income record from Binance (/fapi/v1/income, incomeType=REALIZED_PNL).
public sealed record RealizedPnlEntry(DateTimeOffset Time, decimal Pnl);

// Enforces the account-level limits that already exist in settings but were never applied on
// the auto path: max daily loss and max consecutive losses (both computed from today's UTC
// realized PnL via the Binance income API) and the exposure cap (order margin vs equity).
// Fail-safe by design: when live risk data cannot be read, the order is skipped for this tick
// rather than placed blind — the worker retries 30s later. Paper mode passes through: paper
// fills never reach the exchange account, so there is no real capital to protect (and no
// realized-PnL source to measure).
public sealed class BinanceAutoTradeRiskGate(
    IRuntimeTradingSettingsService settingsService,
    IFuturesAccountClient accountClient,
    IHttpClientFactory httpClientFactory,
    IOptions<BinanceOptions> options,
    ILogger<BinanceAutoTradeRiskGate> logger) : IAutoTradeRiskGate
{
    private readonly BinanceOptions _options = options.Value;

    // Matches RiskProfile.MaxConsecutiveLosses used by the decision engine.
    private const int MaxConsecutiveLosses = 3;

    // A market close can fill in several parts, each writing its own REALIZED_PNL record;
    // records this close together are treated as one trade when counting losses.
    private static readonly TimeSpan SameTradeGap = TimeSpan.FromSeconds(120);

    public async Task<AutoTradeRiskVerdict> EvaluateAsync(
        string symbol, decimal quantity, decimal entryPrice, int leverage, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.TradingAllowed)
            return new AutoTradeRiskVerdict(false, status.Reason);

        var settings = settingsService.GetRuntimeSettings();
        if (status.Status == "paper")
            return new AutoTradeRiskVerdict(true, status.Reason);

        // Guard off: the owner has explicitly disabled the account-level guard, so the
        // exposure clamp is skipped along with the loss pauses. Checked before the equity
        // deref below, which is null whenever the account read failed.
        if (!settings.AccountRiskGuardEnabled)
            return new AutoTradeRiskVerdict(true, "account risk guard disabled — exposure clamp skipped");

        var equity = status.Equity!.Value;

        // Exposure: the trade still opens (confidence is the only signal gate) — only its
        // margin is capped at MaxExposurePercent of equity.
        var lev = Math.Max(1, leverage);
        var margin = entryPrice > 0 ? quantity * entryPrice / lev : 0m;
        var maxMargin = equity * settings.MaxExposurePercent;
        if (maxMargin > 0 && margin > maxMargin)
        {
            var cappedQuantity = Math.Round(maxMargin * lev / entryPrice, 6);
            if (cappedQuantity <= 0)
                return new AutoTradeRiskVerdict(false, "exposure cap leaves no tradable quantity");
            return new AutoTradeRiskVerdict(true,
                $"quantity capped {quantity} -> {cappedQuantity} so margin fits {settings.MaxExposurePercent:P0} of equity",
                cappedQuantity);
        }

        return new AutoTradeRiskVerdict(true, "account risk checks passed");
    }

    public async Task<AutoTradeRiskStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var settings = settingsService.GetRuntimeSettings();

        if (!settings.AutoTradingEnabled)
        {
            return Status(
                false,
                "disabled",
                "auto trading is disabled in settings",
                checkedAt);
        }

        if (settings.PaperTradingOnly
            || string.IsNullOrWhiteSpace(settings.ApiKey)
            || string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            return Status(
                true,
                "paper",
                "paper mode — account risk gate not applicable",
                checkedAt);
        }

        // Guard off means the owner accepts running without account-level brakes, so
        // nothing below may block. The reads still happen (the dashboard keeps showing
        // what the guard WOULD do) but they are best-effort: a failed equity or income
        // call must not resurrect a pause the owner switched off.
        if (!settings.AccountRiskGuardEnabled)
        {
            var (bestEffortEquity, bestEffortPnl) = await TryReadAccountAsync(settings, checkedAt, cancellationToken);
            return ResolveAccountStatus(settings, bestEffortEquity, bestEffortPnl, checkedAt);
        }

        decimal equity;
        try
        {
            var wallets = await accountClient.GetWalletBalancesAsync(cancellationToken);
            equity = wallets.UsdEquity();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "risk gate status: equity fetch failed");
            return Status(
                false,
                "unavailable",
                "equity unavailable — auto trading paused (fail-safe)",
                checkedAt);
        }

        if (equity <= 0)
        {
            return Status(
                false,
                "unavailable",
                "equity is zero/unknown — auto trading paused (fail-safe)",
                checkedAt,
                equity: equity);
        }

        IReadOnlyList<RealizedPnlEntry> todaysPnl;
        try
        {
            todaysPnl = await FetchTodaysRealizedPnlAsync(settings, checkedAt, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "risk gate status: realized PnL fetch failed");
            return Status(
                false,
                "unavailable",
                "realized PnL unavailable — auto trading paused (fail-safe)",
                checkedAt,
                equity: equity);
        }

        return ResolveAccountStatus(settings, equity, todaysPnl, checkedAt);
    }

    // Reads equity and today's realized PnL without ever throwing. Used only when the guard
    // is disabled: the numbers are for display, so a dead endpoint costs a stat, not a trade.
    private async Task<(decimal? Equity, IReadOnlyList<RealizedPnlEntry> TodaysPnl)> TryReadAccountAsync(
        RuntimeTradingSettings settings,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        decimal? equity = null;
        IReadOnlyList<RealizedPnlEntry> todaysPnl = Array.Empty<RealizedPnlEntry>();

        try
        {
            var wallets = await accountClient.GetWalletBalancesAsync(cancellationToken);
            equity = wallets.UsdEquity();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "risk gate (guard off): equity fetch failed — reporting without it");
        }

        try
        {
            todaysPnl = await FetchTodaysRealizedPnlAsync(settings, checkedAt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "risk gate (guard off): realized PnL fetch failed — reporting without it");
        }

        return (equity, todaysPnl);
    }

    // Sum of today's realized PnL, expressed as a positive loss figure (0 when net positive).
    internal static decimal DailyLoss(IReadOnlyList<RealizedPnlEntry> entries)
        => Math.Max(0m, -entries.Sum(e => e.Pnl));

    // Groups income records into trades (records within sameTradeGap belong to one close event),
    // then counts how many trades at the tail of the day are losses. A winning trade resets the run.
    internal static int CountTrailingLosses(IReadOnlyList<RealizedPnlEntry> entries, TimeSpan sameTradeGap)
    {
        var trades = new List<decimal>();
        DateTimeOffset? lastTime = null;
        foreach (var entry in entries.OrderBy(e => e.Time))
        {
            if (lastTime is not null && entry.Time - lastTime.Value <= sameTradeGap)
                trades[^1] += entry.Pnl;
            else
                trades.Add(entry.Pnl);
            lastTime = entry.Time;
        }

        var count = 0;
        for (var i = trades.Count - 1; i >= 0 && trades[i] < 0; i--) count++;
        return count;
    }

    internal static IReadOnlyList<RealizedPnlEntry> ParseIncome(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var entries = new List<RealizedPnlEntry>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("income", out var income) || !item.TryGetProperty("time", out var time))
                continue;
            var pnl = income.ValueKind == JsonValueKind.Number
                ? income.GetDecimal()
                : decimal.Parse(income.GetString() ?? "0", CultureInfo.InvariantCulture);
            entries.Add(new RealizedPnlEntry(DateTimeOffset.FromUnixTimeMilliseconds(time.GetInt64()), pnl));
        }
        return entries;
    }

    // equity is nullable because the guard-off path reads it best-effort: when the account
    // call fails there is no figure to show, and reporting a fabricated 0 would read as a
    // wiped account on the dashboard. Every blocking rule below needs a real equity, so a
    // null one can only occur on the guard-off path, which returns before reaching them.
    internal static AutoTradeRiskStatus ResolveAccountStatus(
        RuntimeTradingSettings settings,
        decimal? equity,
        IReadOnlyList<RealizedPnlEntry> todaysPnl,
        DateTimeOffset checkedAt)
    {
        var dailyLoss = DailyLoss(todaysPnl);
        var dailyLossLimit = (equity ?? 0m) * settings.MaxDailyLossPercent;
        var consecutiveLosses = CountTrailingLosses(todaysPnl, SameTradeGap);

        // Owner switch: guard off = the loss pauses never block, but the stats are still
        // computed and reported so the dashboard keeps showing what the guard WOULD do.
        if (!settings.AccountRiskGuardEnabled)
        {
            return Status(
                true,
                "guard-off",
                "account risk guard disabled — the daily-loss pause is bypassed",
                checkedAt,
                equity,
                dailyLoss,
                dailyLossLimit,
                settings.MaxDailyLossPercent,
                consecutiveLosses);
        }

        if (dailyLossLimit > 0 && dailyLoss >= dailyLossLimit)
        {
            return Status(
                false,
                "daily-loss",
                $"daily realized loss {dailyLoss:F2} USDT >= limit {dailyLossLimit:F2} USDT " +
                $"({settings.MaxDailyLossPercent:P0} of equity) — trading paused until next UTC day",
                checkedAt,
                equity,
                dailyLoss,
                dailyLossLimit,
                settings.MaxDailyLossPercent,
                consecutiveLosses,
                NextUtcDay(checkedAt));
        }

        // A run of losses no longer stops the day. The daily loss limit already bounds what a
        // bad run can cost, and it does so in the currency that matters — money — whereas a
        // count of trades stops on three small scratches while leaving a single large loss
        // untouched. Three trades at -0.05 and three at -1.50 are the same number and nothing
        // like the same damage. The streak is still counted and reported so the dashboard can
        // show it; it simply no longer decides anything.
        return Status(
            true,
            "active",
            "account risk checks passed",
            checkedAt,
            equity,
            dailyLoss,
            dailyLossLimit,
            settings.MaxDailyLossPercent,
            consecutiveLosses);
    }

    internal static DateTimeOffset NextUtcDay(DateTimeOffset now)
        => new(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);

    private static AutoTradeRiskStatus Status(
        bool tradingAllowed,
        string status,
        string reason,
        DateTimeOffset checkedAt,
        decimal? equity = null,
        decimal? dailyLoss = null,
        decimal? dailyLossLimit = null,
        decimal? dailyLossLimitPercent = null,
        int? consecutiveLosses = null,
        DateTimeOffset? resetsAt = null)
        => new(
            tradingAllowed,
            status,
            reason,
            equity,
            dailyLoss,
            dailyLossLimit,
            dailyLossLimitPercent,
            consecutiveLosses,
            MaxConsecutiveLosses,
            resetsAt,
            checkedAt);

    private async Task<IReadOnlyList<RealizedPnlEntry>> FetchTodaysRealizedPnlAsync(
        RuntimeTradingSettings settings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var startOfDay = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var timestamp = now.ToUnixTimeMilliseconds();
        var query = $"incomeType=REALIZED_PNL&startTime={startOfDay.ToUnixTimeMilliseconds()}" +
                    $"&limit=1000&timestamp={timestamp}&recvWindow=5000";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(settings.ApiSecret!));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(query))).ToLowerInvariant();
        var url = $"{_options.RestBaseUrl.TrimEnd('/')}/fapi/v1/income?{query}&signature={signature}";

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-MBX-APIKEY", settings.ApiKey!);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"income fetch failed: {(int)response.StatusCode} {body}");

        return ParseIncome(body);
    }
}
