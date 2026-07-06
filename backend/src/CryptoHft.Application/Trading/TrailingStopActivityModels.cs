using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.Trading;

// One successful trailing-stop ratchet: the protective stop moved PreviousStopLoss -> NewStopLoss
// while the position was ProfitR in profit.
public sealed record TrailingStopEvent(
    string Symbol,
    TradeSide PositionSide,
    decimal EntryPrice,
    decimal MarkPrice,
    decimal? PreviousStopLoss,
    decimal NewStopLoss,
    decimal ProfitR,
    string Reason,
    DateTimeOffset RatchetedAt);

// Live view of the trailing guard for the CURRENT position only. PositionSide is null when
// flat — the store is cleared on position close, so history never mixes two positions.
public sealed record TrailingStopSnapshot(
    string Symbol,
    TradeSide? PositionSide,
    decimal EntryPrice,
    decimal? InitialStopLoss,
    decimal? CurrentStopLoss,
    IReadOnlyList<TrailingStopEvent> Events);

public interface ITrailingStopActivityStore
{
    // Called every guard tick so the dashboard knows the guard is watching even before the
    // first ratchet. A different side/entry resets the state (new position).
    void StartOrUpdatePosition(string symbol, TradeSide side, decimal entryPrice, decimal? initialStopLoss, decimal? currentStopLoss);
    void Record(TrailingStopEvent evt);
    void Clear(string symbol);
    TrailingStopSnapshot Get(string symbol);
}
