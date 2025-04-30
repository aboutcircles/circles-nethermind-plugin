namespace Circles.Pathfinder;

/// <summary>
/// Canonical-case address → compact 0-based id.  
/// Look-ups are O(1) and ids are stable for the life of the process.
/// </summary>
public static class AddressIdPool
{
    private static readonly Dictionary<string, int> Map = new();
    private static readonly List<string> Reverse = new();
    private static readonly Dictionary<string, int> BalanceNodeMap = new();
    private static int _next;

    public static int IdOf(string _address)
    {
        var lower = _address.ToLower();
        if (Map.TryGetValue(lower, out var id))
            return id;

        lock (Map)                        // cheap – only when we meet a new string
        {
            if (Map.TryGetValue(lower, out id))
                return id;

            id = _next++;
            Map[lower] = id;
            Reverse.Add(lower);
            return id;
        }
    }

    public static string StringOf(int id) => Reverse[id];

    public static int BalanceNodeIdOf(string _balanceNodeString)
    {
        var lower = _balanceNodeString.ToLower();
        if (Map.TryGetValue(lower, out var id))
            return id;

        lock (Map)                        // cheap – only when we meet a new string
        {
            if (Map.TryGetValue(lower, out id))
                return id;

            id = _next++;
            Map[lower] = id;
            BalanceNodeMap[lower] = id;
            Reverse.Add(lower);
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