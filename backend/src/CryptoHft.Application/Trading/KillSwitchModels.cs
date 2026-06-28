namespace CryptoHft.Application.Trading;

public sealed record KillSwitchState(
    string Symbol,
    bool Enabled,
    long CountdownTimeMs,
    long HeartbeatIntervalMs,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? NextHeartbeatAt,
    bool IsPaper,
    string Message);

public sealed record KillSwitchRequest(
    string Symbol,
    long CountdownTimeMs,
    long HeartbeatIntervalMs);

