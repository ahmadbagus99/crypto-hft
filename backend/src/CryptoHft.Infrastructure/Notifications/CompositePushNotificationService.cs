using CryptoHft.Application.Account;
using CryptoHft.Application.Notifications;

namespace CryptoHft.Infrastructure.Notifications;

public sealed class CompositePushNotificationService(
    ApnsPushNotificationService apns,
    BarkPushNotificationService bark) : IPushNotificationService
{
    public Task<PushDeviceRegistrationResult> RegisterDeviceAsync(
        RegisterPushDeviceRequest request,
        CancellationToken cancellationToken)
        => apns.RegisterDeviceAsync(request, cancellationToken);

    public Task UnregisterDeviceAsync(string deviceToken, CancellationToken cancellationToken)
        => apns.UnregisterDeviceAsync(deviceToken, cancellationToken);

    public async Task NotifyPositionOpenedAsync(
        FuturesPositionInfo position,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            apns.NotifyPositionOpenedAsync(position, cancellationToken),
            bark.NotifyPositionOpenedAsync(position, cancellationToken));
    }

    public async Task NotifyPositionClosedAsync(
        ClosedPositionPush position,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(
            apns.NotifyPositionClosedAsync(position, cancellationToken),
            bark.NotifyPositionClosedAsync(position, cancellationToken));
    }
}
