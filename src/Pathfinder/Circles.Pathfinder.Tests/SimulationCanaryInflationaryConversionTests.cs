using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Host.Canary;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Tests for the canary's wrapper-type-discriminated unwrap conversion.
///
/// <para><b>Contract behavior (verified on staging 2026-05-27 via direct eth_simulateV1 probes):</b></para>
/// <list type="bullet">
///   <item>DemurrageCircles wrapper 0x548c20e6 (`gCRC`, circlesType=0):
///     <c>unwrap(229_412.19e18)</c> → minted 229_412.19e18 of 1155 (ratio 1.0). Argument is in demurraged units.</item>
///   <item>InflationaryCircles wrapper 0x5d7eaaed (`s-gCRC`, circlesType=1):
///     <c>unwrap(515.77e18)</c> → minted 343.21e18 of 1155 (ratio γ^2050 ≈ 0.6654). Argument is in inflationary units.</item>
/// </list>
/// <para>Both wrappers inherit <c>convertDemurrageToInflationaryValue</c> from the shared Demurrage
/// base, which always applies β^day regardless of wrapper flavor — so the canary cannot rely on
/// the function returning "identity" for demurraged wrappers. Branching is on circlesType set
/// membership, not on the conversion function output.</para>
/// </summary>
[TestFixture]
public class SimulationCanaryInflationaryConversionTests
{
    // --- Hex helpers: assemble 32-byte ABI words at runtime so no literal exceeds 32 hex chars.
    private static string Word(string lowSegment)
    {
        if (lowSegment.Length > 64)
            throw new ArgumentException("segment longer than 32 bytes");
        return new string('0', 64 - lowSegment.Length) + lowSegment;
    }

    // ──────────────────────────────────────────────────────────────────────
    // ComputeInflationDay
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public void ComputeInflationDay_BelowEpoch_ReturnsZero()
    {
        Assert.That(SimulationCanaryService.ComputeInflationDay(0), Is.EqualTo(0));
        Assert.That(SimulationCanaryService.ComputeInflationDay(1_000_000_000), Is.EqualTo(0));
        Assert.That(SimulationCanaryService.ComputeInflationDay(1_602_720_000), Is.EqualTo(0));
    }

    [Test]
    public void ComputeInflationDay_AtEpochPlusOneDay_ReturnsOne()
    {
        Assert.That(SimulationCanaryService.ComputeInflationDay(1_602_720_000 + 86_400), Is.EqualTo(1));
    }

    [Test]
    public void ComputeInflationDay_Day2050_MatchesStagingTimestamp()
    {
        // staging1 latest block timestamp range during 2026-05-27 ≈ 1779900000
        // Expected day = (1779900000 - 1602720000) / 86400 ≈ 2050
        long ts = 1_602_720_000L + 2050L * 86_400L + 500; // mid-day 2050
        Assert.That(SimulationCanaryService.ComputeInflationDay(ts), Is.EqualTo(2050));
    }

    // ──────────────────────────────────────────────────────────────────────
    // EncodeConvertDemurrageCalldata
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public void EncodeConvertDemurrageCalldata_ZeroAmountZeroDay()
    {
        var data = SimulationCanaryService.EncodeConvertDemurrageCalldata(BigInteger.Zero, 0);
        Assert.That(data, Is.EqualTo("0x253dd0b5" + Word("0") + Word("0")));
    }

    [Test]
    public void EncodeConvertDemurrageCalldata_OneEtherDayOne()
    {
        var data = SimulationCanaryService.EncodeConvertDemurrageCalldata(BigInteger.Parse("1000000000000000000"), 1);
        Assert.That(data, Is.EqualTo("0x253dd0b5" + Word("de0b6b3a7640000") + Word("1")));
    }

    [Test]
    public void EncodeConvertDemurrageCalldata_HighBitSetAmount_NoLeadingZeroDoublePadding()
    {
        // 2^248 ≤ value < 2^256 — top nibble ≥ 0x8 triggers BigInteger sign-disambiguation.
        // Encoder must trim that leading zero so left-pad math yields exactly 64 hex chars per word.
        var amount = BigInteger.Pow(2, 252);
        var data = SimulationCanaryService.EncodeConvertDemurrageCalldata(amount, 2050);
        // selector (10 chars) + amount word (64) + day word (64) = 138 chars total
        Assert.That(data.Length, Is.EqualTo(10 + 64 + 64));
        Assert.That(data.StartsWith("0x253dd0b5"), Is.True);
    }

    [Test]
    public void EncodeConvertDemurrageCalldata_NegativeAmount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SimulationCanaryService.EncodeConvertDemurrageCalldata(BigInteger.MinusOne, 0));
    }

    [Test]
    public void EncodeConvertDemurrageCalldata_NegativeDay_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SimulationCanaryService.EncodeConvertDemurrageCalldata(BigInteger.One, -1));
    }

    // ──────────────────────────────────────────────────────────────────────
    // ParseConvertCallReturnData
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public void ParseConvertCallReturnData_SuccessfulCall_ReturnsAmount()
    {
        var json = JsonDocument.Parse(
            "{\"status\":\"0x1\",\"returnData\":\"0x" + Word("de0b6b3a7640000") + "\"}").RootElement;
        var amt = SimulationCanaryService.ParseConvertCallReturnData(json);
        Assert.That(amt, Is.EqualTo(BigInteger.Parse("1000000000000000000")));
    }

    [Test]
    public void ParseConvertCallReturnData_HighBitSetReturn_TreatedAsUnsigned()
    {
        // Top byte 0xff — without the leading "0" prepend, BigInteger.Parse would read as negative.
        var lowSeg = new string('f', 64); // 32 bytes all-FF = 2^256 - 1
        var json = JsonDocument.Parse(
            "{\"status\":\"0x1\",\"returnData\":\"0x" + lowSeg + "\"}").RootElement;
        var amt = SimulationCanaryService.ParseConvertCallReturnData(json);
        Assert.That(amt, Is.GreaterThan(BigInteger.Zero));
        Assert.That(amt, Is.EqualTo(BigInteger.Pow(2, 256) - 1));
    }

    [Test]
    public void ParseConvertCallReturnData_FailedCall_ReturnsNull()
    {
        var json = JsonDocument.Parse(
            "{\"status\":\"0x0\",\"returnData\":\"0x" + Word("1") + "\"}").RootElement;
        Assert.That(SimulationCanaryService.ParseConvertCallReturnData(json), Is.Null);
    }

    [Test]
    public void ParseConvertCallReturnData_MissingStatus_ReturnsNull()
    {
        var json = JsonDocument.Parse("{\"returnData\":\"0x" + Word("1") + "\"}").RootElement;
        Assert.That(SimulationCanaryService.ParseConvertCallReturnData(json), Is.Null);
    }

    [Test]
    public void ParseConvertCallReturnData_EmptyReturnData_ReturnsNull()
    {
        var json = JsonDocument.Parse("{\"status\":\"0x1\",\"returnData\":\"0x\"}").RootElement;
        Assert.That(SimulationCanaryService.ParseConvertCallReturnData(json), Is.Null);
    }

    // ──────────────────────────────────────────────────────────────────────
    // ExtractInflationaryAmounts
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public void ExtractInflationaryAmounts_TwoSuccesses_ReturnsBoth()
    {
        var body = "{\"result\":[{\"calls\":[" +
            "{\"status\":\"0x1\",\"returnData\":\"0x" + Word("de0b6b3a7640000") + "\"}," +
            "{\"status\":\"0x1\",\"returnData\":\"0x" + Word("1bc16d674ec80000") + "\"}" + // 2 ether
            "]}]}";
        var amounts = SimulationCanaryService.ExtractInflationaryAmounts(JsonDocument.Parse(body).RootElement, 2);
        Assert.That(amounts.Count, Is.EqualTo(2));
        Assert.That(amounts[0], Is.EqualTo(BigInteger.Parse("1000000000000000000")));
        Assert.That(amounts[1], Is.EqualTo(BigInteger.Parse("2000000000000000000")));
    }

    [Test]
    public void ExtractInflationaryAmounts_ResponseTruncated_FillsNulls()
    {
        // Only one call returned but caller expected two — second slot must be null, not crash.
        var body = "{\"result\":[{\"calls\":[" +
            "{\"status\":\"0x1\",\"returnData\":\"0x" + Word("de0b6b3a7640000") + "\"}" +
            "]}]}";
        var amounts = SimulationCanaryService.ExtractInflationaryAmounts(JsonDocument.Parse(body).RootElement, 2);
        Assert.That(amounts.Count, Is.EqualTo(2));
        Assert.That(amounts[0], Is.Not.Null);
        Assert.That(amounts[1], Is.Null);
    }

    [Test]
    public void ExtractInflationaryAmounts_MissingResult_ReturnsAllNulls()
    {
        var amounts = SimulationCanaryService.ExtractInflationaryAmounts(JsonDocument.Parse("{}").RootElement, 3);
        Assert.That(amounts.Count, Is.EqualTo(3));
        foreach (var a in amounts) Assert.That(a, Is.Null);
    }

    // ──────────────────────────────────────────────────────────────────────
    // ApplyInflationaryAmounts — type-discrimination behavior
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public void ApplyInflationaryAmounts_DemurragedCalls_PassThroughUnchanged()
    {
        var calls = new[]
        {
            new SimulationCanaryService.DemurragedUnwrapCall("0xfrom1", "0xwrap1", BigInteger.Parse("100"), CirclesType.DemurrageCircles),
            new SimulationCanaryService.DemurragedUnwrapCall("0xfrom2", "0xwrap2", BigInteger.Parse("200"), CirclesType.DemurrageCircles),
        };
        var inflationaryResolved = new List<BigInteger?>(); // empty — no inflationary calls to resolve

        var result = SimulationCanaryService.ApplyInflationaryAmounts(calls, inflationaryResolved);
        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0].Amount, Is.EqualTo(BigInteger.Parse("100")));
        Assert.That(result[1].Amount, Is.EqualTo(BigInteger.Parse("200")));
    }

    [Test]
    public void ApplyInflationaryAmounts_InflationaryCalls_AmountReplaced()
    {
        var calls = new[]
        {
            new SimulationCanaryService.DemurragedUnwrapCall("0xfrom1", "0xwrap1", BigInteger.Parse("100"), CirclesType.InflationaryCircles),
            new SimulationCanaryService.DemurragedUnwrapCall("0xfrom2", "0xwrap2", BigInteger.Parse("200"), CirclesType.InflationaryCircles),
        };
        var inflationaryResolved = new List<BigInteger?>
        {
            BigInteger.Parse("150"), // = 100 * β^day (rounded)
            BigInteger.Parse("300"), // = 200 * β^day
        };

        var result = SimulationCanaryService.ApplyInflationaryAmounts(calls, inflationaryResolved);
        Assert.That(result[0].Amount, Is.EqualTo(BigInteger.Parse("150")));
        Assert.That(result[1].Amount, Is.EqualTo(BigInteger.Parse("300")));
        Assert.That(result.Count, Is.EqualTo(2));
    }

    [Test]
    public void ApplyInflationaryAmounts_MixedBundle_OnlyInflationaryReplaced()
    {
        var calls = new[]
        {
            new SimulationCanaryService.DemurragedUnwrapCall("0xfromA", "0xwrapA", BigInteger.Parse("100"), CirclesType.DemurrageCircles),    // pass through
            new SimulationCanaryService.DemurragedUnwrapCall("0xfromB", "0xwrapB", BigInteger.Parse("200"), CirclesType.InflationaryCircles), // convert
            new SimulationCanaryService.DemurragedUnwrapCall("0xfromC", "0xwrapC", BigInteger.Parse("300"), CirclesType.DemurrageCircles),    // pass through
        };
        var inflationaryResolved = new List<BigInteger?>
        {
            BigInteger.Parse("300"), // for the single inflationary call (200 * 1.5 = 300)
        };

        var result = SimulationCanaryService.ApplyInflationaryAmounts(calls, inflationaryResolved);
        Assert.That(result.Count, Is.EqualTo(3));
        Assert.That(result[0].Amount, Is.EqualTo(BigInteger.Parse("100")), "demurraged A unchanged");
        Assert.That(result[1].Amount, Is.EqualTo(BigInteger.Parse("300")), "inflationary B resolved");
        Assert.That(result[2].Amount, Is.EqualTo(BigInteger.Parse("300")), "demurraged C unchanged");
    }

    [Test]
    public void ApplyInflationaryAmounts_NullResolved_FallsBackToOriginal()
    {
        var calls = new[]
        {
            new SimulationCanaryService.DemurragedUnwrapCall("0xfrom", "0xwrap", BigInteger.Parse("100"), CirclesType.InflationaryCircles),
        };
        var resolved = new List<BigInteger?> { null }; // RPC failure on this call

        var result = SimulationCanaryService.ApplyInflationaryAmounts(calls, resolved);
        Assert.That(result[0].Amount, Is.EqualTo(BigInteger.Parse("100")));
    }

    // ──────────────────────────────────────────────────────────────────────
    // BuildUnwrapPrefix — type tagging
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public void BuildUnwrapPrefix_TagsInflationaryWrappers()
    {
        var wrapperToAvatar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["0xwrapd"] = "0xavatard", // demurraged
            ["0xwrapi"] = "0xavatari", // inflationary
        };
        var inflationaryWrappers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0xwrapi" };

        var transfers = new List<TransferPathStep>
        {
            new() { From = "0xholder1", To = "0xnext", TokenOwner = "0xwrapd", Value = "1000" },
            new() { From = "0xholder2", To = "0xnext", TokenOwner = "0xwrapi", Value = "2000" },
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, wrapperToAvatar, inflationaryWrappers);
        Assert.That(calls.Count, Is.EqualTo(2));
        Assert.That(calls[0].Wrapper, Is.EqualTo("0xwrapd"));
        Assert.That(calls[0].WrapperType, Is.EqualTo(CirclesType.DemurrageCircles));
        Assert.That(calls[1].Wrapper, Is.EqualTo("0xwrapi"));
        Assert.That(calls[1].WrapperType, Is.EqualTo(CirclesType.InflationaryCircles));
    }

    [Test]
    public void BuildUnwrapPrefix_NullInflationarySet_TreatsAllAsDemurraged()
    {
        var wrapperToAvatar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["0xwrap"] = "0xavatar",
        };
        var transfers = new List<TransferPathStep>
        {
            new() { From = "0xfrom", To = "0xto", TokenOwner = "0xwrap", Value = "100" },
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, wrapperToAvatar, inflationaryWrappers: null);
        Assert.That(calls.Count, Is.EqualTo(1));
        Assert.That(calls[0].WrapperType, Is.EqualTo(CirclesType.DemurrageCircles));
    }

    [Test]
    public void BuildUnwrapPrefix_SumsAmountsPerWrapperPreservingType()
    {
        var wrapperToAvatar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["0xwrap"] = "0xavatar",
        };
        var inflationaryWrappers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0xwrap" };
        var transfers = new List<TransferPathStep>
        {
            new() { From = "0xholder", To = "0xa", TokenOwner = "0xwrap", Value = "100" },
            new() { From = "0xholder", To = "0xb", TokenOwner = "0xwrap", Value = "200" },
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, wrapperToAvatar, inflationaryWrappers);
        Assert.That(calls.Count, Is.EqualTo(1));
        Assert.That(calls[0].DemurragedAmount, Is.EqualTo(BigInteger.Parse("300")));
        Assert.That(calls[0].WrapperType, Is.EqualTo(CirclesType.InflationaryCircles));
    }

    // ──────────────────────────────────────────────────────────────────────
    // End-to-end regression: chain-anchored numerical values
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Anchored in the 2026-05-27 staging probe:
    ///   Wrapper 0x5d7eaaed (InflationaryCircles, s-gCRC), day 2050.
    ///   Demurraged 343.214965... 1155 ⇒ inflationary 515.773036... ERC20 (β^2050).
    /// Replays the helper pipeline on these chain-anchored values to lock in the math.
    /// </summary>
    [Test]
    public void RegressionInflationaryConversion_StagingAnchoredValues()
    {
        // From probe: convertInflationaryToDemurrageValue(515.773e18, 2050) = 343.215e18
        // ⇒ convertDemurrageToInflationaryValue(343.215e18, 2050) = 515.773e18 (round-trip).
        // Encode the calldata the canary would send and verify the shape.
        var demurraged = BigInteger.Parse("343214965393071985091"); // chain-observed 1155 amount
        var calldata = SimulationCanaryService.EncodeConvertDemurrageCalldata(demurraged, 2050);
        Assert.That(calldata.Length, Is.EqualTo(10 + 64 + 64));
        Assert.That(calldata.StartsWith("0x253dd0b5"), Is.True);

        // Simulate a successful conversion response: encode the inflationary value and parse it.
        var inflationary = BigInteger.Parse("515773035763859818643"); // chain-observed ERC20 burned
        var hex = inflationary.ToString("x", CultureInfo.InvariantCulture);
        if (hex.Length > 0 && hex[0] == '0' && hex.Length > 1) hex = hex.TrimStart('0');
        var json = JsonDocument.Parse(
            "{\"status\":\"0x1\",\"returnData\":\"0x" + new string('0', 64 - hex.Length) + hex + "\"}").RootElement;
        var parsed = SimulationCanaryService.ParseConvertCallReturnData(json);
        Assert.That(parsed, Is.EqualTo(inflationary));
    }

    /// <summary>
    /// Anchored in the 2026-05-27 staging probe:
    ///   Wrapper 0x548c20e6 (DemurrageCircles, gCRC), holder 0x7abe74b7.
    ///   <c>unwrap(229_412.19e18)</c> succeeds and mints 229_412.19e18 of 1155 (ratio 1.0).
    /// The canary must NOT convert this amount — pass-through is correct.
    /// </summary>
    [Test]
    public void RegressionDemurragedWrapperPassThrough_StagingAnchoredValues()
    {
        var wrapperToAvatar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["0x548c20e6c24e4876e20dadbeab75362e2f5a4bc1"] = "0xc19bc204eb1c1d5b3fe500e5e5dfabab625f286c",
        };
        // InflationaryWrappers set is EMPTY — wrapper is demurraged (circlesType=0).
        var inflationaryWrappers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var transfers = new List<TransferPathStep>
        {
            new()
            {
                From = "0x7abe74b71f2958b624cb2be0596678784c0caf6a",
                To = "0xsomesink",
                TokenOwner = "0x548c20e6c24e4876e20dadbeab75362e2f5a4bc1",
                Value = "229412191490542522084844"
            },
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, wrapperToAvatar, inflationaryWrappers);
        Assert.That(calls.Count, Is.EqualTo(1));
        Assert.That(calls[0].WrapperType, Is.EqualTo(CirclesType.DemurrageCircles));
        Assert.That(calls[0].DemurragedAmount, Is.EqualTo(BigInteger.Parse("229412191490542522084844")),
            "demurraged-wrapper amount must pass through unchanged — no β^day inflation");

        // ApplyInflationaryAmounts with empty inflationary list is a no-op for demurraged calls.
        var resolved = SimulationCanaryService.ApplyInflationaryAmounts(calls, new List<BigInteger?>());
        Assert.That(resolved.Count, Is.EqualTo(1));
        Assert.That(resolved[0].Amount, Is.EqualTo(BigInteger.Parse("229412191490542522084844")));
    }

    /// <summary>
    /// Regression for the PR #408 incident: applying the inflation conversion indiscriminately
    /// to a DemurrageCircles wrapper produces a ~1.5x overshoot at day 2050. This test asserts
    /// that the canary's type-discrimination prevents this exact failure mode — a DemurrageCircles
    /// wrapper must yield <c>IsInflationary=false</c> and the resolver must NOT touch its amount.
    /// </summary>
    [Test]
    public void Regression_PR408_DemurragedWrappersAreNotInflated()
    {
        // From the 2026-05-27 incident: wrapper 0x6520d117 (CRC, demurraged) holder 0x122832c9.
        var wrapperToAvatar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["0x6520d117fb606c240ef83a809b3820765015cbb6"] = "0x122832c93e7c7bd7191b960513583d1a85735942",
        };
        // CRITICAL: this wrapper is circlesType=0 (demurraged), so it MUST NOT be in this set.
        var inflationaryWrappers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var transfers = new List<TransferPathStep>
        {
            new()
            {
                From = "0xholder",
                To   = "0xnext",
                TokenOwner = "0x6520d117fb606c240ef83a809b3820765015cbb6",
                Value = "17991355622341912729" // 17.99 demurraged = exact on-chain balance at incident
            },
        };

        var calls = SimulationCanaryService.BuildUnwrapPrefix(transfers, wrapperToAvatar, inflationaryWrappers);
        Assert.That(calls.Count, Is.EqualTo(1));
        Assert.That(calls[0].WrapperType, Is.EqualTo(CirclesType.DemurrageCircles),
            "DemurrageCircles wrapper must NOT be flagged inflationary — PR #408 regression");

        // Even with a non-empty resolved list (would-be inflated value), ApplyInflationaryAmounts
        // must leave this call alone because IsInflationary is false.
        var wouldBeOvershoot = BigInteger.Parse("27036835329465402436"); // 17.99 * β^2050 = the PR #408 incident value
        var resolved = SimulationCanaryService.ApplyInflationaryAmounts(
            calls, new List<BigInteger?> { wouldBeOvershoot });

        Assert.That(resolved[0].Amount, Is.EqualTo(BigInteger.Parse("17991355622341912729")),
            "DemurrageCircles unwrap amount must remain the demurraged sum, not the inflated value");
        Assert.That(resolved[0].Amount, Is.Not.EqualTo(wouldBeOvershoot),
            "If this fails, the PR #408 regression has returned");
    }
}
