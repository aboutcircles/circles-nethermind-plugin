namespace Circles.Pathfinder;

/// <summary>
/// Canonical-case address → compact 0-based id.  
/// Look-ups are O(1) and ids are stable for the life of the process.
/// </summary>
public static class AddressIdPool
{
    private static readonly Dictionary<string, int> Map = new();
    private static readonly List<string> Reverse = new();
    private static readonly List<(int Holder, int Token)> BalanceParts = new();
    private static int _next;

    public static int IdOf(string _address)
    {
        var lower = _address.ToLower();
        if (Map.TryGetValue(lower, out var id))
            return id;

        lock (Map) // cheap – only when we meet a new string
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

        lock (Map)
        {
            if (Map.TryGetValue(lower, out id))
                return id;

            var split = lower.Split('-');
            int holder = int.Parse(split[0]);
            int token = int.Parse(split[1]);

            id = _next++;
            Map[lower] = id;

            Reverse.Add(lower);
            BalanceParts.Add((holder, token));
            return id;
        }
    }

    public static (int Holder, int Token) BalanceNodePartsOf(int id)
        => BalanceParts[id];

    public static bool IsBalanceNode(int nodeAddress)
        => nodeAddress < BalanceParts.Count &&
           BalanceParts[nodeAddress].Holder != 0; // (0,0) = sentinel
}