using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Tests for <see cref="ProxyCallLog"/>: what the per-call log line records for each
/// params shape, and that nothing a client sends reaches the log unchecked.
/// </summary>
[TestFixture]
public class ProxyCallLogTests
{
    private const string Multicall3 = "0xca11bde05977b3631167028862be2a173976ca11";
    private const string Aggregate3 = "0x82ad56cb";

    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement;

    // ── Describe ───────────────────────────────────────────────────────────

    [Test]
    public void Describe_EthCall_ReturnsToAndSelector()
    {
        var p = Params($$"""[{"to":"{{Multicall3}}","data":"{{Aggregate3}}00000020"},"latest"]""");

        Assert.That(ProxyCallLog.Describe(p), Is.EqualTo((Multicall3, Aggregate3)));
    }

    [Test]
    public void Describe_EthCallWithInputField_ReturnsSelector()
    {
        var p = Params($$"""[{"to":"{{Multicall3}}","input":"{{Aggregate3}}00"},"latest"]""");

        Assert.That(ProxyCallLog.Describe(p).Selector, Is.EqualTo(Aggregate3));
    }

    [Test]
    public void Describe_UpperCaseHex_IsLowerCased()
    {
        var p = Params("""[{"to":"0xCA11BDE05977B3631167028862BE2A173976CA11","data":"0x82AD56CB"}]""");

        Assert.That(ProxyCallLog.Describe(p), Is.EqualTo((Multicall3, Aggregate3)));
    }

    [Test]
    public void Describe_EthGetCode_ReturnsAddressAsTo()
    {
        var p = Params($$"""["{{Multicall3}}","latest"]""");

        Assert.That(ProxyCallLog.Describe(p), Is.EqualTo((Multicall3, "-")));
    }

    [Test]
    public void Describe_EthGetLogsWithAddressArray_CountsAddresses()
    {
        var p = Params($$"""[{"address":["{{Multicall3}}","{{Multicall3}}"],"fromBlock":"0x1"}]""");

        Assert.That(ProxyCallLog.Describe(p), Is.EqualTo(("2 addresses", "-")));
    }

    [Test]
    public void Describe_EthGetLogsWithOneAddress_ReturnsIt()
    {
        var p = Params($$"""[{"address":"{{Multicall3}}","fromBlock":"0x1"}]""");

        Assert.That(ProxyCallLog.Describe(p).To, Is.EqualTo(Multicall3));
    }

    [TestCase("[]")]
    [TestCase("[\"latest\", false]")]
    [TestCase("[\"0x2d72935\", false]")] // block number, not an address
    [TestCase("[{\"from\":\"0xca11bde05977b3631167028862be2a173976ca11\"}]")]
    public void Describe_NoAddressOrSelector_ReturnsDashes(string json)
    {
        Assert.That(ProxyCallLog.Describe(Params(json)), Is.EqualTo(("-", "-")));
    }

    [Test]
    public void Describe_TransactionHash_IsNotAnAddress()
    {
        var hash = "0x" + string.Concat(Enumerable.Repeat("ab", 32));

        Assert.That(ProxyCallLog.Describe(Params($"[\"{hash}\"]")), Is.EqualTo(("-", "-")));
    }

    [Test]
    public void Describe_ParamsAbsent_ReturnsDashes()
    {
        Assert.That(ProxyCallLog.Describe(default), Is.EqualTo(("-", "-")));
    }

    [TestCase("""[{"to":"0xca11bde05977b3631167028862be2a173976ca1\n","data":"0x82ad56cb"}]""")]
    [TestCase("""[{"to":"0xca11bde05977b3631167028862be2a173976cazz","data":"0x82ad56cb"}]""")]
    [TestCase("""[{"to":"ca11bde05977b3631167028862be2a173976ca11ab","data":"0x82ad56cb"}]""")]
    public void Describe_MalformedAddress_IsNotLogged(string json)
    {
        Assert.That(ProxyCallLog.Describe(Params(json)).To, Is.EqualTo("-"));
    }

    [TestCase("""[{"to":"0xca11bde05977b3631167028862be2a173976ca11","data":"0x82ad"}]""")]
    [TestCase("""[{"to":"0xca11bde05977b3631167028862be2a173976ca11","data":"0x82ad\n6cb00"}]""")]
    [TestCase("""[{"to":"0xca11bde05977b3631167028862be2a173976ca11","data":12}]""")]
    public void Describe_MalformedOrShortData_HasNoSelector(string json)
    {
        Assert.That(ProxyCallLog.Describe(Params(json)).Selector, Is.EqualTo("-"));
    }

    // ── ClientIp ───────────────────────────────────────────────────────────

    private static DefaultHttpContext Context(string? forwardedFor = null, string? userAgent = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("172.18.0.5");
        if (forwardedFor != null) ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;
        if (userAgent != null) ctx.Request.Headers.UserAgent = userAgent;
        return ctx;
    }

    [Test]
    public void ClientIp_ForwardedFor_ReturnsFirstEntry()
    {
        Assert.That(ProxyCallLog.ClientIp(Context("34.96.41.231, 172.18.0.2")), Is.EqualTo("34.96.41.231"));
    }

    [Test]
    public void ClientIp_ForwardedForIpv6_ReturnsIt()
    {
        Assert.That(ProxyCallLog.ClientIp(Context("2001:db8::1")), Is.EqualTo("2001:db8::1"));
    }

    [TestCase("not-an-ip")]
    [TestCase("1.2.3.4\nfake log line")]
    public void ClientIp_InvalidForwardedFor_FallsBackToPeer(string header)
    {
        Assert.That(ProxyCallLog.ClientIp(Context(header)), Is.EqualTo("172.18.0.5"));
    }

    [Test]
    public void ClientIp_NoHeader_ReturnsPeer()
    {
        Assert.That(ProxyCallLog.ClientIp(Context()), Is.EqualTo("172.18.0.5"));
    }

    // ── UserAgent ──────────────────────────────────────────────────────────

    [Test]
    public void UserAgent_Absent_ReturnsDash()
    {
        Assert.That(ProxyCallLog.UserAgent(Context()), Is.EqualTo("-"));
    }

    [Test]
    public void UserAgent_Plain_IsKept()
    {
        Assert.That(ProxyCallLog.UserAgent(Context(userAgent: "viem/2.21.1")), Is.EqualTo("viem/2.21.1"));
    }

    [Test]
    public void UserAgent_Long_IsCutTo120()
    {
        Assert.That(ProxyCallLog.UserAgent(Context(userAgent: new string('a', 500))), Has.Length.EqualTo(120));
    }

    [Test]
    public void UserAgent_NonPrintable_IsReplaced()
    {
        Assert.That(ProxyCallLog.UserAgent(Context(userAgent: "uaéx")), Is.EqualTo("ua?x"));
    }
}
