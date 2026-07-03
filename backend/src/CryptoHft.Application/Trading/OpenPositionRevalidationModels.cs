using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.Trading;

public sealed record OpenPositionRevalidationRecord(
    string Symbol,
    TradeSide OpenSide,
    decimal Quantity,
    decimal EntryPrice,
    decimal MarkPrice,
    decimal UnrealizedProfit,
    decimal OppositeConfidence,
    OpenPositionRevalidationAction Action,
    string Reason,
    DateTimeOffset CheckedAt);

public sealed record OpenPositionRevalidationSnapshot(
    string Symbol,
    TradeSide? OpenSide,
    decimal Quantity,
    decimal EntryPrice,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? NextCheckAt,
    IReadOnlyList<OpenPositionRevalidationRecord> Records);

public interface IOpenPositionRevalidationStore
{
    void StartOrUpdatePosition(string symbol, TradeSide side, decimal quantity, decimal entryPrice, DateTimeOffset nextCheckAt);
    void SetNextCheck(string symbol, DateTimeOffset nextCheckAt);
    void Add(OpenPositionRevalidationRecord record, DateTimeOffset nextCheckAt);
    void Clear(string symbol);
    OpenPositionRevalidationSnapshot Get(string symbol);
}
