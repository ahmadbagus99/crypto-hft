using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoHft.Application.Account;
using CryptoHft.Application.Notifications;
using CryptoHft.Domain.Entities;
using CryptoHft.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Notifications;

public sealed class ApnsPushNotificationService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<ApnsOptions> options,
    ILogger<ApnsPushNotificationService> logger) : IPushNotificationService
{
    private readonly ApnsOptions _options = options.Value;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _providerToken;
    private DateTimeOffset _providerTokenCreatedAt;

    public async Task<PushDeviceRegistrationResult> RegisterDeviceAsync(
        RegisterPushDeviceRequest request,
        CancellationToken cancellationToken)
    {
        var token = NormalizeDeviceToken(request.DeviceToken);
        var environment = NormalizeEnvironment(request.Environment);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var device = await db.PushDevices
            .FirstOrDefaultAsync(x => x.DeviceToken == token, cancellationToken);

        if (device is null)
        {
            db.PushDevices.Add(new PushDevice
            {
                DeviceToken = token,
                Platform = "ios",
                Environment = environment
            });
        }
        else
        {
            device.Platform = "ios";
            device.Environment = environment;
            device.Enabled = true;
            device.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new PushDeviceRegistrationResult(true, environment);
    }

    public async Task UnregisterDeviceAsync(string deviceToken, CancellationToken cancellationToken)
    {
        var token = NormalizeDeviceToken(deviceToken);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var device = await db.PushDevices
            .FirstOrDefaultAsync(x => x.DeviceToken == token, cancellationToken);
        if (device is null) return;

        device.Enabled = false;
        device.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task NotifyPositionOpenedAsync(
        FuturesPositionInfo position,
        CancellationToken cancellationToken)
    {
        var message = TradingPushMessageFactory.PositionOpened(position);
        return BroadcastAsync(
            message.Title,
            message.Body,
            message.EventType,
            message.Symbol,
            cancellationToken);
    }

    public Task NotifyPositionClosedAsync(
        ClosedPositionPush position,
        CancellationToken cancellationToken)
    {
        var message = TradingPushMessageFactory.PositionClosed(position);
        return BroadcastAsync(
            message.Title,
            message.Body,
            message.EventType,
            message.Symbol,
            cancellationToken);
    }

    private async Task BroadcastAsync(
        string title,
        string body,
        string eventType,
        string symbol,
        CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            logger.LogDebug("APNs is disabled or incomplete; push event {EventType} was skipped", eventType);
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var devices = await db.PushDevices
                .AsNoTracking()
                .Where(x => x.Enabled && x.Platform == "ios")
                .ToListAsync(cancellationToken);

            foreach (var device in devices)
            {
                await SendAsync(device, title, body, eventType, symbol, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to broadcast APNs event {EventType}", eventType);
        }
    }

    private async Task SendAsync(
        PushDevice device,
        string title,
        string body,
        string eventType,
        string symbol,
        CancellationToken cancellationToken)
    {
        var host = device.Environment == "production"
            ? "https://api.push.apple.com"
            : "https://api.sandbox.push.apple.com";
        var providerToken = await GetProviderTokenAsync(cancellationToken);
        var payload = new Dictionary<string, object?>
        {
            ["aps"] = new Dictionary<string, object?>
            {
                ["alert"] = new { title, body },
                ["sound"] = "default",
                ["thread-id"] = $"position-{symbol.ToLowerInvariant()}"
            },
            ["event"] = eventType,
            ["symbol"] = symbol
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{host}/3/device/{device.DeviceToken}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = JsonContent.Create(payload)
        };
        request.Headers.TryAddWithoutValidation("authorization", $"bearer {providerToken}");
        request.Headers.TryAddWithoutValidation("apns-topic", _options.BundleId);
        request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
        request.Headers.TryAddWithoutValidation("apns-priority", "10");
        request.Headers.TryAddWithoutValidation("apns-expiration", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return;

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogWarning(
            "APNs rejected token ending {TokenSuffix}: {Status} {Body}",
            device.DeviceToken[^Math.Min(8, device.DeviceToken.Length)..],
            (int)response.StatusCode,
            responseBody);

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Gone
            && IsInvalidTokenResponse(responseBody))
        {
            await DisableDeviceAsync(device.DeviceToken, cancellationToken);
        }
    }

    private async Task<string> GetProviderTokenAsync(CancellationToken cancellationToken)
    {
        if (_providerToken is not null
            && DateTimeOffset.UtcNow - _providerTokenCreatedAt < TimeSpan.FromMinutes(50))
            return _providerToken;

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_providerToken is not null
                && DateTimeOffset.UtcNow - _providerTokenCreatedAt < TimeSpan.FromMinutes(50))
                return _providerToken;

            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = _options.KeyId }));
            var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = _options.TeamId,
                iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }));
            var unsignedToken = $"{header}.{payload}";

            using var key = ECDsa.Create();
            key.ImportFromPem(LoadPrivateKey());
            var signature = key.SignData(
                Encoding.ASCII.GetBytes(unsignedToken),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            _providerToken = $"{unsignedToken}.{Base64Url(signature)}";
            _providerTokenCreatedAt = DateTimeOffset.UtcNow;
            return _providerToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private string LoadPrivateKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.PrivateKeyBase64))
            return Encoding.UTF8.GetString(Convert.FromBase64String(_options.PrivateKeyBase64));
        return File.ReadAllText(_options.PrivateKeyPath);
    }

    private async Task DisableDeviceAsync(string deviceToken, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var device = await db.PushDevices
            .FirstOrDefaultAsync(x => x.DeviceToken == deviceToken, cancellationToken);
        if (device is null) return;
        device.Enabled = false;
        device.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsInvalidTokenResponse(string body)
        => body.Contains("BadDeviceToken", StringComparison.OrdinalIgnoreCase)
           || body.Contains("DeviceTokenNotForTopic", StringComparison.OrdinalIgnoreCase)
           || body.Contains("Unregistered", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDeviceToken(string value)
    {
        var token = value.Trim().ToLowerInvariant();
        if (token.Length < 32 || token.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A valid APNs device token is required.", nameof(value));
        return token;
    }

    private static string NormalizeEnvironment(string value)
    {
        var environment = value.Trim().ToLowerInvariant();
        if (environment is not ("sandbox" or "production"))
            throw new ArgumentException("Environment must be sandbox or production.", nameof(value));
        return environment;
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
