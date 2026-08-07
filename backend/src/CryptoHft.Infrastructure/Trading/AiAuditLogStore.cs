using CryptoHft.Application.Trading;

namespace CryptoHft.Infrastructure.Trading;

public sealed class AiAuditLogStore : IAiAuditLogStore
{
    // A refused audit rests 15 minutes before the next one, so a long quiet stretch fills
    // this slowly. The cap only exists so an unattended process cannot grow without bound.
    private const int MaxRefusals = 60;

    private readonly object _lock = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public void Record(AiAuditRefusal refusal)
    {
        var symbol = refusal.Symbol.ToUpperInvariant();
        lock (_lock)
        {
            if (!_states.TryGetValue(symbol, out var state))
                _states[symbol] = state = new State(DateTimeOffset.UtcNow, []);

            state.Refusals.Add(refusal);
            // Oldest first, so the newest refusals are the ones that survive a long run.
            if (state.Refusals.Count > MaxRefusals)
                state.Refusals.RemoveRange(0, state.Refusals.Count - MaxRefusals);
        }
    }

    public void Clear(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        lock (_lock) _states.Remove(symbol);
    }

    public AiAuditLogSnapshot Get(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        lock (_lock)
        {
            return _states.TryGetValue(symbol, out var state)
                ? new AiAuditLogSnapshot(symbol, state.Refusals.ToList(), state.SinceAt)
                : new AiAuditLogSnapshot(symbol, Array.Empty<AiAuditRefusal>(), null);
        }
    }

    private sealed record State(DateTimeOffset SinceAt, List<AiAuditRefusal> Refusals);
}
