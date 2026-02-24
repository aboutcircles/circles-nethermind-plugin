using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Circles.Pathfinder.Data;

/// <summary>
/// In-memory accumulator for inflationary token balances.
/// Populated from a full DB load, then kept current via per-block transfer deltas.
/// Demurrage is NOT stored — it is applied at read time by IncrementalLoadGraph.
/// </summary>
public class InMemoryBalanceState
{
    private static readonly string ZeroAddress = "0x0000000000000000000000000000000000000000";

    private readonly Dictionary<(string Account, string TokenAddress), (BigInteger Balance, long LastActivity)> _state = new();
    private readonly ILogger _logger;

    public InMemoryBalanceState(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public int Count => _state.Count;

    public IEnumerable<KeyValuePair<(string Account, string TokenAddress), (BigInteger Balance, long LastActivity)>> GetAll()
        => _state;

    public IEnumerable<(string Account, string TokenAddress)> Keys => _state.Keys;

    public bool TryGet((string Account, string TokenAddress) key, out (BigInteger Balance, long LastActivity) entry)
        => _state.TryGetValue(key, out entry);

    public void InitializeFromFullLoad(
        IEnumerable<(string Balance, string Account, string TokenAddress, long LastActivity)> rows)
    {
        _state.Clear();
        foreach (var row in rows)
        {
            if (!BigInteger.TryParse(row.Balance, out var balance))
            {
                _logger.LogWarning("[InMemoryBalanceState] Failed to parse balance '{Value}' for account={Account}, token={Token} — skipping",
                    row.Balance, row.Account[..Math.Min(10, row.Account.Length)], row.TokenAddress[..Math.Min(10, row.TokenAddress.Length)]);
                continue;
            }
            if (balance <= 0) continue;

            var key = (row.Account.ToLowerInvariant(), row.TokenAddress.ToLowerInvariant());
            _state[key] = (balance, row.LastActivity);
        }
    }

    /// <summary>
    /// Apply a single transfer event to the in-memory state.
    /// Zero-address sides are skipped (mint/burn) — only the non-zero side is processed.
    /// </summary>
    public void ApplyTransfer(string from, string to, string tokenAddress, string value, long timestamp)
    {
        if (!BigInteger.TryParse(value, out var amount))
        {
            _logger.LogWarning("[InMemoryBalanceState] Failed to parse transfer value '{Value}' from={From}, to={To} — skipping",
                value, from[..Math.Min(10, from.Length)], to[..Math.Min(10, to.Length)]);
            return;
        }
        if (amount <= 0) return;

        from = from.ToLowerInvariant();
        to = to.ToLowerInvariant();
        tokenAddress = tokenAddress.ToLowerInvariant();

        // Self-transfers are balance-neutral; only update lastActivity (D8)
        if (string.Equals(from, to, StringComparison.Ordinal) && !string.Equals(from, ZeroAddress, StringComparison.Ordinal))
        {
            var key = (from, tokenAddress);
            if (_state.TryGetValue(key, out var entry))
                _state[key] = (entry.Balance, Math.Max(entry.LastActivity, timestamp));
            return;
        }

        bool fromIsZero = string.Equals(from, ZeroAddress, StringComparison.OrdinalIgnoreCase);
        bool toIsZero = string.Equals(to, ZeroAddress, StringComparison.OrdinalIgnoreCase);

        // Subtract from sender (unless zero-address = mint)
        if (!fromIsZero)
        {
            var senderKey = (from, tokenAddress);
            if (_state.TryGetValue(senderKey, out var senderEntry))
            {
                var newBalance = senderEntry.Balance - amount;
                var newLastActivity = Math.Max(senderEntry.LastActivity, timestamp);
                if (newBalance <= 0)
                    _state.Remove(senderKey);
                else
                    _state[senderKey] = (newBalance, newLastActivity);
            }
            // If sender not in state: transfer of tokens we don't track (e.g. already-zero balance).
            // D7 safety: this is safe because balanceDeltaQuery.sql uses ORDER BY timestamp ASC,
            // so credits always arrive before debits within the same delta window. Within a single
            // block, BigInteger arithmetic is commutative so ordering doesn't matter.
        }

        // Add to receiver (unless zero-address = burn)
        if (!toIsZero)
        {
            var receiverKey = (to, tokenAddress);
            if (_state.TryGetValue(receiverKey, out var receiverEntry))
            {
                var newBalance = receiverEntry.Balance + amount;
                var newLastActivity = Math.Max(receiverEntry.LastActivity, timestamp);
                _state[receiverKey] = (newBalance, newLastActivity);
            }
            else
            {
                _state[receiverKey] = (amount, timestamp);
            }
        }
    }

    /// <summary>
    /// Overwrite balances for specific accounts from a fresh DB load.
    /// Used to backfill new avatars with their complete historical balances.
    /// First removes ALL existing entries for the given accounts, then inserts fresh ones.
    /// </summary>
    public void BackfillAvatars(
        IEnumerable<string> avatarAddresses,
        IEnumerable<(string Balance, string Account, string TokenAddress, long LastActivity)> freshBalances)
    {
        // Remove all existing entries for these avatars (may have partial data from deltas)
        var avatarsToBackfill = new HashSet<string>(avatarAddresses.Select(a => a.ToLowerInvariant()));
        var keysToRemove = _state.Keys.Where(k => avatarsToBackfill.Contains(k.Account)).ToList();
        foreach (var key in keysToRemove)
            _state.Remove(key);

        // Insert fresh complete balances
        foreach (var row in freshBalances)
        {
            if (!BigInteger.TryParse(row.Balance, out var balance))
            {
                _logger.LogWarning("[InMemoryBalanceState] Failed to parse backfill balance '{Value}' for account={Account} — skipping",
                    row.Balance, row.Account[..Math.Min(10, row.Account.Length)]);
                continue;
            }
            if (balance <= 0) continue;

            var key = (row.Account.ToLowerInvariant(), row.TokenAddress.ToLowerInvariant());
            _state[key] = (balance, row.LastActivity);
        }
    }

    /// <summary>
    /// Create a shallow copy for drift comparison.
    /// </summary>
    public InMemoryBalanceState Snapshot()
    {
        var copy = new InMemoryBalanceState(_logger);
        foreach (var kv in _state)
            copy._state[kv.Key] = kv.Value;
        return copy;
    }
}
