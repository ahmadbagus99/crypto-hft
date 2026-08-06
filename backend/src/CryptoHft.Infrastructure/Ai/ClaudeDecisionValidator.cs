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

    internal const string SystemPrompt =
        "You are the institutional RISK REVIEWER on a crypto trading desk, reviewing a BTCUSDT perpetual entry that " +
        "the automated system has ALREADY decided to open (it cleared the confidence threshold). You cannot reject or " +
        "veto the trade — your sole mandate is to size its risk so weak setups lose little and exceptional setups earn more. " +
        "Weigh the evidence in BOTH directions and let the counts below decide the size. Do not lean defensive by " +
        "reflex and do not lean aggressive by reflex: a setup whose evidence genuinely stacks up must be sized UP, " +
        "or this review adds nothing. Work the three steps in order. " +
        "STEP 1 — COUNT ALIGNMENT. Of the eight independent categories (technical, structure, orderbook, derivatives, " +
        "news, sentiment, liquidity, volatility), count how many genuinely support the proposed direction. A score " +
        "within 45-55 is neutral and counts for nothing. Categories flagged as having no data source (on-chain, macro " +
        "at neutral 50) are UNKNOWN: never invent values or narratives for them and never count them. Report the tally " +
        "as aligned_count. " +
        "STEP 2 — COUNT BLOCKING FACTORS. A factor counts ONLY if it crosses the stated threshold. A mild or " +
        "borderline reading is NOT a blocking factor and must not be counted: " +
        "(a) funding beyond ±0.05% per 8h against the side; " +
        "(b) long/short ratio above 1.5 already crowded on the side being taken; " +
        "(c) open interest falling more than 0.6% while price moves in the trade's direction; " +
        "(d) order book imbalance stronger than 0.35 against the side, or spread wider than 0.05% of price; " +
        "(e) a category scoring more than 15 points against the direction (below 35 for a long, above 65 for a short); " +
        "(f) a scheduled event or live breaking headline due within hours; " +
        "(g) entry chasing an already-extended move, or a volatility regime that contradicts the setup. " +
        "Report the tally as blocking_count. " +
        "STEP 3 — SIZE FROM THE COUNTS. Let net = aligned_count - blocking_count. The system executes your numbers " +
        "literally. size_multiplier scales the baseline qty and MUST land inside the band that net selects: " +
        "net>=6 -> 0.70-0.90 | net=5 -> 0.54-0.70 | net=4 -> 0.42-0.54 | net=3 -> 0.32-0.42 | " +
        "net=2 -> 0.24-0.32 | net=1 -> 0.16-0.24 | net<=0 -> 0.10-0.16. " +
        "Within the band go high when the higher timeframe agrees and the move is not extended, low otherwise. " +
        "Never emit a habitual round number: the multiplier must follow from the two counts you reported, and those " +
        "counts must follow from the data supplied. Overall valid range 0.1-1.5; go above 0.90 only when net>=7 with " +
        "no blocking factor at all, and never above 1.1. " +
        "leverage: integer 1-20, keep the baseline unless net<=0, in which case halve it (minimum 1). " +
        "stop_loss / take_profit: absolute prices on the correct side of entry. The system enforces a minimum " +
        "reward:risk of 2.0 measured from entry — any pair below that is discarded and the baseline is kept, so keep " +
        "the reward at least twice the risk. Place the stop beyond the level that invalidates the setup, not at an " +
        "arbitrary distance. Use only the numbers provided; do not fabricate prices, flows, or news. " +
        "confirmed is ONLY a backdrop label: true when net>=3, false otherwise. It does NOT change whether " +
        "the trade happens and does NOT by itself change the size. Never use words like reject/veto/skip/avoid in the " +
        "narrative; express caution purely through size and leverage. The size_multiplier and leverage you state in the " +
        "narrative MUST equal the JSON fields, since the system executes exactly those. " +
        "adjusted_confidence: your 0-100 conviction for the proposed side after Steps 1-2, anchored to net rather than " +
        "to the system's number — net>=5 is 70-85, net=3-4 is 58-70, net=1-2 is 48-58, net<=0 is 30-48. " +
        "narrative: 2-3 short sentences that state both counts, no filler. risks: up to 3 brief, concrete " +
        "failure modes, most important first — naming a risk does NOT by itself lower the size, only a factor that " +
        "crossed a Step 2 threshold does. " +
        "Respond ONLY with minified JSON: " +
        "{\"aligned_count\":number,\"blocking_count\":number,\"confirmed\":bool,\"adjusted_confidence\":number," +
        "\"size_multiplier\":number,\"leverage\":number,\"stop_loss\":number,\"take_profit\":number," +
        "\"narrative\":string,\"risks\":[string]}";

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
                // The three-step review returns two extra fields and a narrative that cites both
                // tallies, which pushed average output from 499 to 865 tokens. At 1024 a reply
                // that runs long is cut mid-JSON, parses to nothing, and the spend is total loss —
                // so the ceiling sits well clear of the longest reply rather than near it.
                MaxTokens = 2048,
                System = SystemPrompt,
                Messages = [new() { Role = Role.User, Content = payload }]
            });

            usageTracker.Record(model, (int)response.Usage.InputTokens, (int)response.Usage.OutputTokens);

            var text = response.Content
                .Select(b => b.Value)
                .OfType<TextBlock>()
                .Select(t => t.Text)
                .FirstOrDefault() ?? "";

            return ParseResponse(text, decision, logger);
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
        Work Steps 1-3: count alignment, count blocking factors, then size from net. Respond with the JSON schema only.
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

    internal static LlmValidation ParseResponse(string text, AdvancedDecision decision, ILogger? logger = null)
    {
        try
        {
            if (ExtractFirstJsonObject(text) is not string json)
            {
                // A reply cut off mid-JSON lands here. It is billed in full and yields nothing,
                // so it is a warning, not a debug note — at Debug the failure is invisible in
                // production and the only symptom is a silent "Claude unavailable" downstream.
                logger?.LogWarning(
                    "LLM reply carried no complete JSON object ({Length} chars, likely truncated). Raw: {Text}",
                    text.Length, Truncate(text));
                return Passthrough(decision, "LLM returned no JSON");
            }

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

            // The two tallies the sizing bands are derived from. Persisted so we can audit
            // afterwards whether the multiplier Claude sent actually follows its own counts.
            int? alignedCount = root.TryGetProperty("aligned_count", out var acnt) && acnt.ValueKind == JsonValueKind.Number
                ? (int)Math.Round(acnt.GetDecimal()) : null;
            int? blockingCount = root.TryGetProperty("blocking_count", out var bcnt) && bcnt.ValueKind == JsonValueKind.Number
                ? (int)Math.Round(bcnt.GetDecimal()) : null;

            return new LlmValidation(
                confirmed, Math.Clamp(adjusted, 0m, 100m), narrative, risks, true,
                sizeMultiplier, leverage, stopLoss, takeProfit, alignedCount, blockingCount);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse LLM JSON. Raw: {Text}", Truncate(text));
            return Passthrough(decision, "LLM parse error");
        }
    }

    // Returns the first complete JSON object in the reply, or null if none closes.
    //
    // Taking everything between the first '{' and the LAST '}' looked equivalent and was not:
    // production (2026-08-06 06:26) logged a well-formed object followed by a paragraph of
    // commentary, and because that prose contained a brace of its own the slice ran past the
    // object's real end. System.Text.Json then rejected the whole thing — "'W' is invalid
    // after a single JSON value" — and a perfectly good verdict was thrown away as an outage.
    // Matching braces by depth stops at the object's own close; quoted braces and escapes are
    // skipped so a '}' inside the narrative cannot end it early.
    internal static string? ExtractFirstJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }

            switch (ch)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    if (--depth == 0) return text[start..(i + 1)];
                    break;
            }
        }

        return null; // never closed — the reply was truncated
    }

    // Keeps a failed reply readable in the logs without flooding them.
    private static string Truncate(string text, int max = 600)
        => text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…[truncated]");

    private static LlmValidation Passthrough(AdvancedDecision d, string note)
        => new(true, d.Confidence, note, Array.Empty<string>(), false);
}
