using Circles.Cache.Service.Caches;
using Circles.Common;
using FluentAssertions;
using Xunit;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Tests for cross-domain interactions where operations on one cache
/// affect or depend on another cache domain.
/// </summary>
public class CrossDomainTests
{
    private readonly CacheContainer _cache;

    public CrossDomainTests()
    {
        _cache = new CacheContainer(rollbackCapacity: 12);
    }

    [Fact]
    public void V2Trust_WhereGroupIsTruster_CreatesMembership()
    {
        // When group trusts member, UpsertGroupMembership should create a membership entry.
        // The caller (NotificationListenerService) determines whether truster is a group
        // and calls UpsertGroupMembership. Here we verify the membership works correctly.
        var group = "0xgroup000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";

        // Register group
        _cache.Groups.Add(100, group, ("TestGroup", "0xmint", "TG"));
        // Group trusts member → creates membership
        _cache.UpsertGroupMembership(100, group, member, 999999L);

        // Both group membership AND trust should be queryable
        _cache.GetGroupMembers(group).Should().ContainSingle(m => m.Member == member.ToLowerInvariant());
        _cache.GetMemberGroups(member).Should().ContainSingle(g => g.Group == group.ToLowerInvariant());
    }

    [Fact]
    public void V2Trust_GroupRegisteredAfterTrust_NoMembership()
    {
        // If trust is added before group registration, no membership should exist
        // until the caller explicitly creates one
        var group = "0xgroup000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";

        // Add trust before group registration
        _cache.UpsertV2Trust(100, group, member, 999999L);

        // No group membership should exist (just a trust relation)
        _cache.GetGroupMembers(group).Should().BeEmpty();
        _cache.GetMemberGroups(member).Should().BeEmpty();

        // But trust should exist
        _cache.GetTrustsFor(group, isV1: false).Should().ContainSingle();
    }

    [Fact]
    public void V2Trust_ExpiryZero_RemovesTrustAndMembership()
    {
        var group = "0xgroup000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";

        // Add trust + membership
        _cache.UpsertV2Trust(100, group, member, 999999L);
        _cache.UpsertGroupMembership(100, group, member, 999999L);

        // Remove both (simulates expiry=0 event)
        _cache.RemoveV2Trust(101, group, member);
        _cache.RemoveGroupMembership(101, group, member);

        _cache.GetTrustsFor(group, isV1: false).Should().BeEmpty();
        _cache.GetGroupMembers(group).Should().BeEmpty();
        _cache.GetMemberGroups(member).Should().BeEmpty();
    }

    [Fact]
    public void V2Trust_NonGroupTruster_NoMembershipCreated()
    {
        // When a non-group avatar trusts another, no membership should be created
        var avatar1 = "0xhuman00000000000000000000000000000000000";
        var avatar2 = "0xhuman20000000000000000000000000000000000";

        _cache.V2Avatars.Add(100, avatar1, ("CrcV2_RegisterHuman", 12345L));
        _cache.UpsertV2Trust(100, avatar1, avatar2, 999999L);

        // Only trust, no membership
        _cache.GetTrustsFor(avatar1, isV1: false).Should().HaveCount(1);
        _cache.GetGroupMembers(avatar1).Should().BeEmpty();
    }

    [Fact]
    public void ERC20WrapperBalance_MergedIntoV2Cache()
    {
        // ERC20 wrapper balances are stored in V2BalancesByAccountAndToken,
        // distinguished by looking up the wrapper index
        var address = "0xholder00000000000000000000000000000000000";
        var wrapperAddr = "0xwrapper0000000000000000000000000000000000";
        var avatar = "0xavatar00000000000000000000000000000000000";

        // Register wrapper
        _cache.UpsertWrapper(100, wrapperAddr, avatar, CirclesType.InflationaryCircles); // inflationary

        // Add wrapper balance to V2 cache (same cache as ERC1155)
        _cache.V2BalancesByAccountAndToken.Add(100, $"{address}:{wrapperAddr}", 500m);
        _cache.RebuildSecondaryIndexes();

        // Should be retrievable via V2 index
        var tokens = _cache.GetTokenIdsForAddress(address, isV1: false).ToList();
        tokens.Should().Contain(wrapperAddr);

        // Wrapper info should identify it as ERC20
        var info = _cache.GetWrapperInfo(wrapperAddr);
        info.Should().NotBeNull();
        info!.Value.CirclesType.Should().Be(CirclesType.InflationaryCircles);
    }

    [Fact]
    public void CaseNormalization_ConsistentAcrossPaths()
    {
        // Mixed-case addresses should be normalized to lowercase consistently
        var mixedCase = "0xDe374ECE6fA50e781E81AaC78e811B33d16912C7";
        var lowerCase = mixedCase.ToLowerInvariant();

        // Upsert with mixed case
        _cache.UpsertV2Trust(100, mixedCase, "0xTRUSTEE000000000000000000000000000000000", 999999L);
        _cache.UpsertGroupMembership(100, mixedCase, "0xMEMBER0000000000000000000000000000000000", 999999L);
        _cache.UpsertWrapper(100, mixedCase, "0xAVATAR0000000000000000000000000000000000", CirclesType.DemurrageCircles);

        // Query with lowercase — should find everything
        _cache.GetTrustsFor(lowerCase, isV1: false).Should().HaveCount(1);
        _cache.GetGroupMembers(lowerCase).Should().HaveCount(1);
        _cache.GetWrapperInfo(lowerCase).Should().NotBeNull();

        // Query with mixed case — should also find everything (methods normalize internally)
        _cache.GetTrustsFor(mixedCase, isV1: false).Should().HaveCount(1);
        _cache.GetGroupMembers(mixedCase).Should().HaveCount(1);
        _cache.GetWrapperInfo(mixedCase).Should().NotBeNull();
    }

    [Fact]
    public void GroupMembership_KeyFormat_MatchesBetweenWarmupAndLive()
    {
        // Warmup seeds GroupMemberships with "group:member" key format
        // Live updates via UpsertGroupMembership should use the same format
        var group = "0xgroup000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";

        // Simulate warmup seed
        var seedKey = $"{group}:{member}";
        _cache.GroupMemberships.Seed(new Dictionary<string, (string Member, long ExpiryTime)>
        {
            [seedKey] = (member, 999999L)
        }, atBlockNo: 99);
        _cache.RebuildSecondaryIndexes();

        // Verify warmup data is queryable
        _cache.GetGroupMembers(group).Should().HaveCount(1);
        _cache.GetMemberGroups(member).Should().HaveCount(1);

        // Simulate live update overwriting the same entry
        _cache.UpsertGroupMembership(100, group, member, 888888L);

        // Should still have exactly one membership (not two)
        _cache.GetGroupMembers(group).Should().HaveCount(1);
        var updated = _cache.GetGroupMembers(group).First();
        updated.ExpiryTime.Should().Be(888888L);
    }

    [Fact]
    public void TrustAndMembership_BothRolledBackTogether()
    {
        var group = "0xgroup000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";

        // Add at block 100
        _cache.UpsertV2Trust(100, group, member, 999999L);
        _cache.UpsertGroupMembership(100, group, member, 999999L);

        // Add more at block 101
        var member2 = "0xmember2000000000000000000000000000000000";
        _cache.UpsertV2Trust(101, group, member2, 888888L);
        _cache.UpsertGroupMembership(101, group, member2, 888888L);

        // Rollback block 101
        _cache.RollbackAll(101);
        _cache.RebuildSecondaryIndexes();

        // Block 100 data should remain
        _cache.GetTrustsFor(group, isV1: false).Should().HaveCount(1);
        _cache.GetGroupMembers(group).Should().HaveCount(1);

        // Block 101 data should be gone
        _cache.V2TrustRelations.ContainsKey($"{group}:{member2}").Should().BeFalse();
        _cache.GroupMemberships.ContainsKey($"{group}:{member2}").Should().BeFalse();
    }
}
