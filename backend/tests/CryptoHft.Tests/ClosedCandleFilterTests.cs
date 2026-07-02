using System.Text.Json;
using CryptoHft.Infrastructure.Ai;
using Xunit;

namespace CryptoHft.Tests;

public sealed class ClosedCandleFilterTests
{
    // Binance kline array: [openTime, open, high, low, close, volume, closeTime, ...]
    private static string Kline(long openTimeMs, long closeTimeMs, decimal close)
        => $"[{openTimeMs},\"100.0\",\"110.0\",\"90.0\",\"{close}\",\"12.5\",{closeTimeMs},\"0\",1,\"0\",\"0\",\"0\"]";

    [Fact]
    public void ParseClosedCandles_DropsStillFormingCandle()
    {
        var now = DateTimeOffset.UtcNow;
        var closedOpen = now.AddMinutes(-10).ToUnixTimeMilliseconds();
        var closedClose = now.AddMinutes(-5).ToUnixTimeMilliseconds();
        var formingOpen = now.AddMinutes(-5).ToUnixTimeMilliseconds();
        var formingClose = now.AddMinutes(5).ToUnixTimeMilliseconds(); // closes in the future

        var json = $"[{Kline(closedOpen, closedClose, 105m)},{Kline(formingOpen, formingClose, 999m)}]";
        using var doc = JsonDocument.Parse(json);

        var candles = BinanceMultiTimeframeProvider.ParseClosedCandles(doc.RootElement, now);

        Assert.Single(candles);
        Assert.Equal(105m, candles[0].Close);
    }

    [Fact]
    public void ParseClosedCandles_KeepsAllCandles_WhenAllClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var json = "[" + string.Join(",", Enumerable.Range(1, 3).Select(i =>
            Kline(now.AddMinutes(-i * 10).ToUnixTimeMilliseconds(),
                  now.AddMinutes(-i * 10 + 5).ToUnixTimeMilliseconds(),
                  100m + i))) + "]";
        using var doc = JsonDocument.Parse(json);

        var candles = BinanceMultiTimeframeProvider.ParseClosedCandles(doc.RootElement, now);

        Assert.Equal(3, candles.Count);
    }
}
