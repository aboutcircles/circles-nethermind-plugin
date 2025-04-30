namespace Circles.Pathfinder;

/// <summary>
/// Canonical-case address → compact 0-based id.  
/// Look-ups are O(1) and ids are stable for the life of the process.
/// </summary>
public static class AddressIdPool
{
    private static readonly Dictionary<string, int> Map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Reverse = new();
    private static readonly Dictionary<string, int> BalanceNodeMap = new();
    private static int _next;

    public static int IdOf(string address)
    {
        if (Map.TryGetValue(address, out var id))
            return id;

        lock (Map)                        // cheap – only when we meet a new string
        {
            if (Map.TryGetValue(address, out id))
                return id;

            id = _next++;
            Map[address] = id;
            Reverse.Add(address);
            return id;
        }
    }

    public static string StringOf(int id) => Reverse[id];

    public static int BalanceNodeIdOf(string balanceNodeString)
    {
        if (Map.TryGetValue(balanceNodeString, out var id))
            return id;

        lock (Map)                        // cheap – only when we meet a new string
        {
            if (Map.TryGetValue(balanceNodeString, out id))
                return id;

            id = _next++;
            Map[balanceNodeString] = id;
            Reverse.Add(balanceNodeString);
            return id;
        }
    }

    public static bool IsBalanceNode(int nodeAddress)
    {
        lock (BalanceNodeMap)
        {
            return BalanceNodeMap.ContainsKey(StringOf(nodeAddress));
        }
    }
}