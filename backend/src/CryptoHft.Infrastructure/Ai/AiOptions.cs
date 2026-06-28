namespace CryptoHft.Infrastructure.Ai;

public sealed class AiOptions
{
    // Anthropic API key. If empty, the LLM validation layer is skipped (rule engine only).
    public string? AnthropicApiKey { get; init; }

    // Model used for hybrid validation. Defaults to the latest Opus.
    public string Model { get; init; } = "claude-opus-4-8";

    // Only invoke the LLM when rule-based confidence is at/above this threshold,
    // to control cost (the LLM is a confirmation layer, not the hot path).
    public decimal LlmConfidenceThreshold { get; init; } = 80m;
}
