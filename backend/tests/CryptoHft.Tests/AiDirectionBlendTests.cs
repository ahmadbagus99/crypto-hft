using CryptoHft.Infrastructure.Ai;
using Xunit;

namespace CryptoHft.Tests;

// Direction blending: with the setting on, Claude's conviction joins the directional score
// instead of only sizing the trade. Claude historically vetoed nearly everything when given
// gate rights, so its share is deliberately a minority of the vote.
public sealed class AiDirectionBlendTests
{
    private static decimal Blend(decimal engine, decimal claude, bool engineLong)
        => AiDecisionService.BlendDirection(engine, claude, engineLong);

    [Fact]
    public void EngineKeepsTheMajorityOfTheVote()
        => Assert.True(AiDecisionService.AiDirectionWeight < 0.5m,
            "a deterministic, auditable engine should outweigh a non-deterministic model");

    // Agreement pulls the score further in the direction the engine already leaned.
    [Fact]
    public void ClaudeAgreeing_StrengthensTheCall()
    {
        var blended = Blend(engine: 60m, claude: 90m, engineLong: true);

        Assert.True(blended > 60m);
        Assert.Equal(70.5m, blended); // 60*0.65 + 90*0.35
    }

    // Mild doubt pulls conviction down without changing the side — this is the common case.
    [Fact]
    public void ClaudeDoubting_WeakensWithoutFlipping()
    {
        var blended = Blend(engine: 62m, claude: 40m, engineLong: true);

        Assert.InRange(blended, 50m, 62m);
    }

    // Strong disagreement is what the mode exists for: it carries the score past neutral and
    // the side reverses.
    [Fact]
    public void ClaudeStronglyOpposed_ReversesTheSide()
    {
        var blended = Blend(engine: 58m, claude: 10m, engineLong: true);

        Assert.True(blended < 50m, $"blended {blended} should cross to the short side");
    }

    // Mirrored for a short: Claude's conviction is "for the proposed side", so a high number
    // on a short proposal must push the shared bullish scale DOWN.
    [Fact]
    public void ShortProposal_OrientsClaudeConvictionCorrectly()
    {
        var blended = Blend(engine: 40m, claude: 90m, engineLong: false);

        Assert.True(blended < 40m, "Claude backing a short should push the bullish score lower");
        Assert.Equal(29.5m, blended); // 40*0.65 + (100-90)*0.35
    }

    // Claude alone can never manufacture a trade out of a neutral engine read.
    [Fact]
    public void ClaudeCannotCreateConvictionFromNothing()
    {
        var maxFromNeutral = Blend(engine: 50m, claude: 100m, engineLong: true);

        Assert.Equal(67.5m, maxFromNeutral);
        // The engine only calls 50 "neutral"; a blend that starts there still needs Claude at
        // an extreme to reach a tradable band, and it can never exceed its 35% share.
        Assert.True(maxFromNeutral - 50m <= 100m * AiDecisionService.AiDirectionWeight);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    public void BlendStaysInRange(double engine, double claude)
    {
        var blended = Blend((decimal)engine, (decimal)claude, engineLong: true);
        Assert.InRange(blended, 0m, 100m);
    }
}
