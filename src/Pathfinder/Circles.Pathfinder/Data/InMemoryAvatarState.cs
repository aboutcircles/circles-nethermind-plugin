namespace Circles.Pathfinder.Data;

/// <summary>
/// In-memory set of registered avatars and groups.
/// Used to filter trust/balance edges to only include registered participants.
/// Stopped avatars (D13) are excluded from Contains() checks.
/// </summary>
public class InMemoryAvatarState
{
    private readonly HashSet<string> _avatars = new();
    private readonly HashSet<string> _groups = new();
    private readonly HashSet<string> _stopped = new();
    private HashSet<string>? _cachedActiveSet;

    /// <summary>Total number of registered avatars (including stopped).</summary>
    public int Count => _avatars.Count;
    /// <summary>Number of registered groups.</summary>
    public int GroupCount => _groups.Count;
    /// <summary>Number of stopped avatars.</summary>
    public int StoppedCount => _stopped.Count;

    /// <summary>All registered avatars (including groups), excluding stopped.</summary>
    public HashSet<string> AvatarSet => _avatars;

    /// <summary>Only registered groups (subset of avatars).</summary>
    public HashSet<string> GetGroupSet() => _groups;

    /// <summary>All registered avatars excluding stopped avatars. Cached until mutation.</summary>
    public IReadOnlySet<string> GetActiveAvatarSet()
    {
        return _cachedActiveSet ??= _avatars.Where(a => !_stopped.Contains(a)).ToHashSet();
    }

    /// <summary>Returns true if address is a registered, non-stopped avatar.</summary>
    public bool Contains(string address)
    {
        var lower = address.ToLowerInvariant();
        return _avatars.Contains(lower) && !_stopped.Contains(lower);
    }

    /// <summary>Returns true if address was ever registered (including stopped avatars).
    /// Use for tokenAddress validation — stopped avatars' tokens are still valid on-chain.</summary>
    public bool IsRegistered(string address)
    {
        return _avatars.Contains(address.ToLowerInvariant());
    }

    /// <summary>Returns true if address is a registered group.</summary>
    public bool IsGroup(string address) => _groups.Contains(address.ToLowerInvariant());

    /// <summary>
    /// Replace all avatar/group state from a full database load.
    /// Groups are identified by type "CrcV2_RegisterGroup".
    /// </summary>
    public void InitializeFromFullLoad(IEnumerable<(string Avatar, string Type)> rows)
    {
        _cachedActiveSet = null;
        _avatars.Clear();
        _groups.Clear();
        foreach (var row in rows)
        {
            _avatars.Add(row.Avatar.ToLowerInvariant());
            if (row.Type == "CrcV2_RegisterGroup")
                _groups.Add(row.Avatar.ToLowerInvariant());
        }
    }

    /// <summary>
    /// Load stopped avatars (full state). Clears and repopulates the stopped set.
    /// </summary>
    public void InitializeStoppedAvatars(IEnumerable<string> stoppedAvatars)
    {
        _cachedActiveSet = null;
        _stopped.Clear();
        foreach (var avatar in stoppedAvatars)
            _stopped.Add(avatar.ToLowerInvariant());
    }

    /// <summary>Mark an avatar as stopped (incremental delta).</summary>
    public void MarkStopped(string avatar)
    {
        _cachedActiveSet = null;
        _stopped.Add(avatar.ToLowerInvariant());
    }

    /// <summary>Register a new avatar (incremental delta). Groups added to both sets.</summary>
    public void AddAvatar(string avatar, string type)
    {
        _cachedActiveSet = null;
        avatar = avatar.ToLowerInvariant();
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
        foreach (var s in _stopped) copy._stopped.Add(s);
        return copy;
    }
}
