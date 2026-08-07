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
        "at neutral 50) are UNKNOWN: never invent values or narratives for them and never count them. For structure, " +
        "judge the price-structure block (order blocks, fair value gaps, liquidity sweeps, BOS/CHoCH, Fibonacci " +
        "retracement, chart pattern, horizontal S/R) rather than the rolled-up number alone — those named levels are " +
        "the evidence, the number is only their average. Report the tally as aligned_count. " +
        "STEP 2 — COUNT BLOCKING FACTORS. A factor counts ONLY if it crosses the stated threshold. A mild or " +
        "borderline reading is NOT a blocking factor and must not be counted: " +
        "(a) funding beyond ±0.05% per 8h against the side; " +
        "(b) long/short ratio above 1.5 already crowded on the side being taken; " +
        "(c) open interest falling more than 0.6% while price moves in the trade's direction; " +
        "(d) order book imbalance stronger than 0.35 against the side, or spread wider than 0.05% of price; " +
        "(e) a category scoring more than 15 points against the direction (below 35 for a long, above 65 for a short); " +
        "(f) a scheduled event or live breaking headline due within hours; " +
        "(g) entry chasing an already-extended move, or a volatility regime that contradicts the setup; " +
        "(h) UNCONFIRMED LEVEL: price is reaching a level that would justify the trade (an unfilled FVG, a Fibonacci " +
        "retracement zone, a support/resistance band) but the candles show it still moving INTO that level rather than " +
        "turning at it — no reversal bar has closed. A level touched is a setup; a level rejected is a trigger, and " +
        "only the trigger earns size. Read this from the candle block, not from the scores. " +
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

    // Auditor mandate, used only when the owner turns "AI Ikut Menentukan Arah" on. The engine
    // stays the gate: Claude is never asked about a setup the engine did not already accept.
    // What changes is authority — here the verdict decides whether the position opens at all,
    // instead of only scaling it. Steps 1-3 are shared with the advisory prompt so the two
    // modes count the same evidence the same way and only the consequence differs.
    internal const string AuditorPrompt =
        "You are the AUDITOR on a crypto trading desk. An automated engine has proposed a BTCUSDT perpetual entry " +
        "and cleared its own confidence threshold. Your job is to check that proposal against the price evidence and " +
        "decide whether it opens. A refusal is not a rejection of the trade forever — the engine re-evaluates " +
        "continuously and will present the next opportunity, so declining costs one setup, not the strategy. " +
        "Judge only what the data shows. Do NOT deny by reflex: an engine read that the levels and candles support " +
        "must be confirmed, or this audit adds nothing and simply stops the desk from trading. Equally, do not " +
        "confirm out of politeness — an unconfirmed level is a real reason to wait. " +
        "STEP 1 — COUNT ALIGNMENT. Of the eight categories (technical, structure, orderbook, derivatives, news, " +
        "sentiment, liquidity, volatility), count how many genuinely support the proposed direction. A score within " +
        "45-55 is neutral and counts for nothing. Categories flagged as having no data source (on-chain, macro at " +
        "neutral 50) are UNKNOWN: never invent values for them and never count them. For structure, judge the " +
        "price-structure block (order blocks, fair value gaps, liquidity sweeps, BOS/CHoCH, Fibonacci retracement, " +
        "chart pattern, horizontal S/R) rather than the rolled-up number — those named levels are the evidence. " +
        "Report the tally as aligned_count. " +
        "STEP 2 — COUNT BLOCKING FACTORS, counting one only when it crosses the stated threshold; a mild reading is " +
        "not a blocking factor: (a) funding beyond ±0.05% per 8h against the side; (b) long/short ratio above 1.5 " +
        "already crowded on the side taken; (c) open interest falling more than 0.6% while price moves with the trade; " +
        "(d) order book imbalance stronger than 0.35 against the side, or spread wider than 0.05% of price; (e) a " +
        "category more than 15 points against the direction; (f) a scheduled event or breaking headline due within " +
        "hours; (g) entry chasing an already-extended move, or a volatility regime that contradicts the setup. " +
        "Report as blocking_count. These are reasons to doubt the DIRECTION. " +
        "STEP 2b — LEVEL CONFIRMATION, reported separately in level_confirmed because it is a question of TIMING, not " +
        "direction, and must not be counted in blocking_count. Set level_confirmed=false when price is reaching a " +
        "level that would justify the trade (an unfilled FVG, a Fibonacci retracement zone, a support/resistance " +
        "band) but the candles show it still moving INTO that level rather than turning at it — no reversal bar has " +
        "closed. Set it true when a bar has actually rejected the level: a close back through it, a rejection wick, " +
        "or a decisive turn in the last bars. Read this from the candle block, not from the scores. A level touched " +
        "is a setup; a level rejected is a trigger. " +
        "STEP 3 — VERDICT. Let net = aligned_count - blocking_count. Set confirmed=true when net >= 3, whatever " +
        "level_confirmed says. Otherwise confirmed=false and the trade is skipped; the system executes that " +
        "literally, false means no position is opened. An unconfirmed level does NOT block the trade — the system " +
        "halves the size for it on its own, so do not also withhold the verdict for it or the same caution is " +
        "charged twice. " +
        "Read the candle block bar by bar before answering: state in the narrative which specific bar and which " +
        "specific level you based the verdict on. A verdict that cites no bar and no price is not an audit. " +
        "size_multiplier still scales the baseline qty when you confirm, from the same band net selects: " +
        "net>=6 -> 0.70-0.90 | net=5 -> 0.54-0.70 | net=4 -> 0.42-0.54 | net=3 -> 0.32-0.42. When confirmed=false " +
        "the multiplier is ignored; send 0.1. " +
        "leverage: integer 1-20, keep the baseline. stop_loss / take_profit: absolute prices on the correct side of " +
        "entry; the system enforces a minimum reward:risk of 2.0 from entry, so keep reward at least twice risk, and " +
        "place the stop beyond the level that invalidates the setup. Use only the numbers provided; never fabricate " +
        "prices, flows, or news. " +
        "adjusted_confidence: 0-100 conviction for the proposed side, anchored to net — net>=5 is 70-85, net=3-4 is " +
        "58-70, net=1-2 is 48-58, net<=0 is 30-48. narrative: 2-3 sentences stating both counts, whether the level " +
        "was confirmed, the bar you read, and the level itself. risks: up to 3 brief concrete failure modes. " +
        "Respond ONLY with minified JSON: " +
        "{\"aligned_count\":number,\"blocking_count\":number,\"level_confirmed\":bool,\"confirmed\":bool," +
        "\"adjusted_confidence\":number,\"size_multiplier\":number,\"leverage\":number,\"stop_loss\":number," +
        "\"take_profit\":number,\"narrative\":string,\"risks\":[string]}";

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
            // Cached hourly by the service, so this costs nothing per call. Empty on failure,
            // which sends raw scores — the behaviour before centring existed.
            IReadOnlyDictionary<string, decimal> baselines = new Dictionary<string, decimal>();
            try { baselines = await adaptiveWeights.GetCategoryBaselinesAsync(cancellationToken); }
            catch (Exception ex) { logger.LogDebug(ex, "category baselines unavailable for payload"); }

            var payload = BuildPayload(decision, input, learning, baselines);
            var model = ResolveModel();

            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = model,
                // The three-step review returns two extra fields and a narrative that cites both
                // tallies, which pushed average output from 499 to 865 tokens. At 1024 a reply
                // that runs long is cut mid-JSON, parses to nothing, and the spend is total loss —
                // so the ceiling sits well clear of the longest reply rather than near it.
                MaxTokens = 2048,
                // Auditor mandate only when the owner granted it; otherwise the advisory
                // prompt, so an unchecked setting behaves exactly as it always did.
                System = settingsService.GetRuntimeSettings().AiDirectionEnabled ? AuditorPrompt : SystemPrompt,
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

    private static string BuildPayload(
        AdvancedDecision d,
        AdvancedDecisionInput input,
        LearningSnapshot? learning,
        IReadOnlyDictionary<string, decimal> baselines)
    {
        var scores = BuildScoreLine(d, baselines);
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
        {{BuildStructureBlock(d)}}
        {{BuildCandleBlock(input)}}
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

    // Each category alongside the centre of its own distribution, because since the engine
    // started centring inputs on their own habit the raw number and the vote have diverged.
    // Production 2026-08-07 10:17: sentiment read 29 and Claude counted it as a blocking
    // factor "more than 15 points against" — while the engine, knowing sentiment normally
    // sits at 28, had already treated it as neutral. News is the same story upward: 61 looks
    // mildly bullish next to 50 and is a strong reading next to its own 31.6. Judging both
    // sides of the desk on one ruler is the point; showing the raw value too keeps the
    // absolute level available where it genuinely matters.
    internal static string BuildScoreLine(AdvancedDecision d, IReadOnlyDictionary<string, decimal> baselines)
        => string.Join(", ", d.Scores.Select(kv =>
        {
            if (!baselines.TryGetValue(kv.Key, out var baseline)) return $"{kv.Key}={kv.Value:F0}";
            var centred = CategoryBaseline.Recenter(kv.Value, baseline);
            return Math.Abs(centred - kv.Value) < 0.05m
                ? $"{kv.Key}={kv.Value:F0}"
                : $"{kv.Key}={centred:F0} (raw {kv.Value:F0}, its own normal {baseline:F0})";
        }));

    // The level analyses the engine already runs — order blocks, fair value gaps, liquidity
    // sweeps, BOS/CHoCH, Fibonacci retracement, chart patterns, horizontal S/R. Until now all
    // of it was crushed into a single "structure" number before Claude saw anything, so it was
    // being asked to judge a setup it had no way to see. Each analysis already carries a terse
    // summary with the actual levels in it; this just stops throwing them away.
    private static readonly string[] StructureComponents =
        ["SmartMoney", "PriceAction", "Pattern", "Fibonacci", "SupportResistance"];

    internal static string BuildStructureBlock(AdvancedDecision d)
    {
        var parts = StructureComponents
            .Select(name => d.Components.FirstOrDefault(c => c.Name == name))
            .Where(c => c is not null && c!.Reason.Length > 0)
            .Select(c => $"- {c!.Name} ({c.Score:F0}): {c.Reason}")
            .ToList();

        return parts.Count == 0
            ? ""
            : "Price structure (0-100, >50 bullish — these are the levels behind the structure score):\n"
              + string.Join("\n", parts);
    }

    // Raw candles on the fastest timeframe available, so the confirmation question — did price
    // actually turn at the level, or is it still falling into it — can be answered from the
    // bars rather than inferred from a score. Six is enough to see a reversal without spending
    // the token budget on history the levels already summarise.
    private const int ConfirmationCandles = 6;

    internal static string BuildCandleBlock(AdvancedDecisionInput input)
    {
        var tf = input.Timeframes
            .Where(t => t.Candles.Count > 0)
            .OrderBy(t => IntervalMinutes(t.Interval))
            .FirstOrDefault();
        if (tf is null) return "";

        var recent = tf.Candles.TakeLast(ConfirmationCandles).ToList();
        var rows = recent.Select(c =>
            $"  {c.OpenTime:HH:mm} O {c.Open:F1} H {c.High:F1} L {c.Low:F1} C {c.Close:F1} V {c.Volume:F0}");

        return $"Last {recent.Count} closed {tf.Interval} candles (oldest first — use for reversal/confirmation):\n"
               + string.Join("\n", rows);
    }

    private static int IntervalMinutes(string interval) => interval switch
    {
        "1m" => 1, "3m" => 3, "5m" => 5, "15m" => 15, "30m" => 30,
        "1h" => 60, "2h" => 120, "4h" => 240, "1d" => 1440,
        _ => int.MaxValue
    };

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
            bool? levelConfirmed = root.TryGetProperty("level_confirmed", out var lc)
                && lc.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? lc.GetBoolean() : null;

            return new LlmValidation(
                confirmed, Math.Clamp(adjusted, 0m, 100m), narrative, risks, true,
                sizeMultiplier, leverage, stopLoss, takeProfit, alignedCount, blockingCount, levelConfirmed);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse LLM JSON. Raw: {Text}", Truncate(text));
            return Passthrough(decision, "LLM parse error");
        }
    }

    // Returns the verdict object from a reply that may be wrapped in prose on either side.
    //
    // Two production failures shaped this. On 2026-08-06 06:26 the object was followed by a
    // paragraph of commentary and slicing to the LAST '}' swallowed it. At 01:36 the next day
    // the model reasoned in prose FIRST — "Reading candles: price dipped to low 64276.8…" —
    // and taking the FIRST '{' locked onto a brace inside that reasoning, so parsing died on
    // "']' is invalid without a matching open". Neither end of the reply can be trusted to be
    // the object, so every '{' is tried in turn and the first balanced span that actually
    // parses AND carries a verdict field wins. Requiring the field matters: a stray brace in
    // prose can still yield technically-valid JSON that means nothing.
    private static readonly string[] VerdictKeys = ["confirmed", "size_multiplier", "adjusted_confidence"];

    internal static string? ExtractFirstJsonObject(string text)
    {
        for (var start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
        {
            if (BalancedSpan(text, start) is not string candidate) continue;
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && VerdictKeys.Any(k => doc.RootElement.TryGetProperty(k, out _)))
                    return candidate;
            }
            catch (JsonException)
            {
                // Not the verdict object — keep looking.
            }
        }
        return null;
    }

    // The span from an opening brace to its matching close, honouring string literals and
    // escapes so a brace inside the narrative cannot end the object early. Null when it never
    // closes, which is what a truncated reply looks like.
    private static string? BalancedSpan(string text, int start)
    {
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

        return null;
    }

    // Keeps a failed reply readable in the logs without flooding them.
    private static string Truncate(string text, int max = 600)
        => text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…[truncated]");

    private static LlmValidation Passthrough(AdvancedDecision d, string note)
        => new(true, d.Confidence, note, Array.Empty<string>(), false);
}
