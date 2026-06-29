using System.Collections.Concurrent;

namespace CryptoHft.Application.DecisionEngine;

// Holds the most recent AI decision per symbol so the dashboard can display it
// without triggering a fresh (Claude-billed) analysis on every poll. The analysis
// loop is the single writer; the dashboard endpoint is a read-only consumer.
public interface ILatestDecisionStore
{
    void Set(string symbol, AdvancedDecision decision);
    AdvancedDecision? Get(string symbol);
}

public sealed class LatestDecisionStore : ILatestDecisionStore
{
    private readonly ConcurrentDictionary<string, AdvancedDecision> _decisions = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string symbol, AdvancedDecision decision) => _decisions[symbol] = decision;

    public AdvancedDecision? Get(string symbol) =>
        _decisions.TryGetValue(symbol, out var d) ? d : null;
}
