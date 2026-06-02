using System.Numerics;
using System.Text;
using System.Text.Json;
using Circles.Common.Dto;
using Circles.Pathfinder.Host.Canary;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for the active cache-balance-drift probe's pure logic (#74):
/// per-(holder, token) outflow aggregation and ERC1155 balanceOf calldata encoding.
/// The HTTP-driven ProbeBalanceDriftAsync is exercised against staging's eth_simulateV1;
/// these cover the soundness-critical filtering and ABI encoding in isolation.
/// </summary>
[TestFixture, Parallelizable]
public class SimulationCanaryBalanceProbeTests
{
    private const string Arb = "0x2ceb1b41a7e926bae3bfb4d4daad0b877697d8d1";
    private const string GroupToken = "0xc19bc204eb1c1d5b3fe500e5e5dfabab625f286c";
    private const string Sink = "0xd4cf9afd3ae777c24454b70dd28e32d1bd516f05";
    private const string Zero = "0x0000000000000000000000000000000000000000";

    private static TransferPathStep Step(string from, string to, string tokenOwner, string value) =>
        new() { From = from, To = to, TokenOwner = tokenOwner, Value = value };

    #region AggregateRequiredOutflow

    [Test]
    public void Aggregate_SumsMultipleOutflowsOfSamePair()
    {
        var transfers = new List<TransferPathStep>
        {
            Step(Arb, Sink, GroupToken, "1000000000000"),
            Step(Arb, Sink, GroupToken, "2000000000000")
        };

        var required = SimulationCanaryService.AggregateRequiredOutflow(transfers);

        Assert.That(required, Has.Count.EqualTo(1));
        Assert.That(required[(Arb, GroupToken)], Is.EqualTo(BigInteger.Parse("3000000000000")));
    }

    [Test]
    public void Aggregate_SkipsMintFromZeroAddress()
    {
        // Group mint arrives as from=0x0 — no prior balance required.
        var transfers = new List<TransferPathStep>
        {
            Step(Zero, Arb, GroupToken, "5000000000000")
        };

        var required = SimulationCanaryService.AggregateRequiredOutflow(transfers);

        Assert.That(required, Is.Empty);
    }

    [Test]
    public void Aggregate_SkipsSelfIssuance_WhenSenderIsTokenAvatar()
    {
        // from == tokenOwner: personal-token issuance / group mint (group sends its own token).
        // The sender mints rather than spends a prior balance, so it must not be probed.
        var transfers = new List<TransferPathStep>
        {
            Step(GroupToken, Arb, GroupToken, "5000000000000")
        };

        var required = SimulationCanaryService.AggregateRequiredOutflow(transfers);

        Assert.That(required, Is.Empty);
    }

    [Test]
    public void Aggregate_SkipsNonPositiveAndUnparseableValues()
    {
        var transfers = new List<TransferPathStep>
        {
            Step(Arb, Sink, GroupToken, "0"),
            Step(Arb, Sink, GroupToken, "-100"),
            Step(Arb, Sink, GroupToken, "notanumber")
        };

        var required = SimulationCanaryService.AggregateRequiredOutflow(transfers);

        Assert.That(required, Is.Empty);
    }

    [Test]
    public void Aggregate_KeepsDistinctPairsSeparate()
    {
        const string otherHolder = "0x1111111111111111111111111111111111111111";
        var transfers = new List<TransferPathStep>
        {
            Step(Arb, Sink, GroupToken, "1000000000000"),
            Step(otherHolder, Sink, GroupToken, "7000000000000")
        };

        var required = SimulationCanaryService.AggregateRequiredOutflow(transfers);

        Assert.That(required, Has.Count.EqualTo(2));
        Assert.That(required[(Arb, GroupToken)], Is.EqualTo(BigInteger.Parse("1000000000000")));
        Assert.That(required[(otherHolder, GroupToken)], Is.EqualTo(BigInteger.Parse("7000000000000")));
    }

    [Test]
    public void Aggregate_LowercasesKeys()
    {
        var transfers = new List<TransferPathStep>
        {
            Step(Arb.ToUpperInvariant(), Sink, GroupToken.ToUpperInvariant(), "1000000000000")
        };

        var required = SimulationCanaryService.AggregateRequiredOutflow(transfers);

        Assert.That(required.ContainsKey((Arb, GroupToken)), Is.True);
    }

    #endregion

    #region EncodeBalanceOfCalldata

    [Test]
    public void EncodeBalanceOf_ProducesSelectorAndTwoAddressWords()
    {
        var calldata = SimulationCanaryService.EncodeBalanceOfCalldata(Arb, GroupToken);

        var expected = "0x00fdd58e"
                       + "000000000000000000000000" + "2ceb1b41a7e926bae3bfb4d4daad0b877697d8d1"
                       + "000000000000000000000000" + "c19bc204eb1c1d5b3fe500e5e5dfabab625f286c";
        Assert.That(calldata, Is.EqualTo(expected));
        Assert.That(calldata.Length, Is.EqualTo(2 + 8 + 64 + 64));
    }

    [Test]
    public void EncodeBalanceOf_LowercasesMixedCaseAddresses()
    {
        var calldata = SimulationCanaryService.EncodeBalanceOfCalldata(
            "0x2CEB1B41A7E926BAE3BFB4D4DAAD0B877697D8D1", GroupToken);

        Assert.That(calldata, Does.Contain("2ceb1b41a7e926bae3bfb4d4daad0b877697d8d1"));
        Assert.That(calldata, Does.Not.Contain("2CEB1B41"));
    }

    [Test]
    public void EncodeBalanceOf_MalformedAddress_FallsBackToZeroWord_NoThrow()
    {
        // Non-hex / wrong-length holder → zero word (eth_call returns 0 ⇒ treated as no balance).
        var calldata = SimulationCanaryService.EncodeBalanceOfCalldata("0xnothex", GroupToken);

        var expected = "0x00fdd58e"
                       + new string('0', 64)
                       + "000000000000000000000000" + "c19bc204eb1c1d5b3fe500e5e5dfabab625f286c";
        Assert.That(calldata, Is.EqualTo(expected));
    }

    #endregion

    #region IsHexAddress

    [TestCase("0x2ceb1b41a7e926bae3bfb4d4daad0b877697d8d1", true)]
    [TestCase("2ceb1b41a7e926bae3bfb4d4daad0b877697d8d1", true)]  // no 0x prefix
    [TestCase("0xC19BC204EB1C1D5B3FE500E5E5DFABAB625F286C", true)] // uppercase
    [TestCase("0xnothex", false)]
    [TestCase("0x2ceb1b41", false)]                                // too short
    [TestCase("0x2ceb1b41a7e926bae3bfb4d4daad0b877697d8d1ff", false)] // too long
    [TestCase("0x2ceb1b41a7e926bae3bfb4d4daad0b877697d8dg", false)] // non-hex char
    public void IsHexAddress_ValidatesShapeAndChars(string address, bool expected)
    {
        Assert.That(SimulationCanaryService.IsHexAddress(address), Is.EqualTo(expected));
    }

    #endregion

    #region InterpretBalanceProbeResults (drift gate)

    // Builds an eth_simulateV1 "calls" JSON array; each entry is (status, returnData-or-null).
    private static JsonElement CallsArray(params (string Status, string? ReturnData)[] calls)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < calls.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"status\":\"").Append(calls[i].Status).Append('"');
            if (calls[i].ReturnData != null)
                sb.Append(",\"returnData\":\"").Append(calls[i].ReturnData).Append('"');
            sb.Append('}');
        }
        sb.Append(']');
        return JsonDocument.Parse(sb.ToString()).RootElement.Clone();
    }

    // A balanceOf return word: uint256 right-aligned in 32 bytes.
    private static string BalanceWord(BigInteger v) => "0x" + v.ToString("x").TrimStart('0').PadLeft(64, '0');

    private static (string, string, BigInteger) Pair(BigInteger required) => (Arb, GroupToken, required);

    [Test]
    public void Interpret_ExactlyTwoX_Emitted_ge2x()
    {
        var calls = CallsArray(("0x1", BalanceWord(100)));
        var drifts = SimulationCanaryService.InterpretBalanceProbeResults(
            calls, new[] { Pair(200) }, unwrapCount: 0, out var truncated);

        Assert.That(truncated, Is.False);
        Assert.That(drifts, Has.Count.EqualTo(1));
        Assert.That(drifts[0].Bucket, Is.EqualTo("ge_2x"));
        Assert.That(drifts[0].OnChain, Is.EqualTo((BigInteger)100));
        Assert.That(drifts[0].Needed, Is.EqualTo((BigInteger)200));
    }

    [Test]
    public void Interpret_SubTwoX_Suppressed_ge1x()
    {
        // required 150 vs balance 100 ⇒ floor ratio 1 ⇒ ge_1x ⇒ suppressed (demurrage-rounding band).
        var calls = CallsArray(("0x1", BalanceWord(100)));
        var drifts = SimulationCanaryService.InterpretBalanceProbeResults(
            calls, new[] { Pair(150) }, unwrapCount: 0, out _);

        Assert.That(drifts, Is.Empty);
    }

    [Test]
    public void Interpret_ZeroBalanceWithRequired_Emitted()
    {
        var calls = CallsArray(("0x1", BalanceWord(0)));
        var drifts = SimulationCanaryService.InterpretBalanceProbeResults(
            calls, new[] { Pair(5) }, unwrapCount: 0, out _);

        Assert.That(drifts, Has.Count.EqualTo(1));
        Assert.That(drifts[0].Bucket, Is.EqualTo("zero_balance"));
    }

    [Test]
    public void Interpret_RequiredWithinBalance_Suppressed_le1x()
    {
        var calls = CallsArray(("0x1", BalanceWord(100)));
        var drifts = SimulationCanaryService.InterpretBalanceProbeResults(
            calls, new[] { Pair(80) }, unwrapCount: 0, out _);

        Assert.That(drifts, Is.Empty);
    }

    [Test]
    public void Interpret_HundredX_Emitted_ge100x()
    {
        var calls = CallsArray(("0x1", BalanceWord(1)));
        var drifts = SimulationCanaryService.InterpretBalanceProbeResults(
            calls, new[] { Pair(100) }, unwrapCount: 0, out _);

        Assert.That(drifts.Single().Bucket, Is.EqualTo("ge_100x"));
    }

    [Test]
    public void Interpret_FailedBalanceOfSubCall_Skipped_NotFalsePositive()
    {
        // status 0x0 ⇒ ParseConvertCallReturnData null ⇒ skip, NOT a zero_balance drift.
        var calls = CallsArray(("0x0", null));
        var drifts = SimulationCanaryService.InterpretBalanceProbeResults(
            calls, new[] { Pair(200) }, unwrapCount: 0, out _);

        Assert.That(drifts, Is.Empty);
    }

    [Test]
    public void Interpret_RespectsUnwrapOffset()
    {
        // index 0 is the replayed unwrap result (must be ignored); index 1 is the balanceOf.
        var calls = CallsArray(("0x1", "0x"), ("0x1", BalanceWord(1)));
        var drifts = SimulationCanaryService.InterpretBalanceProbeResults(
            calls, new[] { Pair(100) }, unwrapCount: 1, out _);

        Assert.That(drifts.Single().Bucket, Is.EqualTo("ge_100x"));
        Assert.That(drifts[0].OnChain, Is.EqualTo((BigInteger)1));
    }

    [Test]
    public void Interpret_TruncatedResponse_FlagsTruncated_DoesNotFabricate()
    {
        // Two pairs but only one balanceOf result present ⇒ second pair is beyond the array.
        const string other = "0x1111111111111111111111111111111111111111";
        var calls = CallsArray(("0x1", BalanceWord(1)));
        var drifts = SimulationCanaryService.InterpretBalanceProbeResults(
            calls,
            new[] { Pair(100), (other, GroupToken, (BigInteger)100) },
            unwrapCount: 0, out var truncated);

        Assert.That(truncated, Is.True);
        Assert.That(drifts, Has.Count.EqualTo(1)); // only the first, read; never fabricated for the tail
    }

    #endregion
}
