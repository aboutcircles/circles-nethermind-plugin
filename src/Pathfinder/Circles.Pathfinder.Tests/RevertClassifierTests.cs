using Circles.Pathfinder.Simulation;

namespace Circles.Pathfinder.Tests;

[TestFixture]
public class RevertClassifierTests
{
    // Helper: build a 64-char ABI word (avoids pre-commit hook flagging 64 hex chars as keys)
    private static string Word(string hex) => hex.PadLeft(64, '0');
    private static string Zeros => Word("0");

    // Build CirclesErrorAddressUintArgs(address, uint256, uint8) revert data
    private static string AddressUintRevert(string addr, string val, string code, bool withPrefix = true)
        => (withPrefix ? "execution reverted: 0x" : "0x") + "5e418dba" + Word(addr) + Word(val) + Word(code);

    // Build CirclesErrorOneAddressArg(address, uint8) revert data
    private static string OneAddrRevert(string addr, string code, bool withPrefix = true)
        => (withPrefix ? "execution reverted: 0x" : "0x") + "c14c0700" + Word(addr) + Word(code);

    // ── CirclesErrorAddressUintArgs (0x5e418dba) ────────────────────

    [Test]
    public void Staging_OperatorRevert_ClassifiedAsSimulation()
    {
        // Real staging data: code byte 0x00 = OperatorNotApprovedForSource
        var revert = AddressUintRevert("4bfc74983d6338d3395a00118546614bb78472c2", "0", "0");
        var (category, label) = RevertClassifier.Classify(revert);
        Assert.That(category, Is.EqualTo("simulation"));
        Assert.That(label, Is.EqualTo("operator_not_approved"));
    }

    [Test]
    public void FlowEdgeNotPermitted_ClassifiedAsBug()
    {
        // Code byte 0x21 = type 1 = FlowEdgeIsNotPermitted
        var revert = AddressUintRevert("aaa", "bbb", "21");
        var (category, label) = RevertClassifier.Classify(revert);
        Assert.That(category, Is.EqualTo("bug"));
        Assert.That(label, Is.EqualTo("flow_edge_not_permitted"));
    }

    [Test]
    public void GroupMintPolicyRejectedBurn_ClassifiedAsBug()
    {
        var revert = AddressUintRevert("aaa", "bbb", "40", withPrefix: false);
        var (category, label) = RevertClassifier.Classify(revert);
        Assert.That(category, Is.EqualTo("bug"));
        Assert.That(label, Is.EqualTo("group_mint_policy_rejected_burn"));
    }

    [Test]
    public void GroupMintPolicyRejectedMint_ClassifiedAsBug()
    {
        var revert = AddressUintRevert("aaa", "bbb", "60", withPrefix: false);
        var (category, label) = RevertClassifier.Classify(revert);
        Assert.That(category, Is.EqualTo("bug"));
        Assert.That(label, Is.EqualTo("group_mint_policy_rejected_mint"));
    }

    [Test]
    public void DemurrageOverflow_ClassifiedAsBug()
    {
        var revert = AddressUintRevert("aaa", "1", "80", withPrefix: false);
        var (category, label) = RevertClassifier.Classify(revert);
        Assert.That(category, Is.EqualTo("bug"));
        Assert.That(label, Is.EqualTo("demurrage_overflow"));
    }

    [Test]
    public void DemurrageDayError_ClassifiedAsBug()
    {
        var revert = AddressUintRevert("aaa", "1", "a0", withPrefix: false);
        var (category, label) = RevertClassifier.Classify(revert);
        Assert.That(category, Is.EqualTo("bug"));
        Assert.That(label, Is.EqualTo("demurrage_day_error"));
    }

    [Test]
    public void UnrecognizedCodeByte_AddressUintArgs_ClassifiedAsUnknown()
    {
        var revert = AddressUintRevert("aaa", "1", "c0", withPrefix: false);
        var (category, label) = RevertClassifier.Classify(revert);
        Assert.That(category, Is.EqualTo("unknown"));
        Assert.That(label, Is.EqualTo("unrecognized_code_byte"));
    }

    // ── CirclesErrorOneAddressArg (0xc14c0700) ──────────────────────

    [Test]
    public void MustBeHuman_ClassifiedAsSimulation()
    {
        var revert = OneAddrRevert("aaa", "0");
        var (category, label) = RevertClassifier.Classify(revert);
        Assert.That(category, Is.EqualTo("simulation"));
        Assert.That(label, Is.EqualTo("must_be_human"));
    }

    [Test]
    public void AvatarNotRegistered_ClassifiedAsBug()
    {
        var revert = OneAddrRevert("aaa", "25");
        var (category, label) = RevertClassifier.Classify(revert);
        Assert.That(category, Is.EqualTo("bug"));
        Assert.That(label, Is.EqualTo("avatar_not_registered"));
    }

    [Test]
    public void UnrecognizedCodeByte_OneAddressArg_ClassifiedAsUnknown()
    {
        var revert = OneAddrRevert("aaa", "40", withPrefix: false);
        var (category, label) = RevertClassifier.Classify(revert);
        Assert.That(category, Is.EqualTo("unknown"));
        Assert.That(label, Is.EqualTo("unrecognized_code_byte"));
    }

    // ── ERC1155 errors ──────────────────────────────────────────────

    [Test]
    public void ERC1155InsufficientBalance_ClassifiedAsBug()
    {
        var revert = "execution reverted: 0x03dee4c5" + Word("aaa") + Word("1") + Word("64") + Word("32");
        var (category, label) = RevertClassifier.Classify(revert);
        Assert.That(category, Is.EqualTo("bug"));
        Assert.That(label, Is.EqualTo("insufficient_balance"));
    }

    [Test]
    public void ERC1155InvalidReceiver_ClassifiedAsInput()
    {
        var revert = "execution reverted: 0x57f447ce" + Word("aaa");
        var (category, label) = RevertClassifier.Classify(revert);
        Assert.That(category, Is.EqualTo("input"));
        Assert.That(label, Is.EqualTo("invalid_receiver"));
    }

    // ── Edge cases ──────────────────────────────────────────────────

    [Test]
    public void GenericRevert_ClassifiedAsUnknown()
    {
        var (category, label) = RevertClassifier.Classify("execution reverted");
        Assert.That(category, Is.EqualTo("unknown"));
        Assert.That(label, Is.EqualTo("generic_revert"));
    }

    [Test]
    public void NullData_ClassifiedAsUnknown()
    {
        var (category, label) = RevertClassifier.Classify(null);
        Assert.That(category, Is.EqualTo("unknown"));
        Assert.That(label, Is.EqualTo("empty_revert"));
    }

    [Test]
    public void EmptyString_ClassifiedAsUnknown()
    {
        var (category, label) = RevertClassifier.Classify("");
        Assert.That(category, Is.EqualTo("unknown"));
        Assert.That(label, Is.EqualTo("empty_revert"));
    }

    [Test]
    public void TruncatedData_AddressUintArgs_FallsBackToUnknown()
    {
        var truncated = "0x5e418dba" + "00000000000000000000";
        var (category, label) = RevertClassifier.Classify(truncated);
        Assert.That(category, Is.EqualTo("unknown"));
        Assert.That(label, Is.EqualTo("truncated_circles_error"));
    }

    [Test]
    public void TruncatedData_OneAddressArg_FallsBackToUnknown()
    {
        var truncated = "0xc14c0700" + "00000000000000000000";
        var (category, label) = RevertClassifier.Classify(truncated);
        Assert.That(category, Is.EqualTo("unknown"));
        Assert.That(label, Is.EqualTo("truncated_circles_error"));
    }

    [Test]
    public void UnrecognizedSelector_ClassifiedAsUnknown()
    {
        var (category, label) = RevertClassifier.Classify("0xdeadbeef0000000000");
        Assert.That(category, Is.EqualTo("unknown"));
        Assert.That(label, Is.EqualTo("unrecognized"));
    }

    // ── ExtractCodeByte unit tests ──────────────────────────────────

    [TestCase("00", 0x00)]
    [TestCase("1f", 0x1F)]
    [TestCase("20", 0x20)]
    [TestCase("21", 0x21)]
    [TestCase("3f", 0x3F)]
    [TestCase("40", 0x40)]
    [TestCase("80", 0x80)]
    [TestCase("a0", 0xA0)]
    [TestCase("bf", 0xBF)]
    [TestCase("c0", 0xC0)]
    [TestCase("ff", 0xFF)]
    public void ExtractCodeByte_AddressUintArgs_ParsesCorrectly(string codeByteHex, int expected)
    {
        var data = "5e418dba" + Word("aaa") + Word("1") + Word(codeByteHex);
        int codeByteOffset = 8 + 64 + 64 + 62;
        var result = RevertClassifier.ExtractCodeByte(data, "0x5e418dba", codeByteOffset);
        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase("00", 0x00)]
    [TestCase("25", 0x25)]
    [TestCase("40", 0x40)]
    public void ExtractCodeByte_OneAddressArg_ParsesCorrectly(string codeByteHex, int expected)
    {
        var data = "c14c0700" + Word("aaa") + Word(codeByteHex);
        int codeByteOffset = 8 + 64 + 62;
        var result = RevertClassifier.ExtractCodeByte(data, "0xc14c0700", codeByteOffset);
        Assert.That(result, Is.EqualTo(expected));
    }
}
