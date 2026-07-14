using System.Net.Http.Json;
using CryptoHft.Application.Account;
using CryptoHft.Application.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Notifications;

public sealed class BarkPushNotificationService(
    IHttpClientFactory httpClientFactory,
    IOptions<BarkOptions> options,
    ILogger<BarkPushNotificationService> logger)
{
    private readonly BarkOptions _options = options.Value;

    public Task NotifyPositionOpenedAsync(
        FuturesPositionInfo position,
        CancellationToken cancellationToken)
        => SendAsync(TradingPushMessageFactory.PositionOpened(position), cancellationToken);

    public Task NotifyPositionClosedAsync(
        ClosedPositionPush position,
        CancellationToken cancellationToken)
        => SendAsync(TradingPushMessageFactory.PositionClosed(position), cancellationToken);

    private async Task SendAsync(
        TradingPushMessage message,
        CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            logger.LogDebug("Bark is disabled or incomplete; push event {EventType} was skipped", message.EventType);
            return;
        }

        try
        {
            var useDirectPushUrl = !string.IsNullOrWhiteSpace(_options.PushUrl);
            var endpoint = useDirectPushUrl
                ? _options.PushUrl.Trim()
                : $"{_options.ServerUrl.Trim().TrimEnd('/')}/push";
            var payload = new Dictionary<string, object?>
            {
                ["title"] = message.Title,
                ["body"] = message.Body,
                ["group"] = _options.Group,
                ["sound"] = _options.Sound,
                ["level"] = _options.Level,
                ["isArchive"] = 1
            };

            if (!useDirectPushUrl)
                payload["device_key"] = _options.DeviceKey.Trim();
            if (!string.IsNullOrWhiteSpace(_options.IconUrl))
                payload["icon"] = _options.IconUrl.Trim();
            if (!string.IsNullOrWhiteSpace(_options.OpenUrl))
                payload["url"] = _options.OpenUrl.Trim();

            using var response = await httpClientFactory.CreateClient()
                .PostAsJsonAsync(endpoint, payload, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Bark delivered push event {EventType}", message.EventType);
                return;
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "Bark rejected push event {EventType}: {Status} {Body}",
                message.EventType,
                (int)response.StatusCode,
                responseBody);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to send Bark push event {EventType}", message.EventType);
        }
    }
}
