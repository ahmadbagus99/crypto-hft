using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Trading;
using CryptoHft.Infrastructure.Binance;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Trading;

public sealed class BinanceFuturesKillSwitchService(
    IHttpClientFactory httpClientFactory,
    IRuntimeTradingSettingsService runtimeSettings,
    IOptions<BinanceOptions> options) : IKillSwitchService
{
    private readonly BinanceOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private KillSwitchState _state = new(
        Symbol: options.Value.Symbol.ToUpperInvariant(),
        Enabled: false,
        CountdownTimeMs: 120_000,
        HeartbeatIntervalMs: 30_000,
        LastHeartbeatAt: null,
        NextHeartbeatAt: null,
        IsPaper: runtimeSettings.GetRuntimeSettings().PaperTradingOnly,
        Message: "Kill switch disabled");

    public KillSwitchState GetState() => _state;

    public async Task<KillSwitchState> EnableAsync(KillSwitchRequest request, CancellationToken cancellationToken)
    {
        var symbol = string.IsNullOrWhiteSpace(request.Symbol) ? _options.Symbol : request.Symbol;
        var countdownTimeMs = Math.Clamp(request.CountdownTimeMs, 1_000, 600_000);
        var heartbeatIntervalMs = Math.Clamp(request.HeartbeatIntervalMs, 5_000, Math.Max(5_000, countdownTimeMs / 2));

        await _lock.WaitAsync(cancellationToken);
        try
        {
            _state = _state with
            {
                Symbol = symbol.ToUpperInvariant(),
                Enabled = true,
                CountdownTimeMs = countdownTimeMs,
                HeartbeatIntervalMs = heartbeatIntervalMs,
                Message = "Kill switch enabled"
            };

            return await SendHeartbeatCoreAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<KillSwitchState> DisableAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var settings = runtimeSettings.GetRuntimeSettings();
            if (!settings.PaperTradingOnly)
            {
                await SetCountdownCancelAllAsync(_state.Symbol, 0, cancellationToken);
            }

            _state = _state with
            {
                Enabled = false,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                NextHeartbeatAt = null,
                IsPaper = settings.PaperTradingOnly,
                Message = settings.PaperTradingOnly
                    ? "Paper kill switch disabled"
                    : "Binance countdown cancel-all disabled"
            };

            return _state;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<KillSwitchState> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (!_state.Enabled)
        {
            return _state;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            return _state.Enabled ? await SendHeartbeatCoreAsync(cancellationToken) : _state;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<KillSwitchState> SendHeartbeatCoreAsync(CancellationToken cancellationToken)
    {
        var settings = runtimeSettings.GetRuntimeSettings();
        if (!settings.PaperTradingOnly)
        {
            if (!HasCredentials(settings))
            {
                throw new InvalidOperationException("Binance API key/secret required for live futures kill switch.");
            }

            await SetCountdownCancelAllAsync(_state.Symbol, _state.CountdownTimeMs, cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        _state = _state with
        {
            LastHeartbeatAt = now,
            NextHeartbeatAt = now.AddMilliseconds(_state.HeartbeatIntervalMs),
            IsPaper = settings.PaperTradingOnly,
            Message = settings.PaperTradingOnly
                ? "Paper kill switch heartbeat accepted"
                : "Binance countdown cancel-all heartbeat accepted"
        };

        return _state;
    }

    private async Task SetCountdownCancelAllAsync(string symbol, long countdownTimeMs, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["countdownTime"] = countdownTimeMs,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["recvWindow"] = 5000
        };
        parameters["signature"] = Sign(parameters);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.RestBaseUrl.TrimEnd('/')}/fapi/v1/countdownCancelAll")
        {
            Content = new FormUrlEncodedContent(parameters.ToDictionary(
                pair => pair.Key,
                pair => FormatValue(pair.Value!)))
        };
        var settings = runtimeSettings.GetRuntimeSettings();
        request.Headers.Add("X-MBX-APIKEY", settings.ApiKey);

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(body);
        }
    }

    private string Sign(Dictionary<string, object?> parameters)
    {
        var payload = string.Join("&", parameters
            .Where(pair => pair.Value is not null && pair.Key != "signature")
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={FormatValue(pair.Value!)}"));

        var settings = runtimeSettings.GetRuntimeSettings();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(settings.ApiSecret!));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static bool HasCredentials(RuntimeTradingSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.ApiKey) && !string.IsNullOrWhiteSpace(settings.ApiSecret);
    }

    private static string FormatValue(object value)
    {
        return value switch
        {
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            double number => number.ToString(CultureInfo.InvariantCulture),
            float number => number.ToString(CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
    }
}
