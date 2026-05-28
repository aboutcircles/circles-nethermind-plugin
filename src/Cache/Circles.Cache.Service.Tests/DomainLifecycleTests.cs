using Circles.Cache.Service.Caches;
using Circles.Common;
using Xunit;
using FluentAssertions;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Tests the full lifecycle of each cache domain: seed → add → verify → rollback → verify restored.
/// Ensures every domain correctly handles the complete block lifecycle.
/// </summary>
public class DomainLifecycleTests
{
    private readonly CacheContainer _cache;

    public DomainLifecycleTests()
    {
        _cache = new CacheContainer(rollbackCapacity: 12);
    }

    [Fact]
    public void V1Avatars_Lifecycle()
    {
        var addr = "0xaaa0000000000000000000000000000000000000";

        // Seed
        _cache.V1Avatars.Seed(new Dictionary<string, (string, string?)>
        {
            [addr] = ("Human", "0xtoken")
        }, atBlockNo: 99);

        // Add at block 100
        var addr2 = "0xbbb0000000000000000000000000000000000000";
        _cache.V1Avatars.Add(100, addr2, ("Organization", null));
        _cache.V1Avatars.Count.Should().Be(2);

        // Rollback block 100
        _cache.V1Avatars.DeleteAllGreaterOrEqualBlock(100);
        _cache.V1Avatars.Count.Should().Be(1);
        _cache.V1Avatars.TryGetValue(addr, out var info).Should().BeTrue();
        info.Type.Should().Be("Human");
        _cache.V1Avatars.ContainsKey(addr2).Should().BeFalse();
    }

    [Fact]
    public void V1TokenOwner_Lifecycle()
    {
        var token = "0xtoken000000000000000000000000000000000000";
        var owner = "0xowner000000000000000000000000000000000000";

        _cache.V1TokenOwnerByToken.Seed(new Dictionary<string, string>
        {
            [token] = owner
        }, atBlockNo: 99);

        var token2 = "0xtoken200000000000000000000000000000000000";
        _cache.V1TokenOwnerByToken.Add(100, token2, "0xowner2");
        _cache.V1TokenOwnerByToken.Count.Should().Be(2);

        _cache.V1TokenOwnerByToken.DeleteAllGreaterOrEqualBlock(100);
        _cache.V1TokenOwnerByToken.Count.Should().Be(1);
        _cache.V1TokenOwnerByToken.Get(token).Should().Be(owner);
    }

    [Fact]
    public void V1Balances_WithIndex_Lifecycle()
    {
        var addr = "0xholder00000000000000000000000000000000000";
        var token = "0xtoken000000000000000000000000000000000000";
        var key = $"{addr}:{token}";

        // Seed
        _cache.V1BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>
        {
            [key] = 100m
        }, atBlockNo: 99);
        _cache.RebuildSecondaryIndexes();

        _cache.GetTokenIdsForAddress(addr, isV1: true).Should().Contain(token);

        // Add at block 100
        var token2 = "0xtoken200000000000000000000000000000000000";
        _cache.V1BalancesByAccountAndToken.Add(100, $"{addr}:{token2}", 200m);
        _cache.UpdateBalanceIndex($"{addr}:{token2}", isV1: true, 200m);

        _cache.GetTokenIdsForAddress(addr, isV1: true).Should().HaveCount(2);

        // Rollback block 100
        _cache.V1BalancesByAccountAndToken.DeleteAllGreaterOrEqualBlock(100);
        _cache.RebuildSecondaryIndexes();

        _cache.GetTokenIdsForAddress(addr, isV1: true).Should().HaveCount(1);
        _cache.GetTokenIdsForAddress(addr, isV1: true).Should().Contain(token);
    }

    [Fact]
    public void V1Trust_WithIndex_Lifecycle()
    {
        var truster = "0xaaa0000000000000000000000000000000000000";
        var trustee = "0xbbb0000000000000000000000000000000000000";

        // Seed
        _cache.V1TrustRelations.Seed(new Dictionary<string, long>
        {
            [$"{truster}:{trustee}"] = 50L
        }, atBlockNo: 99);
        _cache.RebuildSecondaryIndexes();

        _cache.GetTrustsFor(truster, isV1: true).Should().HaveCount(1);

        // Add at block 100
        var trustee2 = "0xccc0000000000000000000000000000000000000";
        _cache.UpsertV1Trust(100, truster, trustee2, 75);

        _cache.GetTrustsFor(truster, isV1: true).Should().HaveCount(2);

        // Rollback
        _cache.RollbackAll(100);
        _cache.RebuildSecondaryIndexes();

        _cache.GetTrustsFor(truster, isV1: true).Should().HaveCount(1);
        var remaining = _cache.GetTrustsFor(truster, isV1: true).Single();
        remaining.Trustee.Should().Be(trustee);
        remaining.ExpiryTime.Should().Be(50L);
    }

    [Fact]
    public void V1CidMap_Lifecycle()
    {
        var addr = "0xaaa0000000000000000000000000000000000000";

        _cache.V1AvatarToCidMap.Seed(new Dictionary<string, string>
        {
            [addr] = "QmOriginal"
        }, atBlockNo: 99);

        _cache.V1AvatarToCidMap.Add(100, addr, "QmUpdated");
        _cache.V1AvatarToCidMap.Get(addr).Should().Be("QmUpdated");

        _cache.V1AvatarToCidMap.DeleteAllGreaterOrEqualBlock(100);
        _cache.V1AvatarToCidMap.Get(addr).Should().Be("QmOriginal");
    }

    [Fact]
    public void V2Avatars_Lifecycle()
    {
        var addr = "0xaaa0000000000000000000000000000000000000";

        _cache.V2Avatars.Seed(new Dictionary<string, (string, long)>
        {
            [addr] = ("Human", 12345L)
        }, atBlockNo: 99);

        var addr2 = "0xbbb0000000000000000000000000000000000000";
        _cache.V2Avatars.Add(100, addr2, ("Organization", 67890L));
        _cache.V2Avatars.Count.Should().Be(2);

        _cache.V2Avatars.DeleteAllGreaterOrEqualBlock(100);
        _cache.V2Avatars.Count.Should().Be(1);
        _cache.V2Avatars.TryGetValue(addr, out var info).Should().BeTrue();
        info.Type.Should().Be("Human");
    }

    [Fact]
    public void V2Groups_Lifecycle()
    {
        var group = "0xgroup000000000000000000000000000000000000";

        _cache.Groups.Seed(new Dictionary<string, (string, string, string)>
        {
            [group] = ("TestGroup", "0xmint", "TG")
        }, atBlockNo: 99);

        var group2 = "0xgroup200000000000000000000000000000000000";
        _cache.Groups.Add(100, group2, ("NewGroup", "0xmint2", "NG"));
        _cache.Groups.Count.Should().Be(2);

        _cache.Groups.DeleteAllGreaterOrEqualBlock(100);
        _cache.Groups.Count.Should().Be(1);
        _cache.Groups.TryGetValue(group, out var info).Should().BeTrue();
        info.Name.Should().Be("TestGroup");
    }

    [Fact]
    public void V2Balances_WithIndex_Lifecycle()
    {
        var addr = "0xholder00000000000000000000000000000000000";
        var token = "0xtoken000000000000000000000000000000000000";
        var key = $"{addr}:{token}";

        _cache.V2BalancesByAccountAndToken.Seed(new Dictionary<string, decimal>
        {
            [key] = 500m
        }, atBlockNo: 99);
        _cache.RebuildSecondaryIndexes();

        // Add at block 100
        var token2 = "0xtoken200000000000000000000000000000000000";
        _cache.V2BalancesByAccountAndToken.Add(100, $"{addr}:{token2}", 300m);
        _cache.UpdateBalanceIndex($"{addr}:{token2}", isV1: false, 300m);

        _cache.GetTokenIdsForAddress(addr, isV1: false).Should().HaveCount(2);

        // Rollback
        _cache.V2BalancesByAccountAndToken.DeleteAllGreaterOrEqualBlock(100);
        _cache.RebuildSecondaryIndexes();

        _cache.GetTokenIdsForAddress(addr, isV1: false).Should().HaveCount(1);
    }

    [Fact]
    public void V2Trust_WithIndex_Lifecycle()
    {
        var truster = "0xaaa0000000000000000000000000000000000000";
        var trustee = "0xbbb0000000000000000000000000000000000000";

        _cache.V2TrustRelations.Seed(new Dictionary<string, long>
        {
            [$"{truster}:{trustee}"] = 999999L
        }, atBlockNo: 99);
        _cache.RebuildSecondaryIndexes();

        // Add at block 100
        var trustee2 = "0xccc0000000000000000000000000000000000000";
        _cache.UpsertV2Trust(100, truster, trustee2, 888888L);

        _cache.GetTrustsFor(truster, isV1: false).Should().HaveCount(2);

        // Rollback
        _cache.RollbackAll(100);
        _cache.RebuildSecondaryIndexes();

        _cache.GetTrustsFor(truster, isV1: false).Should().HaveCount(1);
    }

    [Fact]
    public void V2Memberships_WithIndex_Lifecycle()
    {
        var group = "0xgroup000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";

        _cache.GroupMemberships.Seed(new Dictionary<string, (string, long)>
        {
            [$"{group}:{member}"] = (member, 999999L)
        }, atBlockNo: 99);
        _cache.RebuildSecondaryIndexes();

        // Add at block 100
        var member2 = "0xmember2000000000000000000000000000000000";
        _cache.UpsertGroupMembership(100, group, member2, 888888L);

        _cache.GetGroupMembers(group).Should().HaveCount(2);

        // Rollback
        _cache.RollbackAll(100);
        _cache.RebuildSecondaryIndexes();

        _cache.GetGroupMembers(group).Should().HaveCount(1);
        _cache.GetGroupMembers(group).Single().Member.Should().Be(member);
    }

    [Fact]
    public void ERC20Wrappers_WithReverseIndex_Lifecycle()
    {
        var wrapper = "0xwrapper0000000000000000000000000000000000";
        var avatar = "0xavatar00000000000000000000000000000000000";

        _cache.Erc20WrapperAddresses.Seed(new Dictionary<string, (string, CirclesType)>
        {
            [wrapper] = (avatar, CirclesType.DemurrageCircles)
        }, atBlockNo: 99);
        _cache.RebuildSecondaryIndexes();

        _cache.GetWrapperInfo(wrapper).Should().NotBeNull();

        // Add at block 100
        var wrapper2 = "0xwrapper2000000000000000000000000000000000";
        _cache.UpsertWrapper(100, wrapper2, "0xavatar2", CirclesType.InflationaryCircles);

        _cache.GetWrapperInfo(wrapper2).Should().NotBeNull();

        // Rollback
        _cache.Erc20WrapperAddresses.DeleteAllGreaterOrEqualBlock(100);
        _cache.RebuildSecondaryIndexes();

        _cache.GetWrapperInfo(wrapper).Should().NotBeNull();
        _cache.GetWrapperInfo(wrapper2).Should().BeNull();
    }

    [Fact]
    public void V2CidMap_Lifecycle()
    {
        var addr = "0xaaa0000000000000000000000000000000000000";

        _cache.V2AvatarToCidMap.Seed(new Dictionary<string, string>
        {
            [addr] = "QmOriginal"
        }, atBlockNo: 99);

        _cache.V2AvatarToCidMap.Add(100, addr, "QmUpdated");
        _cache.V2AvatarToCidMap.Get(addr).Should().Be("QmUpdated");

        _cache.V2AvatarToCidMap.DeleteAllGreaterOrEqualBlock(100);
        _cache.V2AvatarToCidMap.Get(addr).Should().Be("QmOriginal");
    }

    [Fact]
    public void V2ShortNames_Lifecycle()
    {
        var addr = "0xaaa0000000000000000000000000000000000000";

        _cache.V2AvatarToShortNameMap.Seed(new Dictionary<string, string>
        {
            [addr] = "alice"
        }, atBlockNo: 99);

        _cache.V2AvatarToShortNameMap.Add(100, addr, "alice2");
        _cache.V2AvatarToShortNameMap.Get(addr).Should().Be("alice2");

        _cache.V2AvatarToShortNameMap.DeleteAllGreaterOrEqualBlock(100);
        _cache.V2AvatarToShortNameMap.Get(addr).Should().Be("alice");
    }

    [Fact]
    public void ConsentedFlowFlags_Lifecycle()
    {
        var addr = "0xaaa0000000000000000000000000000000000000";
        var flags = new byte[] { 0x01, 0x02 };

        _cache.ConsentedFlowFlags.Seed(new Dictionary<string, byte[]>
        {
            [addr] = flags
        }, atBlockNo: 99);

        var newFlags = new byte[] { 0xFF };
        _cache.ConsentedFlowFlags.Add(100, addr, newFlags);
        _cache.ConsentedFlowFlags.Get(addr).Should().BeEquivalentTo(newFlags);

        _cache.ConsentedFlowFlags.DeleteAllGreaterOrEqualBlock(100);
        _cache.ConsentedFlowFlags.Get(addr).Should().BeEquivalentTo(flags);
    }

    [Fact]
    public void V2LastActivity_Lifecycle()
    {
        var key = "0xaddr:0xtoken";

        _cache.V2LastActivity.Seed(new Dictionary<string, long>
        {
            [key] = 1000L
        }, atBlockNo: 99);

        _cache.V2LastActivity.Add(100, key, 2000L);
        _cache.V2LastActivity.Get(key).Should().Be(2000L);

        _cache.V2LastActivity.DeleteAllGreaterOrEqualBlock(100);
        _cache.V2LastActivity.Get(key).Should().Be(1000L);
    }

    // --- Multi-domain scenarios ---

    [Fact]
    public void AllCaches_RollbackAll_CrossConsistency()
    {
        // Populate multiple domains at block 100
        _cache.V1Avatars.Add(100, "0xaaa", ("Human", "0xtoken"));
        _cache.V2Avatars.Add(100, "0xbbb", ("CrcV2_RegisterHuman", 12345L));
        _cache.V1BalancesByAccountAndToken.Add(100, "0xaaa:0xtoken", 100m);
        _cache.V2BalancesByAccountAndToken.Add(100, "0xbbb:0xerc1155", 200m);
        _cache.UpsertV1Trust(100, "0xaaa", "0xbbb", 50);
        _cache.UpsertV2Trust(100, "0xbbb", "0xaaa", 999999L);
        _cache.V2LastActivity.Add(100, "0xbbb:0xerc1155", 1000L);

        // Rollback all
        _cache.RollbackAll(100);

        // Everything should be gone
        _cache.V1Avatars.Count.Should().Be(0);
        _cache.V2Avatars.Count.Should().Be(0);
        _cache.V1BalancesByAccountAndToken.Count.Should().Be(0);
        _cache.V2BalancesByAccountAndToken.Count.Should().Be(0);
        _cache.V1TrustRelations.Count.Should().Be(0);
        _cache.V2TrustRelations.Count.Should().Be(0);
        _cache.V2LastActivity.Count.Should().Be(0);
    }

    [Fact]
    public void AllCaches_RebuildSecondaryIndexes_MatchesPrimary()
    {
        // Populate caches
        var addr = "0xaaa0000000000000000000000000000000000000";
        var token = "0xtoken000000000000000000000000000000000000";
        var truster = "0xbbb0000000000000000000000000000000000000";
        var group = "0xgroup000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";
        var wrapper = "0xwrapper0000000000000000000000000000000000";

        _cache.V1BalancesByAccountAndToken.Add(100, $"{addr}:{token}", 100m);
        _cache.V2BalancesByAccountAndToken.Add(100, $"{addr}:{token}", 200m);
        _cache.V1TrustRelations.Add(100, $"{addr}:{truster}", 50L);
        _cache.V2TrustRelations.Add(100, $"{addr}:{truster}", 999999L);
        _cache.GroupMemberships.Add(100, $"{group}:{member}", (member, 999999L));
        _cache.Erc20WrapperAddresses.Add(100, wrapper, ("0xavatar", CirclesType.DemurrageCircles));

        // Rebuild and verify
        _cache.RebuildSecondaryIndexes();

        _cache.GetTokenIdsForAddress(addr, isV1: true).Should().Contain(token);
        _cache.GetTokenIdsForAddress(addr, isV1: false).Should().Contain(token);
        _cache.GetTrustsFor(addr, isV1: true).Should().HaveCount(1);
        _cache.GetTrustsFor(addr, isV1: false).Should().HaveCount(1);
        _cache.GetTrustedByFor(truster, isV1: true).Should().HaveCount(1);
        _cache.GetTrustedByFor(truster, isV1: false).Should().HaveCount(1);
        _cache.GetGroupMembers(group).Should().HaveCount(1);
        _cache.GetMemberGroups(member).Should().HaveCount(1);
        _cache.GetWrapperInfo(wrapper).Should().NotBeNull();

        // Rebuild again — should be idempotent
        _cache.RebuildSecondaryIndexes();
        _cache.GetTokenIdsForAddress(addr, isV1: true).Should().Contain(token);
        _cache.GetGroupMembers(group).Should().HaveCount(1);
    }
}
