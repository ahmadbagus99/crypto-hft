namespace CryptoHft.Infrastructure.Notifications;

public sealed class ApnsOptions
{
    public bool Enabled { get; set; }
    public string TeamId { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public string BundleId { get; set; } = "com.ahmadbagus.cryptoHFT";
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string PrivateKeyBase64 { get; set; } = string.Empty;

    public bool IsConfigured => Enabled
        && !string.IsNullOrWhiteSpace(TeamId)
        && !string.IsNullOrWhiteSpace(KeyId)
        && !string.IsNullOrWhiteSpace(BundleId)
        && (!string.IsNullOrWhiteSpace(PrivateKeyBase64)
            || !string.IsNullOrWhiteSpace(PrivateKeyPath));
}
