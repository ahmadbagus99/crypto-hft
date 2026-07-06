using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;

namespace CryptoHft.Infrastructure.Trading;

// In-memory, current-position-only view of the trailing guard (same lifecycle as
// OpenPositionRevalidationStore): reset when a different position appears, cleared by the
// worker when the position closes. The permanent audit trail lives in the Orders table.
public sealed class TrailingStopActivityStore : ITrailingStopActivityStore
{
    private const int MaxEvents = 50;

    private readonly object _lock = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public void StartOrUpdatePosition(string symbol, TradeSide side, decimal entryPrice, decimal? initialStopLoss, decimal? currentStopLoss)
    {
        symbol = symbol.ToUpperInvariant();
        lock (_lock)
        {
            if (!_states.TryGetValue(symbol, out var state) || state.Side != side || state.EntryPrice != entryPrice)
            {
                _states[symbol] = new State(side, entryPrice) { InitialStopLoss = initialStopLoss, CurrentStopLoss = currentStopLoss };
                return;
            }

            state.InitialStopLoss ??= initialStopLoss;
            if (currentStopLoss is not null)
                state.CurrentStopLoss = currentStopLoss;
        }
    }

    public void Record(TrailingStopEvent evt)
    {
        var symbol = evt.Symbol.ToUpperInvariant();
        lock (_lock)
        {
            if (!_states.TryGetValue(symbol, out var state) || state.Side != evt.PositionSide || state.EntryPrice != evt.EntryPrice)
            {
                state = new State(evt.PositionSide, evt.EntryPrice) { InitialStopLoss = evt.PreviousStopLoss };
                _states[symbol] = state;
            }

            state.CurrentStopLoss = evt.NewStopLoss;
            state.Events.Insert(0, evt);
            if (state.Events.Count > MaxEvents)
                state.Events.RemoveRange(MaxEvents, state.Events.Count - MaxEvents);
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

    public TrailingStopSnapshot Get(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        lock (_lock)
        {
            if (!_states.TryGetValue(symbol, out var state))
            {
                return new TrailingStopSnapshot(
                    symbol,
                    PositionSide: null,
                    EntryPrice: 0m,
                    InitialStopLoss: null,
                    CurrentStopLoss: null,
                    Events: Array.Empty<TrailingStopEvent>());
            }

            return new TrailingStopSnapshot(
                symbol,
                state.Side,
                state.EntryPrice,
                state.InitialStopLoss,
                state.CurrentStopLoss,
                state.Events.ToList());
        }
    }

    private sealed class State(TradeSide side, decimal entryPrice)
    {
        public TradeSide Side { get; } = side;
        public decimal EntryPrice { get; } = entryPrice;
        public decimal? InitialStopLoss { get; set; }
        public decimal? CurrentStopLoss { get; set; }
        public List<TrailingStopEvent> Events { get; } = [];
    }
}
