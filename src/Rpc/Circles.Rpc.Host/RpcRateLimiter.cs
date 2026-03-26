using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading.RateLimiting;

namespace Circles.Rpc.Host;

/// <summary>
/// Per-IP token bucket rate limiter for JSON-RPC requests.
///
/// Each IP address gets its own token bucket that refills at a steady rate.
/// Batch items count individually — a 50-item batch costs 50 tokens, preventing
/// batch amplification from bypassing rate limits.
///
/// Token bucket parameters:
///   - TokensPerPeriod = RpcRateLimitPerSecond (sustained rate, refills every second)
///   - TokenLimit = RpcRateLimitBurst (max burst size before throttling)
///   - ReplenishmentPeriod = 1 second
///   - AutoReplenishment = true (timer-based, no manual replenish needed)
///
/// Stale limiters (no activity for 5+ minutes) are evicted periodically to
/// prevent unbounded memory growth from unique IPs.
///
/// Set RPC_RATE_LIMIT_PER_SECOND=0 to disable rate limiting entirely.
/// </summary>
public sealed class RpcRateLimiter : IDisposable
{
    private readonly ConcurrentDictionary<string, (TokenBucketRateLimiter Limiter, long LastUsedTicks)> _limiters = new();
    private readonly int _tokensPerSecond;
    private readonly int _burstLimit;
    private readonly Timer? _evictionTimer;
    private bool _disposed;

    /// <summary>True if rate limiting is active (RPC_RATE_LIMIT_PER_SECOND > 0).</summary>
    public bool IsEnabled => _tokensPerSecond > 0;

    public RpcRateLimiter(int tokensPerSecond, int burstLimit)
    {
        _tokensPerSecond = tokensPerSecond;
        _burstLimit = burstLimit;

        if (IsEnabled)
        {
            // Evict stale per-IP limiters every 5 minutes
            _evictionTimer = new Timer(EvictStaleLimiters, null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
    }

    /// <summary>
    /// Try to acquire <paramref name="permits"/> tokens for the given IP.
    /// Returns true if allowed, false if rate-limited (caller should return 429).
    /// Internal Docker/private network callers (group-tms, cache-service, etc.) are exempt.
    /// </summary>
    public bool TryAcquire(string remoteIp, int permits = 1)
    {
        if (!IsEnabled || permits <= 0) return true;
        if (IsPrivateNetwork(remoteIp)) return true;

        var (limiter, _) = _limiters.GetOrAdd(remoteIp, _ => (CreateLimiter(), Stopwatch.GetTimestamp()));

        // Update last-used timestamp for eviction
        _limiters[remoteIp] = (limiter, Stopwatch.GetTimestamp());

        using var lease = limiter.AttemptAcquire(permits);
        return lease.IsAcquired;
    }

    private TokenBucketRateLimiter CreateLimiter() => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = _burstLimit,
        TokensPerPeriod = _tokensPerSecond,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        AutoReplenishment = true,
        QueueLimit = 0 // No queuing — reject immediately
    });

    /// <summary>
    /// Periodic cleanup: remove limiters for IPs that haven't been seen in 5+ minutes.
    /// Prevents unbounded memory growth from many unique IPs (scanners, load balancers).
    /// </summary>
    private void EvictStaleLimiters(object? state)
    {
        var cutoff = Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * 300); // 5 minutes
        foreach (var kvp in _limiters)
        {
            if (kvp.Value.LastUsedTicks < cutoff)
            {
                if (_limiters.TryRemove(kvp.Key, out var removed))
                    removed.Limiter.Dispose();
            }
        }
    }

    /// <summary>
    /// Returns true for private/reserved IP ranges (RFC 1918, link-local, loopback).
    /// These are internal Docker network callers that should bypass rate limiting.
    /// Handles both raw IPs and IPv4-mapped IPv6 (::ffff:172.18.x.x).
    /// </summary>
    private static bool IsPrivateNetwork(string ipString)
    {
        // Strip ::ffff: prefix (IPv4-mapped IPv6, common in Kestrel)
        if (ipString.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
            ipString = ipString[7..];

        if (!IPAddress.TryParse(ipString, out var ip))
            return false;

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false; // Only handle IPv4

        return bytes[0] switch
        {
            10 => true,                                          // 10.0.0.0/8
            172 => bytes[1] >= 16 && bytes[1] <= 31,             // 172.16.0.0/12 (Docker default)
            192 => bytes[1] == 168,                               // 192.168.0.0/16
            127 => true,                                          // 127.0.0.0/8 (loopback)
            169 => bytes[1] == 254,                               // 169.254.0.0/16 (link-local)
            _ => false
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _evictionTimer?.Dispose();
        foreach (var kvp in _limiters)
            kvp.Value.Limiter.Dispose();
        _limiters.Clear();
    }
}
