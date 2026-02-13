using System.Collections.Concurrent;

namespace Circles.Pathfinder;

/// <summary>
/// Canonical-case address → compact 0-based id.  
/// Look-ups are O(1) and ids are stable for the life of the process.
/// </summary>
public static class AddressIdPool
{
    private static int _next; // 0-based ids

    private static readonly ConcurrentDictionary<string, int> Map =
        new(concurrencyLevel: Environment.ProcessorCount, capacity: 1024, comparer: StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<int, string> Reverse =
        new(concurrencyLevel: Environment.ProcessorCount, capacity: 1024);

    // Marker set: which ids correspond to balance-node strings ("holder-token" / "...-sim")
    private static readonly ConcurrentDictionary<int, byte> BalanceNodeIds =
        new(concurrencyLevel: Environment.ProcessorCount, capacity: 1024);

    /// <summary>Current number of entries in the address pool (for O9 metric).</summary>
    public static int Count => Map.Count;

    public static List<string> GetAvatarSnapshot()
    {
        // Snapshot enumeration of ConcurrentDictionary is safe; we still filter out balance-node strings
        // (we keep the legacy "-" heuristic) and anything we explicitly tagged as a balance node.
        var result = new List<string>(Reverse.Count);
        foreach (var kv in Reverse)
        {
            int id = kv.Key;
            string s = kv.Value;

            bool looksLikeBalanceNode = s.Contains('-');
            bool isBalanceNode = BalanceNodeIds.ContainsKey(id);
            bool include = !looksLikeBalanceNode && !isBalanceNode;

            if (include)
            {
                result.Add(s);
            }
        }

        return result;
    }

    /// <summary>
    /// Looks up an existing id without creating a new entry.
    /// Returns false if the address has never been registered.
    /// </summary>
    public static bool TryIdOf(string address, out int id)
    {
        string lower = address.ToLowerInvariant();
        return Map.TryGetValue(lower, out id);
    }

    public static int IdOf(string _address)
    {
        string lower = _address.ToLowerInvariant();

        if (Map.TryGetValue(lower, out int id))
        {
            return id;
        }

        // Allocate a new id; GetOrAdd ensures only one winner for each key.
        int newId = Interlocked.Increment(ref _next) - 1;
        id = Map.GetOrAdd(lower, newId);
        if (id == newId)
        {
            Reverse.TryAdd(id, lower);
        }

        return id;
    }

    public static string StringOf(int id)
    {
        if (Reverse.TryGetValue(id, out string? value))
        {
            return value;
        }

        throw new KeyNotFoundException($"Unknown id: {id}");
    }

    public static int BalanceNodeIdOf(string _balanceNodeString)
    {
        string lower = _balanceNodeString.ToLowerInvariant();

        if (Map.TryGetValue(lower, out int existing))
        {
            BalanceNodeIds.TryAdd(existing, 0);
            return existing;
        }

        int newId = Interlocked.Increment(ref _next) - 1;
        int id = Map.GetOrAdd(lower, newId);
        if (id == newId)
        {
            Reverse.TryAdd(id, lower);
        }

        BalanceNodeIds.TryAdd(id, 0);
        return id;
    }

    public static bool IsBalanceNode(int nodeAddress)
    {
        return BalanceNodeIds.ContainsKey(nodeAddress);
    }

    public static int TokenPoolIdOf(int tokenId)
        => BalanceNodeIdOf($"tpool-{tokenId}");
}