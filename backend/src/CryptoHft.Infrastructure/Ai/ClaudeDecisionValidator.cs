using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using CryptoHft.Application.Ai;
using CryptoHft.Application.DecisionEngine;
using CryptoHft.Application.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoHft.Infrastructure.Ai;

// Hybrid LLM layer. Feeds the rule engine's scores + market context to Claude and asks
// for a final confirm/veto with an adjusted confidence and a plain-language narrative.
// This is a confirmation gate, not the primary signal — if no API key, it passes through.
public sealed class ClaudeDecisionValidator(
    IOptions<AiOptions> options,
    IRuntimeTradingSettingsService settingsService,
    IAiUsageTracker usageTracker,
    ILogger<ClaudeDecisionValidator> logger) : ILlmDecisionValidator
{
    private readonly AiOptions _options = options.Value;

    // Runtime UI key/model takes precedence over the config/env values.
    private string? ResolveApiKey()
    {
        var runtime = settingsService.GetRuntimeSettings().AnthropicApiKey;
        return !string.IsNullOrWhiteSpace(runtime) ? runtime : _options.AnthropicApiKey;
    }

    private string ResolveModel()
    {
        var runtime = settingsService.GetRuntimeSettings().AiModel;
        return !string.IsNullOrWhiteSpace(runtime) ? runtime : _options.Model;
    }

    private const string SystemPrompt =
        "You are an institutional quantitative crypto trader specializing in BTCUSDT perpetuals. " +
        "Your objective is NOT to predict the future — it is to decide whether opening a position RIGHT NOW " +
        "has positive statistical expectancy. Think like a professional hedge fund trader. " +
        "You receive pre-computed category scores (0-100, where >50 is bullish), separate BUY/SELL/HOLD " +
        "confidences, the detected market regime, derivatives data, orderbook liquidity, and news/social sentiment. " +
        "Some categories (on-chain, macro) have no live data source and are scored neutral (50) — never invent values for them; " +
        "treat them as unknown. You do NOT decide whether to trade and you CANNOT reject or veto it — the system " +
        "already opens this position because it cleared the confidence threshold. Your ONLY job is to size the risk. " +
        "confirmed is ONLY a backdrop label: true = clean setup, false = conflicted/marginal (funding extreme, thin " +
        "liquidity, higher-timeframe disagrees, weak conviction). It does NOT change whether the trade happens and does " +
        "NOT by itself change the size. Never use words like reject/veto/skip/avoid in the narrative; frame everything " +
        "as how large and how leveraged the entry should be. Give an adjusted confidence (0-100) for the side. " +
        "ALWAYS return the execution params for the proposed side, and they are ALWAYS applied: size_multiplier " +
        "(0.1-1.5 of the baseline size — use ~1.0 for a clean setup, go below 1.0 the more hesitant you are, and only " +
        "above 1.0 for an exceptionally strong setup), leverage (integer 1-10), and stop_loss / take_profit (absolute " +
        "prices on the correct side of entry, keeping risk/reward sound). Whatever size_multiplier and leverage you " +
        "state in the narrative MUST equal the JSON fields, since the system executes exactly those. " +
        "Respond ONLY with minified JSON: " +
        "{\"confirmed\":bool,\"adjusted_confidence\":number,\"size_multiplier\":number,\"leverage\":number," +
        "\"stop_loss\":number,\"take_profit\":number,\"narrative\":string,\"risks\":[string]}";

    public async Task<LlmValidation> ValidateAsync(
        AdvancedDecision decision, AdvancedDecisionInput input, CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return Passthrough(decision, "LLM disabled (no API key)");

        try
        {
            var client = new AnthropicClient { ApiKey = apiKey };
            var payload = BuildPayload(decision, input);
            var model = ResolveModel();

            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = model,
                MaxTokens = 1024,
                System = SystemPrompt,
                Messages = [new() { Role = Role.User, Content = payload }]
            });

            usageTracker.Record(model, (int)response.Usage.InputTokens, (int)response.Usage.OutputTokens);

            var text = response.Content
                .Select(b => b.Value)
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .FirstOrDefault() ?? "";

            return ParseResponse(text, decision);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Claude validation failed; falling back to rule-based confidence");
            return Passthrough(decision, "LLM error, used rule-based result");
        }
    }

    private static string BuildPayload(AdvancedDecision d, AdvancedDecisionInput input)
    {
        var scores = string.Join(", ", d.Scores.Select(kv => $"{kv.Key}={kv.Value:F0}"));
        var headlines = input.Sentiment.Headlines.Count > 0
            ? string.Join(" | ", input.Sentiment.Headlines.Take(5))
            : "none";
        var cautions = d.Cautions.Count > 0 ? string.Join(" | ", d.Cautions) : "none";

        return $$"""
        Symbol: {{d.Symbol}}
        Proposed action: {{d.Action}}
        Confidence — BUY: {{d.ConfidenceBuy:F0}}, SELL: {{d.ConfidenceSell:F0}}, HOLD: {{d.ConfidenceHold:F0}} (action-side conviction: {{d.Confidence:F0}})
        Market regime: {{d.Regime}}
        Entry: {{d.EntryPrice}}, StopLoss: {{d.StopLoss}}, TakeProfit: {{d.TakeProfit}}, RiskReward: {{d.RiskReward:F2}}
        Baseline size (qty): {{d.PositionSizeQuantity}}, baseline leverage: {{d.Leverage}}x (size_multiplier scales this qty)
        Category scores (0-100, >50 bullish): {{scores}}
        Funding rate: {{input.Derivatives.FundingRate * 100:F4}}%
        Open interest change: {{input.Derivatives.OpenInterestChangePercent:F2}}%
        Long/short ratio: {{input.Derivatives.LongShortRatio:F2}}
        Order book imbalance: {{input.Derivatives.OrderBookImbalance:F3}}
        Fear & Greed: {{input.Sentiment.FearGreedIndex}} ({{input.Sentiment.FearGreedLabel}})
        Recent headlines: {{headlines}}
        Quality cautions (do not block the trade — use them to size defensively): {{cautions}}

        Validate this signal. Respond with the JSON schema only.
        """;
    }

    private LlmValidation ParseResponse(string text, AdvancedDecision decision)
    {
        try
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return Passthrough(decision, "LLM returned no JSON");
            var json = text.Substring(start, end - start + 1);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var confirmed = root.TryGetProperty("confirmed", out var c) && c.GetBoolean();
            var adjusted = root.TryGetProperty("adjusted_confidence", out var ac) ? ac.GetDecimal() : decision.Confidence;
            var narrative = root.TryGetProperty("narrative", out var n) ? n.GetString() ?? "" : "";
            var risks = root.TryGetProperty("risks", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : new List<string>();

            // Optional defensive execution overrides (applied only when not confirmed, within caps downstream).
            var sizeMultiplier = root.TryGetProperty("size_multiplier", out var sm) && sm.ValueKind == JsonValueKind.Number
                ? sm.GetDecimal() : 1m;
            int? leverage = root.TryGetProperty("leverage", out var lv) && lv.ValueKind == JsonValueKind.Number
                ? (int)Math.Round(lv.GetDecimal()) : null;
            decimal? stopLoss = root.TryGetProperty("stop_loss", out var sl) && sl.ValueKind == JsonValueKind.Number
                ? sl.GetDecimal() : null;
            decimal? takeProfit = root.TryGetProperty("take_profit", out var tp) && tp.ValueKind == JsonValueKind.Number
                ? tp.GetDecimal() : null;

            return new LlmValidation(
                confirmed, Math.Clamp(adjusted, 0m, 100m), narrative, risks, true,
                sizeMultiplier, leverage, stopLoss, takeProfit);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to parse LLM JSON: {Text}", text);
            return Passthrough(decision, "LLM parse error");
        }
    }

    private static LlmValidation Passthrough(AdvancedDecision d, string note)
        => new(true, d.Confidence, note, Array.Empty<string>(), false);
}
