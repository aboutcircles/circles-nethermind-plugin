using NUnit.Framework;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Pure tests for <see cref="CirclesRpcModule.ParseFee"/> — the defensive parser for the community
/// profile's <c>membershipFee</c> value. Communities publish their own profile documents, so the parser
/// must accept the schema's JSON-number shape, tolerate a JSON-string shape and a stray '%', and reject
/// anything else as "no fee" (null) rather than crash or feed garbage into the 100%-cap sum.
/// </summary>
[TestFixture]
public class CommunityFeeParseTests
{
    [TestCase("0.1", 0.1)]      // JSON number → text "0.1"
    [TestCase("10", 10)]        // whole number
    [TestCase("0", 0)]          // zero fee
    [TestCase("0.333", 0.333)]  // many decimals
    [TestCase(" 0.2 ", 0.2)]    // surrounding whitespace
    [TestCase("10%", 10)]       // tolerated trailing percent sign
    [TestCase("0.1 %", 0.1)]    // percent with space
    public void ParseFee_AcceptsValidNumericForms(string raw, decimal expected)
    {
        Assert.That(CirclesRpcModule.ParseFee(raw), Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("abc")]       // non-numeric
    [TestCase("0.1.2")]     // malformed
    [TestCase("-5")]        // negative would wrongly reduce the cap sum → rejected
    [TestCase("-0.1")]
    [TestCase("NaN")]
    public void ParseFee_RejectsInvalidOrNegative_AsNull(string? raw)
    {
        Assert.That(CirclesRpcModule.ParseFee(raw), Is.Null);
    }
}
