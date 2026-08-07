using CryptoHft.Application.Trading;
using CryptoHft.Domain.Enums;
using CryptoHft.Infrastructure.Trading;
using Xunit;

namespace CryptoHft.Tests;

// A run of refusals with the counts attached is the difference between "the market gave
// nothing" and "the auditor is too strict" — those need different responses, and without a
// record the owner only sees that nothing opened. The window is one position's worth:
// refusals accumulate, stay readable while the position they preceded is open, and clear
// when it closes.
public sealed class AiAuditLogStoreTests
{
    private static AiAuditRefusal Refusal(string narrative = "no reversal bar yet at 64,320", int aligned = 3)
        => new("BTCUSDT", TradeSide.Long, 57.2m, 48m, aligned, 2, narrative,
            "Claude did not confirm the long", DateTimeOffset.UtcNow);

    [Fact]
    public void EmptyBeforeAnythingIsRecorded()
    {
        var snapshot = new AiAuditLogStore().Get("BTCUSDT");

        Assert.Empty(snapshot.Refusals);
        Assert.Null(snapshot.SinceAt);
    }

    [Fact]
    public void RefusalsAccumulateInOrder()
    {
        var store = new AiAuditLogStore();
        store.Record(Refusal("first"));
        store.Record(Refusal("second"));

        var snapshot = store.Get("BTCUSDT");

        Assert.Equal(2, snapshot.Refusals.Count);
        Assert.Equal("first", snapshot.Refusals[0].Narrative);
        Assert.Equal("second", snapshot.Refusals[1].Narrative);
        Assert.NotNull(snapshot.SinceAt);
    }

    // Claude's own tallies travel with the record: a refusal at 5 aligned reads very
    // differently from one at 1, and that distinction is the whole point of keeping the log.
    [Fact]
    public void TalliesAndConvictionSurvive()
    {
        var store = new AiAuditLogStore();
        store.Record(Refusal(aligned: 5));

        var only = store.Get("BTCUSDT").Refusals.Single();

        Assert.Equal(5, only.AlignedCount);
        Assert.Equal(2, only.BlockingCount);
        Assert.Equal(48m, only.AdjustedConfidence);
        Assert.Equal(57.2m, only.EngineConfidence);
    }

    [Fact]
    public void ClearingStartsTheNextRunFromEmpty()
    {
        var store = new AiAuditLogStore();
        store.Record(Refusal());
        store.Record(Refusal());

        store.Clear("BTCUSDT");

        Assert.Empty(store.Get("BTCUSDT").Refusals);
        Assert.Null(store.Get("BTCUSDT").SinceAt);
    }

    [Fact]
    public void SymbolsDoNotShareALog()
    {
        var store = new AiAuditLogStore();
        store.Record(Refusal());
        store.Record(new AiAuditRefusal("ETHUSDT", TradeSide.Short, 56m, 44m, 2, 3, "", "", DateTimeOffset.UtcNow));

        store.Clear("ETHUSDT");

        Assert.Single(store.Get("BTCUSDT").Refusals);
        Assert.Empty(store.Get("ETHUSDT").Refusals);
    }

    [Fact]
    public void LookupIsCaseInsensitive()
    {
        var store = new AiAuditLogStore();
        store.Record(Refusal());

        Assert.Single(store.Get("btcusdt").Refusals);
    }

    // An unattended process must not grow without bound; the newest refusals are the ones
    // worth keeping.
    [Fact]
    public void OldestEntriesAreDroppedOnceCapped()
    {
        var store = new AiAuditLogStore();
        for (var i = 0; i < 200; i++) store.Record(Refusal($"n{i}"));

        var refusals = store.Get("BTCUSDT").Refusals;

        Assert.True(refusals.Count <= 60, $"log grew to {refusals.Count}");
        Assert.Equal("n199", refusals[^1].Narrative);
        Assert.DoesNotContain(refusals, r => r.Narrative == "n0");
    }
}
