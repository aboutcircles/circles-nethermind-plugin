using System.Numerics;

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
            var balance = BigInteger.Parse(row.Balance);
            if (balance <= 0) continue;

            var key = (row.Account, row.TokenAddress);
            _state[key] = (balance, row.LastActivity);
        }
    }

    /// <summary>
    /// Apply a single transfer event to the in-memory state.
    /// Zero-address sides are skipped (mint/burn) — only the non-zero side is processed.
    /// </summary>
    public void ApplyTransfer(string from, string to, string tokenAddress, string value, long timestamp)
    {
        var amount = BigInteger.Parse(value);
        if (amount <= 0) return;

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
            // If sender not in state: transfer of tokens we don't track (e.g. already-zero balance)
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
    /// Create a shallow copy for drift comparison.
    /// </summary>
    public InMemoryBalanceState Snapshot()
    {
        var copy = new InMemoryBalanceState();
        foreach (var kv in _state)
            copy._state[kv.Key] = kv.Value;
        return copy;
    }
}
