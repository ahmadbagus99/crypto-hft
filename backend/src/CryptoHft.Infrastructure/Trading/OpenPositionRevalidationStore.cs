using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;

namespace CryptoHft.Infrastructure.Trading;

public sealed class OpenPositionRevalidationStore : IOpenPositionRevalidationStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public void StartOrUpdatePosition(string symbol, TradeSide side, decimal quantity, decimal entryPrice, DateTimeOffset nextCheckAt)
    {
        symbol = symbol.ToUpperInvariant();
        lock (_lock)
        {
            if (!_states.TryGetValue(symbol, out var state) || state.OpenSide != side || state.EntryPrice != entryPrice)
            {
                _states[symbol] = new State(side, quantity, entryPrice, DateTimeOffset.UtcNow, nextCheckAt, []);
                return;
            }

            state.Quantity = quantity;
            state.NextCheckAt = nextCheckAt;
        }
    }

    public void SetNextCheck(string symbol, DateTimeOffset nextCheckAt)
    {
        symbol = symbol.ToUpperInvariant();
        lock (_lock)
        {
            if (_states.TryGetValue(symbol, out var state))
                state.NextCheckAt = nextCheckAt;
        }
    }

    public void Add(OpenPositionRevalidationRecord record, DateTimeOffset nextCheckAt)
    {
        var symbol = record.Symbol.ToUpperInvariant();
        lock (_lock)
        {
            if (!_states.TryGetValue(symbol, out var state) || state.OpenSide != record.OpenSide)
            {
                state = new State(record.OpenSide, record.Quantity, record.EntryPrice, DateTimeOffset.UtcNow, nextCheckAt, []);
                _states[symbol] = state;
            }

            state.Quantity = record.Quantity;
            state.NextCheckAt = nextCheckAt;
            state.Records.Insert(0, record);
            if (state.Records.Count > 24)
                state.Records.RemoveRange(24, state.Records.Count - 24);
        }
    }

    public void Clear(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        lock (_lock)
        {
            _states.Remove(symbol);
        }
    }

    public OpenPositionRevalidationSnapshot Get(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        lock (_lock)
        {
            if (!_states.TryGetValue(symbol, out var state))
            {
                return new OpenPositionRevalidationSnapshot(
                    symbol,
                    OpenSide: null,
                    Quantity: 0m,
                    EntryPrice: 0m,
                    OpenedAt: null,
                    NextCheckAt: null,
                    Records: Array.Empty<OpenPositionRevalidationRecord>());
            }

            return new OpenPositionRevalidationSnapshot(
                symbol,
                state.OpenSide,
                state.Quantity,
                state.EntryPrice,
                state.OpenedAt,
                state.NextCheckAt,
                state.Records.ToList());
        }
    }

    private sealed class State(
        TradeSide openSide,
        decimal quantity,
        decimal entryPrice,
        DateTimeOffset openedAt,
        DateTimeOffset nextCheckAt,
        List<OpenPositionRevalidationRecord> records)
    {
        public TradeSide OpenSide { get; } = openSide;
        public decimal Quantity { get; set; } = quantity;
        public decimal EntryPrice { get; } = entryPrice;
        public DateTimeOffset OpenedAt { get; } = openedAt;
        public DateTimeOffset NextCheckAt { get; set; } = nextCheckAt;
        public List<OpenPositionRevalidationRecord> Records { get; } = records;
    }
}
