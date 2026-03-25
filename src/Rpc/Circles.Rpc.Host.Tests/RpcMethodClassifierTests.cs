namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Tests for <see cref="RpcMethodClassifier"/> — the routing brain of the batch handler.
/// Ensures circles methods are handled locally, safe Ethereum methods are proxied,
/// and dangerous admin/debug methods are blocked.
/// </summary>
[TestFixture]
public class RpcMethodClassifierTests
{
    // ── IsCirclesMethod ────────────────────────────────────────────────────

    [TestCase("circles_health")]
    [TestCase("circles_getTotalBalance")]
    [TestCase("circles_query")]
    [TestCase("circles_events")]
    [TestCase("circles_events_paginated")]
    [TestCase("circles_getAvatarInfo")]
    [TestCase("circles_paginated_query")]
    public void IsCirclesMethod_CirclesPrefix_ReturnsTrue(string method)
    {
        Assert.That(RpcMethodClassifier.IsCirclesMethod(method), Is.True);
    }

    [TestCase("circlesV2_findPath")]
    [TestCase("circlesV2_getTotalBalance")]
    public void IsCirclesMethod_CirclesV2Prefix_ReturnsTrue(string method)
    {
        Assert.That(RpcMethodClassifier.IsCirclesMethod(method), Is.True);
    }

    [Test]
    public void IsCirclesMethod_RpcDiscover_ReturnsTrue()
    {
        Assert.That(RpcMethodClassifier.IsCirclesMethod("rpc.discover"), Is.True);
    }

    [TestCase("eth_blockNumber")]
    [TestCase("net_version")]
    [TestCase("web3_clientVersion")]
    [TestCase("admin_addPeer")]
    [TestCase("debug_traceTransaction")]
    [TestCase("personal_unlockAccount")]
    [TestCase("miner_start")]
    [TestCase("unknown_method")]
    public void IsCirclesMethod_NonCirclesMethod_ReturnsFalse(string method)
    {
        Assert.That(RpcMethodClassifier.IsCirclesMethod(method), Is.False);
    }

    [Test]
    public void IsCirclesMethod_Null_ReturnsFalse()
    {
        Assert.That(RpcMethodClassifier.IsCirclesMethod(null), Is.False);
    }

    [Test]
    public void IsCirclesMethod_Empty_ReturnsFalse()
    {
        Assert.That(RpcMethodClassifier.IsCirclesMethod(""), Is.False);
    }

    // ── IsProxyAllowed ─────────────────────────────────────────────────────

    [TestCase("eth_blockNumber")]
    [TestCase("eth_getBalance")]
    [TestCase("eth_call")]
    [TestCase("eth_estimateGas")]
    [TestCase("eth_getTransactionReceipt")]
    public void IsProxyAllowed_EthPrefix_ReturnsTrue(string method)
    {
        Assert.That(RpcMethodClassifier.IsProxyAllowed(method), Is.True);
    }

    [TestCase("net_version")]
    [TestCase("net_peerCount")]
    public void IsProxyAllowed_NetPrefix_ReturnsTrue(string method)
    {
        Assert.That(RpcMethodClassifier.IsProxyAllowed(method), Is.True);
    }

    [TestCase("web3_clientVersion")]
    [TestCase("web3_sha3")]
    public void IsProxyAllowed_Web3Prefix_ReturnsTrue(string method)
    {
        Assert.That(RpcMethodClassifier.IsProxyAllowed(method), Is.True);
    }

    [TestCase("admin_addPeer")]
    [TestCase("admin_nodeInfo")]
    [TestCase("debug_traceTransaction")]
    [TestCase("debug_traceBlock")]
    [TestCase("personal_unlockAccount")]
    [TestCase("personal_sendTransaction")]
    [TestCase("miner_start")]
    [TestCase("miner_stop")]
    public void IsProxyAllowed_DangerousMethod_ReturnsFalse(string method)
    {
        Assert.That(RpcMethodClassifier.IsProxyAllowed(method), Is.False,
            $"SECURITY: {method} must NOT be proxied to Nethermind");
    }

    [TestCase("circles_health")]
    [TestCase("circlesV2_findPath")]
    [TestCase("rpc.discover")]
    [TestCase("unknown_method")]
    public void IsProxyAllowed_NonEthMethod_ReturnsFalse(string method)
    {
        Assert.That(RpcMethodClassifier.IsProxyAllowed(method), Is.False);
    }

    [Test]
    public void IsProxyAllowed_Null_ReturnsFalse()
    {
        Assert.That(RpcMethodClassifier.IsProxyAllowed(null), Is.False);
    }

    [Test]
    public void IsProxyAllowed_Empty_ReturnsFalse()
    {
        Assert.That(RpcMethodClassifier.IsProxyAllowed(""), Is.False);
    }

    // ── Classification completeness ────────────────────────────────────────

    [Test]
    public void CirclesAndProxy_AreExclusive()
    {
        // No method should match both classifiers
        var methods = new[]
        {
            "circles_health", "circlesV2_findPath", "rpc.discover",
            "eth_blockNumber", "net_version", "web3_clientVersion",
            "admin_addPeer", "debug_traceTransaction", "unknown"
        };

        foreach (var m in methods)
        {
            var isCircles = RpcMethodClassifier.IsCirclesMethod(m);
            var isProxy = RpcMethodClassifier.IsProxyAllowed(m);
            Assert.That(isCircles && isProxy, Is.False,
                $"Method '{m}' matched BOTH classifiers — routing ambiguity");
        }
    }
}
