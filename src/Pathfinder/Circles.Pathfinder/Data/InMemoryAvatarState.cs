namespace Circles.Pathfinder.Data;

/// <summary>
/// In-memory set of registered avatars and groups.
/// Used to filter trust/balance edges to only include registered participants.
/// </summary>
public class InMemoryAvatarState
{
    private readonly HashSet<string> _avatars = new();
    private readonly HashSet<string> _groups = new();

    public int Count => _avatars.Count;
    public int GroupCount => _groups.Count;

    /// <summary>All registered avatars (including groups).</summary>
    public HashSet<string> AvatarSet => _avatars;

    /// <summary>Only registered groups (subset of avatars).</summary>
    public HashSet<string> GetGroupSet() => _groups;

    public bool Contains(string address) => _avatars.Contains(address);
    public bool IsGroup(string address) => _groups.Contains(address);

    public void InitializeFromFullLoad(IEnumerable<(string Avatar, string Type)> rows)
    {
        _avatars.Clear();
        _groups.Clear();
        foreach (var row in rows)
        {
            _avatars.Add(row.Avatar);
            if (row.Type == "CrcV2_RegisterGroup")
                _groups.Add(row.Avatar);
        }
    }

    public void AddAvatar(string avatar, string type)
    {
        _avatars.Add(avatar);
        if (type == "CrcV2_RegisterGroup")
            _groups.Add(avatar);
    }

    /// <summary>
    /// Create a shallow copy for drift comparison.
    /// </summary>
    public InMemoryAvatarState Snapshot()
    {
        var copy = new InMemoryAvatarState();
        foreach (var a in _avatars) copy._avatars.Add(a);
        foreach (var g in _groups) copy._groups.Add(g);
        return copy;
    }
}
