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
    IAdaptiveWeightService adaptiveWeights,
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
        "You are the institutional RISK REVIEWER on a crypto trading desk, reviewing a BTCUSDT perpetual entry that " +
        "the automated system has ALREADY decided to open (it cleared the confidence threshold). You cannot reject or " +
        "veto the trade — your sole mandate is to size its risk so weak setups lose little and exceptional setups earn more. " +
        "You are a skeptic, not a cheerleader: your default posture is to look for reasons the trade FAILS. " +
        "Before sizing, check every failure mode against the data: crowded positioning (funding extreme, one-sided " +
        "long/short ratio), open-interest divergence against the direction, thin or imbalanced order book / wide spread, " +
        "higher-timeframe disagreement, trend exhaustion or volatility regime mismatch (chasing an extended move, or " +
        "trading a dead market), event/news risk in the headlines, and category scores that contradict the side. " +
        "Then judge confluence: count how many INDEPENDENT categories (technical, structure, orderbook, derivatives, " +
        "news, sentiment, liquidity, volatility) genuinely support the direction — a score within 45-55 is neutral and " +
        "supports nothing. Categories flagged as having no data source (on-chain, macro at neutral 50) are UNKNOWN: " +
        "never invent values or narratives for them and never count them as confluence. Use only the numbers provided; " +
        "do not fabricate prices, flows, or news. " +
        "SIZING POLICY (the system executes your numbers literally): size_multiplier scales the baseline qty, valid 0.1-1.5. " +
        "Strong confluence (>=4 independent categories aligned, no major failure factor): 0.9-1.1. " +
        "Mixed or noisy (2-3 aligned, or one significant failure factor): 0.4-0.8. " +
        "Weak or conflicted (<=1 aligned, several failure factors, or live event risk): 0.1-0.3. " +
        "Above 1.0 is EXCEPTIONAL and rare — only when nearly all categories align, funding is healthy, the higher " +
        "timeframe agrees, and there is no event risk; if in doubt stay at or below 1.0. " +
        "leverage: integer 1-20, stay at or below the baseline leverage unless the setup is exceptional; use 1-3 when hesitant. " +
        "stop_loss / take_profit: absolute prices on the correct side of entry. The system enforces a minimum " +
        "reward:risk of 2.0 measured from entry — any pair below that is discarded and the baseline is kept, so keep " +
        "the reward at least twice the risk. Place the stop beyond the level that invalidates the setup, not at an " +
        "arbitrary distance. " +
        "confirmed is ONLY a backdrop label: true = clean setup, false = conflicted/marginal. It does NOT change whether " +
        "the trade happens and does NOT by itself change the size. Never use words like reject/veto/skip/avoid in the " +
        "narrative; express caution purely through size and leverage. The size_multiplier and leverage you state in the " +
        "narrative MUST equal the JSON fields, since the system executes exactly those. " +
        "adjusted_confidence: your honest 0-100 conviction for the proposed side AFTER the failure-mode review — it may " +
        "be well below the system's number. narrative: 2-4 decision-grade sentences, no filler. risks: up to 5 short, " +
        "concrete failure modes, most important first. " +
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
            // Give Claude memory: realized results of past trades and of its own past verdicts.
            // Null until the first trades close, so early behavior is identical (fail-safe).
            LearningSnapshot? learning = null;
            try { learning = await adaptiveWeights.GetLearningSnapshotAsync(decision.Regime, cancellationToken); }
            catch (Exception ex) { logger.LogDebug(ex, "learning snapshot unavailable"); }

            var client = new AnthropicClient { ApiKey = apiKey };
            var payload = BuildPayload(decision, input, learning);
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

    private static string BuildPayload(AdvancedDecision d, AdvancedDecisionInput input, LearningSnapshot? learning)
    {
        var scores = string.Join(", ", d.Scores.Select(kv => $"{kv.Key}={kv.Value:F0}"));
        var headlines = input.Sentiment.Headlines.Count > 0
            ? string.Join(" | ", input.Sentiment.Headlines.Take(5))
            : "none";
        var cautions = d.Cautions.Count > 0 ? string.Join(" | ", d.Cautions) : "none";
        var newsReasons = input.Sentiment.Reasons.Count > 0
            ? string.Join(" | ", input.Sentiment.Reasons.Take(5))
            : "none";
        var spreadPct = d.EntryPrice > 0 ? input.Derivatives.BidAskSpread / d.EntryPrice * 100m : 0m;
        var volumeProfile = d.VolumeProfileNote.Length > 0
            ? $"\nVolume profile (1h, ~10d): {d.VolumeProfileNote}"
            : "";

        return $$"""
        Symbol: {{d.Symbol}}
        Proposed action: {{d.Action}}
        Confidence — BUY: {{d.ConfidenceBuy:F0}}, SELL: {{d.ConfidenceSell:F0}}, HOLD: {{d.ConfidenceHold:F0}} (action-side conviction: {{d.Confidence:F0}})
        Market regime: {{d.Regime}}
        Entry: {{d.EntryPrice}}, StopLoss: {{d.StopLoss}}, TakeProfit: {{d.TakeProfit}}, RiskReward: {{d.RiskReward:F2}}
        Baseline size (qty): {{d.PositionSizeQuantity}}, baseline leverage: {{d.Leverage}}x (size_multiplier scales this qty)
        Category scores (0-100, >50 bullish): {{scores}}
        Funding rate: {{input.Derivatives.FundingRate * 100:F4}}% (per 8h; beyond ±0.05% is stretched, beyond ±0.10% is crowded), cumulative 24h: {{input.Derivatives.CumulativeFunding24h * 100:F4}}%
        Open interest change: {{input.Derivatives.OpenInterestChangePercent:F2}}%
        Forced liquidations (last 5 min): longs ${{input.Derivatives.LongLiquidationNotional / 1_000_000m:F2}}M, shorts ${{input.Derivatives.ShortLiquidationNotional / 1_000_000m:F2}}M (one-sided flush = capitulation of that side)
        Long/short ratio: {{input.Derivatives.LongShortRatio:F2}}
        Taker buy/sell ratio: {{input.Derivatives.TakerBuySellRatio:F2}}
        Order book imbalance: {{input.Derivatives.OrderBookImbalance:F3}} (-1 ask-heavy … +1 bid-heavy)
        Bid/ask spread: {{spreadPct:F4}}% of price{{volumeProfile}}
        News sentiment: {{input.Sentiment.NewsScore:F0}}/100 ({{input.Sentiment.SentimentLabel}}), evidence confidence {{input.Sentiment.NewsConfidence:F0}}/100
        News score drivers: {{newsReasons}}
        Social sentiment: {{input.Sentiment.SocialScore:F0}}/100, Fear & Greed: {{input.Sentiment.FearGreedIndex}} ({{input.Sentiment.FearGreedLabel}})
        Recent headlines: {{headlines}}
        Quality cautions (do not block the trade — use them to size defensively): {{cautions}}
        {{BuildLearningBlock(learning)}}
        Run the failure-mode review, then size the trade. Respond with the JSON schema only.
        """;
    }

    // Realized-history digest: the desk's actual results and Claude's own track record.
    // Empty string when no realized data exists — the payload is then identical to before.
    private static string BuildLearningBlock(LearningSnapshot? s)
    {
        if (s is null) return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("REALIZED PERFORMANCE (closed live trades — weigh this evidence in your sizing):");
        if (s.RegimeTrades > 0)
        {
            sb.AppendLine(
                $"- This regime: {s.RegimeTrades} realized trades, win rate {s.RegimeWinRate:F0}%, " +
                $"exits {s.TakeProfitHits} take-profit vs {s.StopLossHits} stop-loss");
        }
        sb.AppendLine(
            $"- Learned baselines already applied to this proposal: SL {s.SlAtrMultiplier}xATR, " +
            $"TP {s.TpAtrMultiplier}xATR, leverage factor {s.LeverageFactor}x");
        foreach (var v in s.ValidationOutcomes.Where(v => v.Verdict is "confirmed" or "hesitant"))
        {
            sb.AppendLine(
                $"- Your past '{v.Verdict}' calls: {v.Samples} trades, win rate {v.WinRate:F0}%, " +
                $"avg ROI {v.AvgRoi * 100:F0}% — calibrate your sizing against this record");
        }
        return sb.ToString().TrimEnd();
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

            // Execution sizing fields — always applied downstream within hard caps, regardless of confirmed.
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
