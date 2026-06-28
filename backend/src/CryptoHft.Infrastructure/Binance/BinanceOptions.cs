namespace CryptoHft.Infrastructure.Binance;

public sealed class BinanceOptions
{
    public string Environment { get; init; } = "Testnet";
    public string RestBaseUrl { get; init; } = "https://fapi.binance.com";
    public string WebSocketBaseUrl { get; init; } = "wss://fstream.binance.com/ws";
    public string WebSocketApiBaseUrl { get; init; } = "wss://ws-fapi.binance.com/ws-fapi/v1";
    public string Symbol { get; init; } = "btcusdt";
    public string[] KlineIntervals { get; init; } = ["1m", "5m", "15m", "1h", "4h", "1d"];
    public string? ApiKey { get; init; }
    public string? ApiSecret { get; init; }
    public bool PaperTradingOnly { get; init; } = true;
}
