namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for AddressIdPool: ID allocation, reverse lookup,
/// balance-node marking, avatar snapshot filtering, and thread safety.
/// </summary>
[TestFixture, Parallelizable]
public class AddressIdPoolTests
{
    // Unique prefix per test file to avoid collisions with other test classes
    // (AddressIdPool is static and shared across the entire process)
    private const string Prefix = "0xap";

    #region IdOf / StringOf

    [Test]
    public void IdOf_ReturnsSameId_ForSameAddress()
    {
        var addr = $"{Prefix}10000000000000000000000000000000000000a1";
        int id1 = AddressIdPool.IdOf(addr);
        int id2 = AddressIdPool.IdOf(addr);

        Assert.That(id2, Is.EqualTo(id1));
    }

    [Test]
    public void IdOf_IsCaseInsensitive()
    {
        var lower = $"{Prefix}20000000000000000000000000000000000000a2";
        var upper = lower.ToUpperInvariant();

        int id1 = AddressIdPool.IdOf(lower);
        int id2 = AddressIdPool.IdOf(upper);

        Assert.That(id2, Is.EqualTo(id1));
    }

    [Test]
    public void IdOf_DifferentAddresses_DifferentIds()
    {
        var a = $"{Prefix}30000000000000000000000000000000000000a3";
        var b = $"{Prefix}40000000000000000000000000000000000000a4";

        int idA = AddressIdPool.IdOf(a);
        int idB = AddressIdPool.IdOf(b);

        Assert.That(idA, Is.Not.EqualTo(idB));
    }

    [Test]
    public void StringOf_ReturnsLowercasedAddress()
    {
        var addr = $"{Prefix}50000000000000000000000000000000000000A5";
        int id = AddressIdPool.IdOf(addr);

        string result = AddressIdPool.StringOf(id);

        Assert.That(result, Is.EqualTo(addr.ToLowerInvariant()));
    }

    [Test]
    public void StringOf_UnknownId_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => AddressIdPool.StringOf(int.MaxValue - 1));
    }

    #endregion

    #region BalanceNodeIdOf / IsBalanceNode

    [Test]
    public void BalanceNodeIdOf_MarksAsBalanceNode()
    {
        var balStr = $"{Prefix}60000000000000000000000000000000000000a6-tok";
        int id = AddressIdPool.BalanceNodeIdOf(balStr);

        Assert.That(AddressIdPool.IsBalanceNode(id), Is.True);
    }

    [Test]
    public void IsBalanceNode_RegularAddress_ReturnsFalse()
    {
        var addr = $"{Prefix}70000000000000000000000000000000000000a7";
        int id = AddressIdPool.IdOf(addr);

        Assert.That(AddressIdPool.IsBalanceNode(id), Is.False);
    }

    [Test]
    public void TokenPoolIdOf_CreatesBalanceNodeWithTpoolPrefix()
    {
        var tokenAddr = $"{Prefix}80000000000000000000000000000000000000a8";
        int tokenId = AddressIdPool.IdOf(tokenAddr);
        int poolId = AddressIdPool.TokenPoolIdOf(tokenId);

        Assert.That(AddressIdPool.IsBalanceNode(poolId), Is.True);
        Assert.That(AddressIdPool.StringOf(poolId), Does.StartWith("tpool-"));
    }

    #endregion

    #region GetAvatarSnapshot

    [Test]
    public void GetAvatarSnapshot_ExcludesBalanceNodes()
    {
        // Register a regular avatar
        var avatar = $"{Prefix}90000000000000000000000000000000000000a9";
        AddressIdPool.IdOf(avatar);

        // Register a balance node (contains "-" AND marked via BalanceNodeIdOf)
        var balNode = $"{Prefix}a0000000000000000000000000000000000000b0-tok";
        AddressIdPool.BalanceNodeIdOf(balNode);

        var snapshot = AddressIdPool.GetAvatarSnapshot();

        Assert.That(snapshot, Does.Contain(avatar.ToLowerInvariant()));
        Assert.That(snapshot, Does.Not.Contain(balNode.ToLowerInvariant()));
    }

    [Test]
    public void GetAvatarSnapshot_ExcludesEntriesContainingDash()
    {
        // The "-" heuristic catches balance-node strings even if not explicitly marked
        var dashEntry = $"{Prefix}b0000000000000000000000000000000000000b1-x";
        AddressIdPool.IdOf(dashEntry);

        var snapshot = AddressIdPool.GetAvatarSnapshot();

        Assert.That(snapshot, Does.Not.Contain(dashEntry.ToLowerInvariant()));
    }

    #endregion

    #region Count

    [Test]
    public void Count_IncreasesWithNewEntries()
    {
        int before = AddressIdPool.Count;

        var unique = $"{Prefix}c0000000000000000000000000000000000000c2";
        AddressIdPool.IdOf(unique);

        Assert.That(AddressIdPool.Count, Is.GreaterThanOrEqualTo(before + 1));
    }

    #endregion

    #region Concurrent Access

    [Test]
    public void IdOf_ConcurrentAccess_AlwaysConsistent()
    {
        // Multiple threads requesting the same new address should all get the same ID
        var addr = $"{Prefix}d0000000000000000000000000000000000000d3";
        var ids = new int[100];

        Parallel.For(0, 100, i =>
        {
            ids[i] = AddressIdPool.IdOf(addr);
        });

        Assert.That(ids.Distinct().Count(), Is.EqualTo(1),
            "All concurrent callers should receive the same ID");
    }

    [Test]
    public void IdOf_ConcurrentDifferentAddresses_AllUnique()
    {
        var ids = new int[50];

        Parallel.For(0, 50, i =>
        {
            ids[i] = AddressIdPool.IdOf($"{Prefix}e{i:d39}");
        });

        Assert.That(ids.Distinct().Count(), Is.EqualTo(50),
            "Each unique address should get a unique ID");
    }

    #endregion
}
