using CryptoHft.Infrastructure.Ai;
using Xunit;
using Headline = CryptoHft.Infrastructure.Ai.FreeSentimentProvider.Headline;

namespace CryptoHft.Tests;

public sealed class SentimentScoringTests
{
    private static Headline Fresh(string title) => new(title, DateTimeOffset.UtcNow.AddHours(-1));

    [Fact]
    public void ScoreHeadlines_Empty_ReturnsNeutralWithZeroConfidence()
    {
        var (score, label, confidence, _) = FreeSentimentProvider.ScoreHeadlines([]);
        Assert.Equal(50m, score);
        Assert.Equal("Neutral", label);
        Assert.Equal(0m, confidence);
    }

    [Fact]
    public void ScoreHeadlines_EtfOutflow_ScoresBearish_NotBullish()
    {
        // The old keyword scorer counted "etf" as bullish even in outflow headlines.
        var headlines = new[]
        {
            Fresh("Bitcoin ETF outflows hit $500M as investors retreat"),
            Fresh("Spot ETF outflows continue for third day, BTC under pressure"),
            Fresh("Crypto market sees heavy ETF outflows amid uncertainty")
        };
        var (score, label, _, reasons) = FreeSentimentProvider.ScoreHeadlines(headlines);
        Assert.True(score < 40m, $"expected bearish score, got {score}");
        Assert.Equal("Bearish", label);
        Assert.Contains(reasons, r => r.Contains("ETF outflows"));
    }

    [Fact]
    public void ScoreHeadlines_HackAndCollapse_ScoreStronglyBearish()
    {
        var headlines = new[]
        {
            Fresh("Major exchange hacked, $200M in bitcoin stolen"),
            Fresh("Crypto lender declares bankruptcy, halts withdrawals"),
            Fresh("Mass liquidations wipe out $1 billion in crypto longs")
        };
        var (score, label, confidence, _) = FreeSentimentProvider.ScoreHeadlines(headlines);
        Assert.True(score <= 25m, $"expected strongly bearish score, got {score}");
        Assert.Equal("Bearish", label);
        Assert.True(confidence >= 60m, $"unanimous high-impact evidence should be confident, got {confidence}");
    }

    [Fact]
    public void ScoreHeadlines_EtfInflowsAndApproval_ScoreBullish()
    {
        var headlines = new[]
        {
            Fresh("BlackRock bitcoin ETF sees record inflows of $800M"),
            Fresh("SEC approves new spot bitcoin ETF applications"),
            Fresh("Institutional buying of bitcoin accelerates, says report")
        };
        var (score, label, _, _) = FreeSentimentProvider.ScoreHeadlines(headlines);
        Assert.True(score >= 75m, $"expected strongly bullish score, got {score}");
        Assert.Equal("Bullish", label);
    }

    [Fact]
    public void ScoreHeadlines_SingleHeadline_IsDampened()
    {
        // One headline, however dramatic, may move the score at most halfway to the extreme.
        var headlines = new[] { Fresh("Major exchange hacked, bitcoin stolen") };
        var (score, _, _, _) = FreeSentimentProvider.ScoreHeadlines(headlines);
        Assert.True(score >= 25m, $"single headline should be dampened, got {score}");
        Assert.True(score < 50m, $"still directionally bearish, got {score}");
    }

    [Fact]
    public void ScoreHeadlines_SpeculativeQuestionHeadline_CarriesLessWeight()
    {
        var declarative = new[] { Fresh("Bitcoin crashes below key support") };
        var speculative = new[] { Fresh("Could bitcoin crash below key support?") };
        var (scoreDeclarative, _, _, _) = FreeSentimentProvider.ScoreHeadlines(declarative);
        var (scoreSpeculative, _, _, _) = FreeSentimentProvider.ScoreHeadlines(speculative);
        // Both single-headline dampened; direction survives but magnitude must not grow.
        Assert.True(scoreSpeculative <= 50m);
        Assert.True(scoreDeclarative <= scoreSpeculative,
            $"declarative ({scoreDeclarative}) should be at least as bearish as speculative ({scoreSpeculative})");
    }

    [Fact]
    public void ScoreHeadlines_MixedSignals_LowerConfidence()
    {
        var mixed = new[]
        {
            Fresh("Bitcoin ETF inflows surge to new record"),
            Fresh("Crypto exchange hacked, funds drained"),
            Fresh("Bitcoin rallies on institutional buying"),
            Fresh("Mass liquidation cascade hits bitcoin longs")
        };
        var unanimous = new[]
        {
            Fresh("Bitcoin ETF inflows surge to new record"),
            Fresh("SEC approves spot bitcoin ETF"),
            Fresh("Bitcoin rallies on institutional buying"),
            Fresh("Bitcoin breakout continues as inflows accelerate")
        };
        var (_, _, confMixed, _) = FreeSentimentProvider.ScoreHeadlines(mixed);
        var (_, _, confUnanimous, _) = FreeSentimentProvider.ScoreHeadlines(unanimous);
        Assert.True(confMixed < confUnanimous,
            $"conflicting evidence ({confMixed}) must be less confident than unanimous ({confUnanimous})");
    }

    [Theory]
    [InlineData("Bitcoin holds steady above $100K", true)]
    [InlineData("Fed signals rate cut in September", true)]
    [InlineData("SEC charges crypto exchange over unregistered securities", true)]
    [InlineData("Taylor Swift announces new stadium tour", false)]
    [InlineData("Local sports team wins championship game", false)]
    public void RelevanceFilter_MatchesOnlyCryptoAndMacroHeadlines(string title, bool expected)
    {
        Assert.Equal(expected, FreeSentimentProvider.Relevance.IsMatch(title));
    }
}
