using CryptoHft.Domain.Enums;

namespace CryptoHft.Application.Trading;

// One setup the engine put forward and Claude turned down. Kept so the owner can see what
// the auditor has been refusing rather than only that nothing opened — a run of refusals
// with the counts attached is the difference between "the market gave nothing" and "the
// auditor is too strict", and those need different responses.
public sealed record AiAuditRefusal(
    string Symbol,
    TradeSide ProposedSide,
    decimal EngineConfidence,     // what the engine was willing to trade on
    decimal AdjustedConfidence,   // what Claude made of it
    int? AlignedCount,            // Claude's own tallies, null when it did not report them
    int? BlockingCount,
    string Narrative,             // Claude's reasoning, including the bar and level it read
    string Reason,                // the entry-policy verdict text
    DateTimeOffset RefusedAt);

// The refusals since the log was last reset. The window is deliberately one position's
// worth: refusals accumulate, survive the position that eventually opens so the run leading
// up to it stays readable, and clear when that position closes.
public sealed record AiAuditLogSnapshot(
    string Symbol,
    IReadOnlyList<AiAuditRefusal> Refusals,
    DateTimeOffset? SinceAt);

public interface IAiAuditLogStore
{
    void Record(AiAuditRefusal refusal);
    // Called when a position closes — the next run starts from empty.
    void Clear(string symbol);
    AiAuditLogSnapshot Get(string symbol);
}
