using System.Numerics;
using Circles.Common.Dto;
using Circles.Pathfinder.Host.Canary;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for the eth_simulateV1 unwrap-prefix path in SimulationCanaryService.
/// Exercises BuildUnwrapPrefix and EncodeUnwrapCalldata only — the HTTP-driven
/// SimulateBundleAsync is covered by integration via the staging eth_simulateV1 probe.
/// </summary>
[TestFixture, Parallelizable]
public class SimulationCanaryUnwrapPrefixTests
{
    private const string Source = "0x742b7dbb0f1d330a83497df79075ebc778e6e698";
    private const string Sink = "0xd4cf9afd3ae777c24454b70dd28e32d1bd516f05";
    private const string V4Avatar = "0x837bf44405f7551f572f0256e55adb4a4e28157a";
    private const string V4Wrapper = "0x82f929cf40b063f08751fe9a1839eb002cf9320e";
    private const string V6Avatar = "0xc5377c93487953327902c349ed5bc75c306effcb";
    private const string V6Wrapper = "0xac2dc25d07e1194802eb45d8535f9e8c28d57907";

    private static IReadOnlyDictionary<string, string> WrapperMap() => new Dictionary<string, string>
    {
        [V4Wrapper] = V4Avatar,
        [V6Wrapper] = V6Avatar
    };

    private static TransferPathStep Step(string from, string to, string tokenOwner, string value) =>
        new() { From = from, To = to, TokenOwner = tokenOwner, Value = value };

    #region BuildUnwrapPrefix

    [Test]
    public void BuildUnwrapPrefix_NullMap_ReturnsEmpty()
    {
        var transfers = new List<TransferPathStep>
        {
            Step(Source, Sink, V4Wrapper, "1000000000000000000")
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, null);

        Assert.That(calls, Is.Empty);
    }

    [Test]
    public void BuildUnwrapPrefix_EmptyMap_ReturnsEmpty()
    {
        var transfers = new List<TransferPathStep>
        {
            Step(Source, Sink, V4Wrapper, "1000000000000000000")
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, new Dictionary<string, string>());

        Assert.That(calls, Is.Empty);
    }

    [Test]
    public void BuildUnwrapPrefix_NoWrapperTransfers_ReturnsEmpty()
    {
        // Both transfers carry native 1155 (TokenOwner = avatar, not wrapper).
        var transfers = new List<TransferPathStep>
        {
            Step(Source, Sink, V4Avatar, "1000000000000000000"),
            Step(Source, Sink, V6Avatar, "500000000000000000")
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, WrapperMap());

        Assert.That(calls, Is.Empty);
    }

    [Test]
    public void BuildUnwrapPrefix_SingleWrapperTransfer_EmitsOneCall()
    {
        var transfers = new List<TransferPathStep>
        {
            Step(Source, "0xaaa", V4Wrapper, "1000000000000000000")
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, WrapperMap());

        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].From, Is.EqualTo(Source));
        Assert.That(calls[0].Wrapper, Is.EqualTo(V4Wrapper));
        Assert.That(calls[0].Amount, Is.EqualTo(BigInteger.Parse("1000000000000000000")));
    }

    [Test]
    public void BuildUnwrapPrefix_SamePairAcrossTransfers_SumsAmounts()
    {
        // Two transfers from Source using the same wrapper but going to different recipients
        // → one unwrap call covering both legs.
        var transfers = new List<TransferPathStep>
        {
            Step(Source, "0xaaa", V4Wrapper, "1000000000000000000"),
            Step(Source, "0xbbb", V4Wrapper, "2500000000000000000")
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, WrapperMap());

        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].Amount, Is.EqualTo(BigInteger.Parse("3500000000000000000")));
    }

    [Test]
    public void BuildUnwrapPrefix_DifferentWrappers_EmitsCallPerWrapperInFirstSeenOrder()
    {
        var transfers = new List<TransferPathStep>
        {
            Step(Source, "0xaaa", V6Wrapper, "100"),
            Step(Source, "0xbbb", V4Wrapper, "200"),
            Step(Source, "0xccc", V6Wrapper, "50")
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, WrapperMap());

        Assert.That(calls, Has.Count.EqualTo(2));
        Assert.That(calls[0].Wrapper, Is.EqualTo(V6Wrapper), "V6 first-seen → first in bundle");
        Assert.That(calls[0].Amount, Is.EqualTo(new BigInteger(150)));
        Assert.That(calls[1].Wrapper, Is.EqualTo(V4Wrapper));
        Assert.That(calls[1].Amount, Is.EqualTo(new BigInteger(200)));
    }

    [Test]
    public void BuildUnwrapPrefix_MixedWrapperAndNative_OnlyWrapperEmitsCalls()
    {
        var transfers = new List<TransferPathStep>
        {
            Step(Source, "0xaaa", V4Avatar, "999"),   // native — ignored
            Step(Source, "0xbbb", V4Wrapper, "1000"), // wrapper — emit
            Step(Source, "0xccc", V6Avatar, "777")    // native — ignored
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, WrapperMap());

        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].Wrapper, Is.EqualTo(V4Wrapper));
        Assert.That(calls[0].Amount, Is.EqualTo(new BigInteger(1000)));
    }

    [Test]
    public void BuildUnwrapPrefix_ZeroOrNegativeValue_Skipped()
    {
        var transfers = new List<TransferPathStep>
        {
            Step(Source, "0xaaa", V4Wrapper, "0"),
            Step(Source, "0xbbb", V4Wrapper, "-50"),       // pathological — defensive skip
            Step(Source, "0xccc", V4Wrapper, "not-a-num"), // defensive parse skip
            Step(Source, "0xddd", V4Wrapper, "10")
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, WrapperMap());

        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].Amount, Is.EqualTo(new BigInteger(10)));
    }

    [Test]
    public void BuildUnwrapPrefix_TokenOwnerCaseInsensitive()
    {
        var transfers = new List<TransferPathStep>
        {
            Step(Source, "0xaaa", V4Wrapper.ToUpperInvariant(), "1000")
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, WrapperMap());

        Assert.That(calls, Has.Count.EqualTo(1));
        Assert.That(calls[0].Wrapper, Is.EqualTo(V4Wrapper),
            "Wrapper key normalised to lowercase before map lookup and emission");
    }

    [Test]
    public void BuildUnwrapPrefix_DifferentFromsForSameWrapper_KeptSeparate()
    {
        // If an intermediary (not source) ever held wrapped supply that the pathfinder
        // routed — currently filtered out by GraphFactory:884, but the prefix builder
        // must not silently merge holders if that ever changes.
        var otherHolder = "0xdeadbeef00000000000000000000000000000001";
        var transfers = new List<TransferPathStep>
        {
            Step(Source, "0xaaa", V4Wrapper, "1000"),
            Step(otherHolder, "0xbbb", V4Wrapper, "2000")
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, WrapperMap());

        Assert.That(calls, Has.Count.EqualTo(2),
            "Per-(from, wrapper) grouping must not collapse across distinct holders");
    }

    #endregion

    #region EncodeUnwrapCalldata

    [Test]
    public void EncodeUnwrapCalldata_Zero()
    {
        var data = SimulationCanaryService.EncodeUnwrapCalldata(BigInteger.Zero);

        Assert.That(data, Is.EqualTo("0xde0e9a3e" + new string('0', 64)));
    }

    [Test]
    public void EncodeUnwrapCalldata_One()
    {
        var data = SimulationCanaryService.EncodeUnwrapCalldata(BigInteger.One);

        Assert.That(data, Is.EqualTo("0xde0e9a3e" + new string('0', 63) + "1"));
    }

    [Test]
    public void EncodeUnwrapCalldata_OneEther()
    {
        var data = SimulationCanaryService.EncodeUnwrapCalldata(BigInteger.Parse("1000000000000000000"));

        // 1e18 == 0x0de0b6b3a7640000 → padded to 64 nibbles
        Assert.That(data, Is.EqualTo("0xde0e9a3e0000000000000000000000000000000000000000000000000de0b6b3a7640000"));
    }

    [Test]
    public void EncodeUnwrapCalldata_LargeAmountWithTopBitSet_NoLeadingSignByte()
    {
        // 0x8000000000000000_0000000000000000_0000000000000000_0000000000000000
        // BigInteger.ToString("x") would normally emit "8000...0" with leading 0 to
        // disambiguate sign. Verify we strip that so the encoded calldata is exactly
        // 32 bytes (64 hex chars) after the selector.
        var amount = BigInteger.Pow(2, 255);
        var data = SimulationCanaryService.EncodeUnwrapCalldata(amount);

        Assert.That(data.Length, Is.EqualTo(2 + 8 + 64), "0x + selector + 32-byte word");
        Assert.That(data, Does.StartWith("0xde0e9a3e8"),
            "Encoded amount must start with 8 (top nibble), no leading zero or sign byte");
    }

    [Test]
    public void EncodeUnwrapCalldata_MaxUint256_ExactlyOneWord()
    {
        // 2^256 - 1 — confirms padding logic handles the largest legal amount.
        var maxU256 = (BigInteger.One << 256) - 1;
        var data = SimulationCanaryService.EncodeUnwrapCalldata(maxU256);

        Assert.That(data, Is.EqualTo("0xde0e9a3e" + new string('f', 64)));
    }

    #endregion
}
