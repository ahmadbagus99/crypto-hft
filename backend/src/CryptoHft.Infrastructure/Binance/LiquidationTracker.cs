using CryptoHft.Application.DecisionEngine;

namespace CryptoHft.Infrastructure.Binance;

// Thread-safe rolling window of forced-liquidation events from the forceOrder stream.
// Registered as a singleton: the websocket dispatcher writes, the derivatives provider
// reads. Events older than the retention horizon are pruned on every touch, so memory
// stays bounded even through a liquidation cascade.
public sealed class LiquidationTracker : ILiquidationFeed
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Queue<(DateTimeOffset Time, bool Long, decimal Notional)> _events = new();

    public void Record(bool longLiquidated, decimal notionalUsd, DateTimeOffset time)
    {
        if (notionalUsd <= 0) return;
        lock (_gate)
        {
            _events.Enqueue((time, longLiquidated, notionalUsd));
            Prune(time);
        }
    }

    public (decimal LongNotional, decimal ShortNotional) GetWindowNotional(TimeSpan window)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now - window;
        lock (_gate)
        {
            Prune(now);
            decimal longSum = 0, shortSum = 0;
            foreach (var (time, isLong, notional) in _events)
            {
                if (time < cutoff) continue;
                if (isLong) longSum += notional; else shortSum += notional;
            }
            return (longSum, shortSum);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - Retention;
        while (_events.Count > 0 && _events.Peek().Time < cutoff)
            _events.Dequeue();
    }
}
