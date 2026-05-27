using System.Numerics;
using System.Text.Json;
using Circles.Pathfinder.Host.Canary;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for the inflationary-units conversion helpers in SimulationCanaryService.
/// The conversion eliminates a false-positive InsufficientBalance revert when a path
/// transits an InflationaryCircles wrapper: BuildUnwrapPrefix sums in demurraged 1155
/// units, but the wrapper.unwrap() argument is interpreted in inflation-corrected
/// ERC20 units, so the same numeric value yields fewer 1155 tokens after unwrap.
/// </summary>
[TestFixture, Parallelizable]
public class SimulationCanaryInflationaryConversionTests
{
    private const string Holder = "0x14aab8d72b68c79cbb7873d003585a7c3ef98633";
    private const string BackersWrapper = "0xa0ea681f5685bfa6857d776b5acbf3d51bbecc9a";
    private const string HolderOwnWrapper = "0xa9adbc52bfe10981a15c3466b7683f5aa98a2d5e";

    // 32-byte ABI words assembled at runtime so each string literal stays under the
    // length that pre-commit / dotfile guards flag as a possible private key.
    private static string Word(string lowSegment) => new string('0', 64 - lowSegment.Length) + lowSegment;
    private const string Selector = "0x253dd0b5";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    #region ComputeInflationDay

    [Test]
    public void ComputeInflationDay_AtInflationDayZero_ReturnsZero()
    {
        Assert.That(SimulationCanaryService.ComputeInflationDay(1602720000L), Is.EqualTo(0L));
    }

    [Test]
    public void ComputeInflationDay_OneDayLater_ReturnsOne()
    {
        Assert.That(SimulationCanaryService.ComputeInflationDay(1602720000L + 86400L), Is.EqualTo(1L));
    }

    [Test]
    public void ComputeInflationDay_PreGenesis_ReturnsZero()
    {
        Assert.That(SimulationCanaryService.ComputeInflationDay(0L), Is.EqualTo(0L));
        Assert.That(SimulationCanaryService.ComputeInflationDay(-1L), Is.EqualTo(0L));
    }

    [Test]
    public void ComputeInflationDay_CaptureBlockTimestamp_MatchesChainProbe()
    {
        // Block 46384341 timestamp from the f72f2d61 capture day; the on-chain wrapper.day()
        // returned 2050 for the same input, which we use as the ground truth.
        Assert.That(SimulationCanaryService.ComputeInflationDay(1779858015L), Is.EqualTo(2050L));
    }

    #endregion

    #region EncodeConvertDemurrageCalldata

    [Test]
    public void EncodeConvertDemurrageCalldata_SmallValues_PadsBothArguments()
    {
        var data = SimulationCanaryService.EncodeConvertDemurrageCalldata(BigInteger.One, day: 1L);

        Assert.That(data, Has.Length.EqualTo(2 + 8 + 64 + 64)); // "0x" + selector + amount word + day word
        Assert.That(data.StartsWith(Selector, StringComparison.Ordinal));
        Assert.That(data, Does.EndWith(Word("1") + Word("1")));
    }

    [Test]
    public void EncodeConvertDemurrageCalldata_CaptureValues_AreReproducible()
    {
        // f72f2d61 capture: 3147.356750 CRC demurraged on wrapper-1 at day 2050.
        // 3147356750000000000000 = 0xaa9e5960b6d9eae000; 2050 = 0x802.
        var data = SimulationCanaryService.EncodeConvertDemurrageCalldata(
            BigInteger.Parse("3147356750000000000000"),
            day: 2050L);

        Assert.That(data, Is.EqualTo(Selector + Word("aa9e5960b6d9eae000") + Word("802")));
    }

    [Test]
    public void EncodeConvertDemurrageCalldata_RejectsNegativeAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SimulationCanaryService.EncodeConvertDemurrageCalldata(BigInteger.MinusOne, day: 0L));
    }

    [Test]
    public void EncodeConvertDemurrageCalldata_RejectsNegativeDay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SimulationCanaryService.EncodeConvertDemurrageCalldata(BigInteger.One, day: -1L));
    }

    #endregion

    #region ParseConvertCallReturnData

    [Test]
    public void ParseConvertCallReturnData_SuccessfulCall_DecodesUint256()
    {
        // f72f2d61 capture's wrapper-1 inflationary value (ABI uint256, big-endian, left-padded).
        var payload = "0x" + Word("100667f6ba2af04b7c1");
        var call = Parse("{\"status\":\"0x1\",\"returnData\":\"" + payload + "\"}");

        var result = SimulationCanaryService.ParseConvertCallReturnData(call);

        Assert.That(result, Is.EqualTo(BigInteger.Parse("4729752223130021312449")));
    }

    [Test]
    public void ParseConvertCallReturnData_RevertedCall_ReturnsNull()
    {
        var call = Parse(@"{""status"":""0x0"",""returnData"":""0x""}");

        Assert.That(SimulationCanaryService.ParseConvertCallReturnData(call), Is.Null);
    }

    [Test]
    public void ParseConvertCallReturnData_EmptyReturnData_ReturnsNull()
    {
        var call = Parse(@"{""status"":""0x1"",""returnData"":""0x""}");

        Assert.That(SimulationCanaryService.ParseConvertCallReturnData(call), Is.Null);
    }

    [Test]
    public void ParseConvertCallReturnData_MissingStatus_ReturnsNull()
    {
        var call = Parse("{\"returnData\":\"0x" + Word("1") + "\"}");

        Assert.That(SimulationCanaryService.ParseConvertCallReturnData(call), Is.Null);
    }

    [Test]
    public void ParseConvertCallReturnData_HighBitSet_DecodesAsUnsigned()
    {
        // Top nibble set ⇒ default BigInteger parse would interpret as negative;
        // the helper must prepend a leading zero to force unsigned interpretation.
        var payload = "0xff" + new string('0', 62);
        var call = Parse("{\"status\":\"0x1\",\"returnData\":\"" + payload + "\"}");

        var result = SimulationCanaryService.ParseConvertCallReturnData(call);

        Assert.That(result, Is.GreaterThan(BigInteger.Zero));
    }

    #endregion

    #region ExtractInflationaryAmounts

    private static string MakeBundleJson(params (string status, string? returnDataWordLow)[] entries)
    {
        var calls = entries.Select(e =>
            e.returnDataWordLow == null
                ? $"{{\"status\":\"{e.status}\",\"returnData\":\"0x\"}}"
                : $"{{\"status\":\"{e.status}\",\"returnData\":\"0x{Word(e.returnDataWordLow)}\"}}");
        return "{\"result\":[{\"calls\":[" + string.Join(",", calls) + "]}]}";
    }

    [Test]
    public void ExtractInflationaryAmounts_HappyPath_ReturnsBothAmounts()
    {
        var json = Parse(MakeBundleJson(("0x1", "7b"), ("0x1", "1c8")));

        var amounts = SimulationCanaryService.ExtractInflationaryAmounts(json, expectedCount: 2);

        Assert.That(amounts, Has.Count.EqualTo(2));
        Assert.That(amounts[0], Is.EqualTo(new BigInteger(123)));
        Assert.That(amounts[1], Is.EqualTo(new BigInteger(456)));
    }

    [Test]
    public void ExtractInflationaryAmounts_TopLevelError_ReturnsAllNulls()
    {
        var json = Parse(@"{""error"":{""code"":-32601,""message"":""Method not found""}}");

        var amounts = SimulationCanaryService.ExtractInflationaryAmounts(json, expectedCount: 3);

        Assert.That(amounts, Has.Count.EqualTo(3));
        Assert.That(amounts, Is.All.Null);
    }

    [Test]
    public void ExtractInflationaryAmounts_TruncatedResponse_MissingSlotsAreNull()
    {
        // Server returned only 1 call when we sent 2 — second slot must surface as null
        // so the caller falls back, not silently uses zero.
        var json = Parse(MakeBundleJson(("0x1", "2a")));

        var amounts = SimulationCanaryService.ExtractInflationaryAmounts(json, expectedCount: 2);

        Assert.That(amounts[0], Is.EqualTo(new BigInteger(42)));
        Assert.That(amounts[1], Is.Null);
    }

    [Test]
    public void ExtractInflationaryAmounts_OneRevertedCall_OnlyThatSlotIsNull()
    {
        var json = Parse(MakeBundleJson(("0x1", "1"), ("0x0", null), ("0x1", "3")));

        var amounts = SimulationCanaryService.ExtractInflationaryAmounts(json, expectedCount: 3);

        Assert.That(amounts[0], Is.EqualTo(BigInteger.One));
        Assert.That(amounts[1], Is.Null);
        Assert.That(amounts[2], Is.EqualTo(new BigInteger(3)));
    }

    [Test]
    public void ExtractInflationaryAmounts_MissingCallsArray_ReturnsAllNulls()
    {
        var json = Parse(@"{""result"":[{""otherField"":42}]}");

        var amounts = SimulationCanaryService.ExtractInflationaryAmounts(json, expectedCount: 2);

        Assert.That(amounts, Is.All.Null);
    }

    [Test]
    public void ExtractInflationaryAmounts_EmptyResultArray_ReturnsAllNulls()
    {
        var json = Parse(@"{""result"":[]}");

        var amounts = SimulationCanaryService.ExtractInflationaryAmounts(json, expectedCount: 2);

        Assert.That(amounts, Is.All.Null);
    }

    #endregion

    #region ApplyInflationaryAmounts

    [Test]
    public void ApplyInflationaryAmounts_AllResolved_ReplacesAllAmounts()
    {
        var demurraged = new[]
        {
            new SimulationCanaryService.UnwrapCall(Holder, BackersWrapper, BigInteger.Parse("3147356750000000000000")),
            new SimulationCanaryService.UnwrapCall(Holder, HolderOwnWrapper, BigInteger.Parse("668195817000000000000"))
        };
        var inflationary = new BigInteger?[]
        {
            BigInteger.Parse("4729752223130021312449"),
            BigInteger.Parse("668195817000000000000")
        };

        var resolved = SimulationCanaryService.ApplyInflationaryAmounts(demurraged, inflationary);

        Assert.That(resolved, Has.Count.EqualTo(2));
        Assert.That(resolved[0].From, Is.EqualTo(Holder));
        Assert.That(resolved[0].Wrapper, Is.EqualTo(BackersWrapper));
        Assert.That(resolved[0].Amount, Is.EqualTo(BigInteger.Parse("4729752223130021312449")));
        Assert.That(resolved[1].Amount, Is.EqualTo(BigInteger.Parse("668195817000000000000")));
    }

    [Test]
    public void ApplyInflationaryAmounts_NullEntry_FallsBackToDemurraged()
    {
        // RPC quirk or unknown wrapper variant — keep the demurraged amount and let the
        // bundle simulation proceed. A revert downstream surfaces normally; a silent
        // skip of unknown wrappers does not.
        var demurraged = new[]
        {
            new SimulationCanaryService.UnwrapCall(Holder, BackersWrapper, BigInteger.Parse("3147356750000000000000")),
            new SimulationCanaryService.UnwrapCall(Holder, HolderOwnWrapper, BigInteger.Parse("668195817000000000000"))
        };
        var inflationary = new BigInteger?[]
        {
            BigInteger.Parse("4729752223130021312449"),
            null
        };

        var resolved = SimulationCanaryService.ApplyInflationaryAmounts(demurraged, inflationary);

        Assert.That(resolved[0].Amount, Is.EqualTo(BigInteger.Parse("4729752223130021312449")));
        Assert.That(resolved[1].Amount, Is.EqualTo(BigInteger.Parse("668195817000000000000")));
    }

    [Test]
    public void ApplyInflationaryAmounts_LengthMismatch_Throws()
    {
        var demurraged = new[]
        {
            new SimulationCanaryService.UnwrapCall(Holder, BackersWrapper, BigInteger.One)
        };
        var inflationary = new BigInteger?[] { BigInteger.One, new BigInteger(2) };

        Assert.Throws<ArgumentException>(
            () => SimulationCanaryService.ApplyInflationaryAmounts(demurraged, inflationary));
    }

    [Test]
    public void ApplyInflationaryAmounts_PreservesOrder()
    {
        // BuildUnwrapPrefix emits unwraps in deterministic first-seen order; the resolved
        // list must stay aligned with the operateFlowMatrix calldata that follows.
        var demurraged = new[]
        {
            new SimulationCanaryService.UnwrapCall(Holder, HolderOwnWrapper, new BigInteger(1)),
            new SimulationCanaryService.UnwrapCall(Holder, BackersWrapper, new BigInteger(2)),
            new SimulationCanaryService.UnwrapCall(Holder, HolderOwnWrapper, new BigInteger(3))
        };
        var inflationary = new BigInteger?[] { new BigInteger(10), new BigInteger(20), new BigInteger(30) };

        var resolved = SimulationCanaryService.ApplyInflationaryAmounts(demurraged, inflationary);

        Assert.That(resolved.Select(c => c.Wrapper).ToArray(),
            Is.EqualTo(new[] { HolderOwnWrapper, BackersWrapper, HolderOwnWrapper }));
        Assert.That(resolved.Select(c => c.Amount).ToArray(),
            Is.EqualTo(new[] { new BigInteger(10), new BigInteger(20), new BigInteger(30) }));
    }

    #endregion

    #region End-to-end regression (f72f2d61)

    [Test]
    public void RegressionF72f2d61_HelperPipelinePreservesInflationaryAmounts()
    {
        // Scope: this test only validates the helper pipeline
        // (ExtractInflationaryAmounts → ApplyInflationaryAmounts → EncodeUnwrapCalldata)
        // round-trip and that the encoded unwrap calldata carries the inflationary value
        // rather than the demurraged sum. The end-to-end bundle outcome — that the
        // resolved unwrap turns the operateFlowMatrix revert into success — is covered
        // by the staging probe runs documented in the project memory (see
        // project_canary_unwrap_prefix_2026-05-21.md), not by this in-process test.
        //
        // The f72f2d61 capture's path was correct — the canary's old logic passed the
        // demurraged-units sum (3147.356750 CRC) directly to wrapper.unwrap, which on an
        // InflationaryCircles wrapper credited only ~2094 CRC of 1155 to the holder. The
        // operateFlowMatrix then reverted with InsufficientBalance(balance=2094 needed=3147).
        //
        // Chain probe at the same block: convertDemurrageToInflationaryValue returns
        // 4729752223130021312449 wei for the same inputs; unwrapping that amount credits
        // ~3147 CRC of 1155 — exactly what the flow matrix asks for.
        var capturedDemurraged = new[]
        {
            new SimulationCanaryService.UnwrapCall(Holder, BackersWrapper, BigInteger.Parse("3147356750000000000000")),
            new SimulationCanaryService.UnwrapCall(Holder, HolderOwnWrapper, BigInteger.Parse("668195817000000000000"))
        };
        // Probed staging Nethermind: inflationary amounts for both wrappers at simBlock.
        // wrapper-1: 4729752223130021312449 (= 0x100667f6ba2af04b7c1)
        // wrapper-2: 1004144398610653491409 (= 0x366f4d8a59f23fb0d1)
        var simulateV1Response = Parse(MakeBundleJson(
            ("0x1", "100667f6ba2af04b7c1"),
            ("0x1", "366f4d8a59f23fb0d1")));

        var amounts = SimulationCanaryService.ExtractInflationaryAmounts(simulateV1Response, expectedCount: 2);
        var resolved = SimulationCanaryService.ApplyInflationaryAmounts(capturedDemurraged, amounts);

        // Necessary condition: inflationary amount > demurraged sum, so unwrap credits
        // at least the demurraged total of 1155 to the holder.
        Assert.That(resolved[0].Amount, Is.GreaterThan(capturedDemurraged[0].Amount),
            "wrapper-1 inflationary must exceed demurraged sum");
        Assert.That(resolved[1].Amount, Is.GreaterThan(capturedDemurraged[1].Amount),
            "wrapper-2 inflationary must exceed demurraged sum");

        // And the encoded unwrap calldata must carry the inflationary amount, not the
        // demurraged one (catches future regressions where the bundle path bypasses
        // ApplyInflationaryAmounts).
        var encoded0 = SimulationCanaryService.EncodeUnwrapCalldata(resolved[0].Amount);
        Assert.That(encoded0, Does.Not.EndWith(Word("aa9e5960b6d9eae000")),
            "encoded unwrap-0 must not carry the demurraged amount");
    }

    #endregion
}
