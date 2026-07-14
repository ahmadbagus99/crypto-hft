using CryptoHft.Application.Account;
using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.Notifications;

public sealed record RegisterPushDeviceRequest(
    string DeviceToken,
    string Environment,
    string Platform = "ios");

public sealed record PushDeviceRegistrationResult(
    bool Registered,
    string Environment);

public sealed record ClosedPositionPush(
    string Symbol,
    TradeSide Side,
    decimal Quantity,
    decimal EntryPrice,
    decimal MarkPrice,
    decimal RealizedPnl,
    PositionCloseReason CloseReason);

public interface IPushNotificationService
{
    Task<PushDeviceRegistrationResult> RegisterDeviceAsync(
        RegisterPushDeviceRequest request,
        CancellationToken cancellationToken);

    Task UnregisterDeviceAsync(string deviceToken, CancellationToken cancellationToken);

    Task NotifyPositionOpenedAsync(
        FuturesPositionInfo position,
        CancellationToken cancellationToken);

    Task NotifyPositionClosedAsync(
        ClosedPositionPush position,
        CancellationToken cancellationToken);
}
