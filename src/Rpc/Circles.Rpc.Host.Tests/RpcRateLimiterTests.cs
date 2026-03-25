namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Tests for <see cref="RpcRateLimiter"/> — per-IP token bucket rate limiting.
/// Verifies token consumption, burst handling, per-IP isolation, and disable behavior.
/// </summary>
[TestFixture]
public class RpcRateLimiterTests
{
    [Test]
    public void TryAcquire_WithinLimit_ReturnsTrue()
    {
        using var limiter = new RpcRateLimiter(tokensPerSecond: 10, burstLimit: 10);
        Assert.That(limiter.TryAcquire("192.168.1.1"), Is.True);
    }

    [Test]
    public void TryAcquire_ExceedsBurst_ReturnsFalse()
    {
        using var limiter = new RpcRateLimiter(tokensPerSecond: 5, burstLimit: 3);

        // Consume all 3 burst tokens
        Assert.That(limiter.TryAcquire("10.0.0.1", permits: 3), Is.True);
        // 4th should fail
        Assert.That(limiter.TryAcquire("10.0.0.1"), Is.False);
    }

    [Test]
    public void TryAcquire_BatchCostsMultipleTokens()
    {
        using var limiter = new RpcRateLimiter(tokensPerSecond: 100, burstLimit: 50);

        // 50-item batch should consume all 50 burst tokens
        Assert.That(limiter.TryAcquire("10.0.0.1", permits: 50), Is.True);
        // Next request should fail (no tokens left until refill)
        Assert.That(limiter.TryAcquire("10.0.0.1"), Is.False);
    }

    [Test]
    public void TryAcquire_DifferentIPs_Independent()
    {
        using var limiter = new RpcRateLimiter(tokensPerSecond: 5, burstLimit: 2);

        // Exhaust IP 1
        Assert.That(limiter.TryAcquire("10.0.0.1", permits: 2), Is.True);
        Assert.That(limiter.TryAcquire("10.0.0.1"), Is.False);

        // IP 2 should still have its own budget
        Assert.That(limiter.TryAcquire("10.0.0.2", permits: 2), Is.True);
    }

    [Test]
    public void TryAcquire_Disabled_AlwaysReturnsTrue()
    {
        // tokensPerSecond=0 disables rate limiting
        using var limiter = new RpcRateLimiter(tokensPerSecond: 0, burstLimit: 0);

        Assert.That(limiter.IsEnabled, Is.False);
        // Should always succeed regardless of volume
        for (int i = 0; i < 1000; i++)
            Assert.That(limiter.TryAcquire("10.0.0.1"), Is.True);
    }

    [Test]
    public void TryAcquire_ZeroPermits_AlwaysReturnsTrue()
    {
        using var limiter = new RpcRateLimiter(tokensPerSecond: 1, burstLimit: 1);
        // Zero permits = nothing consumed
        Assert.That(limiter.TryAcquire("10.0.0.1", permits: 0), Is.True);
    }

    [Test]
    public void TryAcquire_NegativePermits_AlwaysReturnsTrue()
    {
        using var limiter = new RpcRateLimiter(tokensPerSecond: 1, burstLimit: 1);
        Assert.That(limiter.TryAcquire("10.0.0.1", permits: -1), Is.True);
    }

    [Test]
    public void IsEnabled_PositiveRate_ReturnsTrue()
    {
        using var limiter = new RpcRateLimiter(tokensPerSecond: 100, burstLimit: 200);
        Assert.That(limiter.IsEnabled, Is.True);
    }

    [Test]
    public void IsEnabled_ZeroRate_ReturnsFalse()
    {
        using var limiter = new RpcRateLimiter(tokensPerSecond: 0, burstLimit: 0);
        Assert.That(limiter.IsEnabled, Is.False);
    }

    [Test]
    public void Dispose_DoesNotThrow()
    {
        var limiter = new RpcRateLimiter(tokensPerSecond: 10, burstLimit: 10);
        limiter.TryAcquire("10.0.0.1");
        limiter.TryAcquire("10.0.0.2");
        Assert.DoesNotThrow(() => limiter.Dispose());
    }

    [Test]
    public void Dispose_DoubleDispose_DoesNotThrow()
    {
        var limiter = new RpcRateLimiter(tokensPerSecond: 10, burstLimit: 10);
        limiter.Dispose();
        Assert.DoesNotThrow(() => limiter.Dispose());
    }
}
