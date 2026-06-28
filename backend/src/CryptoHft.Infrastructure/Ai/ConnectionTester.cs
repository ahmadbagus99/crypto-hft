using Anthropic;
using Anthropic.Models.Messages;
using CryptoHft.Application.Abstractions;
using CryptoHft.Application.Trading;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Ai;

// Verifies that the configured Binance and Anthropic credentials actually work,
// for the "Test connection" buttons in Settings.
public sealed class ConnectionTester(
    IFuturesAccountClient accountClient,
    IRuntimeTradingSettingsService settingsService,
    IOptions<AiOptions> aiOptions) : IConnectionTester
{
    private readonly AiOptions _aiOptions = aiOptions.Value;

    public async Task<ConnectionTestResult> TestBinanceAsync(CancellationToken cancellationToken)
    {
        var settings = settingsService.GetRuntimeSettings();
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.ApiSecret))
            return new ConnectionTestResult(false, "No Binance API key/secret configured");

        try
        {
            var wallets = await accountClient.GetWalletBalancesAsync(cancellationToken);
            var usdt = wallets.FirstOrDefault(w => w.Asset == "USDT");
            var detail = usdt is not null ? $"USDT balance {usdt.Balance:F2}" : $"{wallets.Count} assets";
            return new ConnectionTestResult(true, "Binance Futures connected", detail);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, "Binance authentication failed", Trim(ex.Message));
        }
    }

    public async Task<ConnectionTestResult> TestAnthropicAsync(CancellationToken cancellationToken)
    {
        var runtime = settingsService.GetRuntimeSettings().AnthropicApiKey;
        var apiKey = !string.IsNullOrWhiteSpace(runtime) ? runtime : _aiOptions.AnthropicApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ConnectionTestResult(false, "No Anthropic API key configured");

        try
        {
            var client = new AnthropicClient { ApiKey = apiKey };
            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = _aiOptions.Model,
                MaxTokens = 8,
                Messages = [new() { Role = Role.User, Content = "ping" }]
            });
            var model = response.Model.ToString();
            return new ConnectionTestResult(true, "Anthropic API connected", $"model {model}");
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, "Anthropic authentication failed", Trim(ex.Message));
        }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}
