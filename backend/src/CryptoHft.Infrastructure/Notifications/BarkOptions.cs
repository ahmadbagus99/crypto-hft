namespace CryptoHft.Infrastructure.Notifications;

public sealed class BarkOptions
{
    public bool Enabled { get; set; }
    public string PushUrl { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = "https://api.day.app";
    public string DeviceKey { get; set; } = string.Empty;
    public string Sound { get; set; } = "minuet";
    public string Group { get; set; } = "seqra-quant";
    public string Level { get; set; } = "timeSensitive";
    public string IconUrl { get; set; } = string.Empty;
    public string OpenUrl { get; set; } = "https://trading.seqra.space";

    public bool IsConfigured => Enabled
        && (IsAbsoluteHttpUrl(PushUrl)
            || (IsAbsoluteHttpUrl(ServerUrl) && !string.IsNullOrWhiteSpace(DeviceKey)));

    private static bool IsAbsoluteHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https";
}
