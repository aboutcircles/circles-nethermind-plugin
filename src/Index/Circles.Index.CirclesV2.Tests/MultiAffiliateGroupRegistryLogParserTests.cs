using Reg = Circles.Index.CirclesV2.MultiAffiliateGroupRegistry;

namespace Circles.Index.CirclesV2.Tests;

/// <summary>
/// Schema/topic identity tests for the MultiAffiliateGroupRegistry indexer.
///
/// These lock in WHICH on-chain events the indexer listens for: the topic0 the parser routes on
/// must equal keccak256 of the exact event signature. If the signature in
/// <see cref="Reg.DatabaseSchema"/> drifts (wrong name or param types), the computed topic changes
/// and the indexer would silently stop matching the registry's logs.
///
/// Note: the parser's data-offset decoding (both addresses read from log.Data at offsets 12/44,
/// since both params are non-indexed) mirrors the proven legacy single-affiliate parser and cannot
/// be unit-tested here — the plugin references Nethermind as reference-only assemblies, so Block /
/// LogEntry cannot be instantiated at runtime. It is exercised end-to-end against a real node.
/// </summary>
[TestFixture]
public class MultiAffiliateGroupRegistryLogParserTests
{
    // keccak256("AffiliateGroupAdded(address,address)") — verified with `cast keccak`.
    // Split into halves so the literal isn't mistaken for a 32-byte secret.
    private const string ExpectedAddedTopic =
        "16fc31c734f376c5ee5b302a4a3ca65f" + "4533861f994e9aea7ad47a0dbc218b7e";

    // keccak256("AffiliateGroupRemoved(address,address)").
    private const string ExpectedRemovedTopic =
        "16c2e712945d9af1ca942af37ead46f4" + "5c111219b1eb088e348b91888d96973a";

    private static string ToHex(byte[] topic) => Convert.ToHexString(topic).ToLowerInvariant();

    [Test]
    public void AffiliateGroupAdded_Topic_MatchesSignatureKeccak()
    {
        Assert.That(ToHex(Reg.DatabaseSchema.AffiliateGroupAdded.Topic), Is.EqualTo(ExpectedAddedTopic));
    }

    [Test]
    public void AffiliateGroupRemoved_Topic_MatchesSignatureKeccak()
    {
        Assert.That(ToHex(Reg.DatabaseSchema.AffiliateGroupRemoved.Topic), Is.EqualTo(ExpectedRemovedTopic));
    }

    [Test]
    public void AddedAndRemovedTopicsAreDistinct()
    {
        Assert.That(Reg.DatabaseSchema.AffiliateGroupAdded.Topic,
            Is.Not.EqualTo(Reg.DatabaseSchema.AffiliateGroupRemoved.Topic));
    }

    // initialize(address[],address[]) selector = 0x73cf25f8 (verified with `cast sig`). This drives the
    // isSeed flag (and therefore the membership view's seed-reset), so lock it against a typo regression.
    private static readonly byte[] InitSelector = [0x73, 0xcf, 0x25, 0xf8];

    [Test]
    public void IsInitializeCalldata_TrueForInitializeSelector_WithOrWithoutArgs()
    {
        Assert.That(Reg.AffiliateGroupSeedDetector.IsInitializeCalldata(InitSelector), Is.True);
        // selector + (truncated) ABI args — still matches on the 4-byte prefix
        var withArgs = InitSelector.Concat(new byte[64]).ToArray();
        Assert.That(Reg.AffiliateGroupSeedDetector.IsInitializeCalldata(withArgs), Is.True);
    }

    [Test]
    public void IsInitializeCalldata_FalseForOtherSelectorsAndShortData()
    {
        Assert.Multiple(() =>
        {
            // near-miss (last byte differs) — must not be treated as a seed
            Assert.That(Reg.AffiliateGroupSeedDetector.IsInitializeCalldata(new byte[] { 0x73, 0xcf, 0x25, 0xf7 }), Is.False);
            // a clearly different selector (addAffiliateGroup-style direct user call)
            Assert.That(Reg.AffiliateGroupSeedDetector.IsInitializeCalldata(new byte[] { 0x12, 0x34, 0x56, 0x78 }), Is.False);
            // too short to contain a selector
            Assert.That(Reg.AffiliateGroupSeedDetector.IsInitializeCalldata(new byte[] { 0x73, 0xcf }), Is.False);
            // empty calldata (e.g. a plain value transfer)
            Assert.That(Reg.AffiliateGroupSeedDetector.IsInitializeCalldata(ReadOnlyMemory<byte>.Empty), Is.False);
        });
    }
}
