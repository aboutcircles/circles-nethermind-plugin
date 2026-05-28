using Circles.Cache.Service.Caches;
using Circles.Common;
using Xunit;
using FluentAssertions;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Regression guards for the 8 bugs fixed in PR #242.
/// Each test prevents re-introduction of a specific bug.
/// </summary>
public class PR242RegressionTests
{
    private readonly CacheContainer _cache;

    public PR242RegressionTests()
    {
        _cache = new CacheContainer(rollbackCapacity: 12);
    }

    // --- Bug 1: V1 trust direction was reversed (canSendTo was trustee, user was truster) ---

    [Fact]
    public void V1Trust_Direction_CanSendToIsTruster_UserIsTrustee()
    {
        // In Hub.sol Trust(address indexed canSendTo, address indexed user, uint256 limit)
        // canSendTo = truster (the one granting trust), user = trustee (the one being trusted)
        // Key format: "truster:trustee"
        var truster = "0xaaa0000000000000000000000000000000000000";
        var trustee = "0xbbb0000000000000000000000000000000000000";

        _cache.UpsertV1Trust(100, truster, trustee, 50);
        _cache.RebuildSecondaryIndexes();

        // GetTrustsFor(truster) should return trustee
        var trusts = _cache.GetTrustsFor(truster, isV1: true).ToList();
        trusts.Should().ContainSingle(t => t.Trustee == trustee.ToLowerInvariant());

        // GetTrustedByFor(trustee) should return truster
        var trustedBy = _cache.GetTrustedByFor(trustee, isV1: true).ToList();
        trustedBy.Should().ContainSingle(t => t.Truster == truster.ToLowerInvariant());

        // Reversed direction should NOT exist
        _cache.GetTrustsFor(trustee, isV1: true).Should().BeEmpty();
        _cache.GetTrustedByFor(truster, isV1: true).Should().BeEmpty();
    }

    // --- Bug 2: V1 trust limits were stored as 0L sentinel instead of actual limit ---

    [Fact]
    public void V1Trust_Limit_Persisted_NotSentinelZero()
    {
        var truster = "0xaaa0000000000000000000000000000000000000";
        var trustee = "0xbbb0000000000000000000000000000000000000";
        var limit = 75L;

        _cache.UpsertV1Trust(100, truster, trustee, limit);

        var key = $"{truster}:{trustee}";
        _cache.V1TrustRelations.TryGetValue(key, out var storedLimit).Should().BeTrue();
        storedLimit.Should().Be(limit, "actual trust limit should be stored, not sentinel 0L");
    }

    // --- Bug 3: V2 group membership direction was inverted (trustee was used as group instead of truster) ---

    [Fact]
    public void V2GroupMembership_TrusterIsGroup_NotTrustee()
    {
        // When a group trusts a member, the GROUP is the truster.
        // Key format: "group:member"
        var group = "0xgroup000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";

        _cache.UpsertGroupMembership(100, group, member, 999999L);
        _cache.RebuildSecondaryIndexes();

        // GetGroupMembers(group) should return member
        var members = _cache.GetGroupMembers(group).ToList();
        members.Should().ContainSingle(m => m.Member == member.ToLowerInvariant());

        // GetMemberGroups(member) should return group
        var groups = _cache.GetMemberGroups(member).ToList();
        groups.Should().ContainSingle(g => g.Group == group.ToLowerInvariant());

        // Inverted direction should NOT work
        _cache.GetGroupMembers(member).Should().BeEmpty("member is not a group");
        _cache.GetMemberGroups(group).Should().BeEmpty("group is not a member of itself");
    }

    // --- Bug 4: V2LastActivity was ConcurrentDictionary (not rollback-safe), now RollbackCache ---

    [Fact]
    public void V2LastActivity_IsRollbackCache_SupportsRollback()
    {
        // V2LastActivity should be a RollbackCache, verifiable by its rollback behavior
        _cache.V2LastActivity.Should().BeOfType<RollbackCache<string, long>>();

        // Functional test: add data at two blocks, rollback to first
        _cache.V2LastActivity.Add(100, "0xaddr:0xtoken", 1000L);
        _cache.V2LastActivity.Add(101, "0xaddr:0xtoken", 2000L);

        _cache.V2LastActivity.TryGetValue("0xaddr:0xtoken", out var val).Should().BeTrue();
        val.Should().Be(2000L);

        // Rollback block 101
        _cache.V2LastActivity.DeleteAllGreaterOrEqualBlock(101);

        _cache.V2LastActivity.TryGetValue("0xaddr:0xtoken", out var restoredVal).Should().BeTrue();
        restoredVal.Should().Be(1000L, "rollback should restore previous value");
    }

    // --- Bug 5: Secondary trust index not synced atomically on upsert ---

    [Fact]
    public void SecondaryTrustIndex_SyncedOnUpsert()
    {
        var truster = "0xaaa0000000000000000000000000000000000000";
        var trustee = "0xbbb0000000000000000000000000000000000000";

        _cache.UpsertV2Trust(100, truster, trustee, 999999L);

        // Without RebuildSecondaryIndexes, indexes should already be populated
        var trusts = _cache.GetTrustsFor(truster, isV1: false).ToList();
        trusts.Should().ContainSingle(t => t.Trustee == trustee.ToLowerInvariant());

        var trustedBy = _cache.GetTrustedByFor(trustee, isV1: false).ToList();
        trustedBy.Should().ContainSingle(t => t.Truster == truster.ToLowerInvariant());
    }

    // --- Bug 6: Secondary trust index not synced atomically on remove ---

    [Fact]
    public void SecondaryTrustIndex_SyncedOnRemove()
    {
        var truster = "0xaaa0000000000000000000000000000000000000";
        var trustee = "0xbbb0000000000000000000000000000000000000";

        _cache.UpsertV2Trust(100, truster, trustee, 999999L);
        _cache.RemoveV2Trust(101, truster, trustee);

        // Indexes should be cleared without needing RebuildSecondaryIndexes
        _cache.GetTrustsFor(truster, isV1: false).Should().BeEmpty();
        _cache.GetTrustedByFor(trustee, isV1: false).Should().BeEmpty();
    }

    // --- Bug 7: Secondary membership index not synced atomically ---

    [Fact]
    public void SecondaryMembershipIndex_SyncedOnUpsert()
    {
        var group = "0xgroup000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";

        _cache.UpsertGroupMembership(100, group, member, 999999L);

        // Without RebuildSecondaryIndexes, indexes should already be populated
        _cache.GetGroupMembers(group).Should().ContainSingle(m => m.Member == member.ToLowerInvariant());
        _cache.GetMemberGroups(member).Should().ContainSingle(g => g.Group == group.ToLowerInvariant());
    }

    [Fact]
    public void SecondaryMembershipIndex_SyncedOnRemove()
    {
        var group = "0xgroup000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";

        _cache.UpsertGroupMembership(100, group, member, 999999L);
        _cache.RemoveGroupMembership(101, group, member);

        _cache.GetGroupMembers(group).Should().BeEmpty();
        _cache.GetMemberGroups(member).Should().BeEmpty();
    }

    // --- Bug 8: Wrapper reverse index synced on upsert ---

    [Fact]
    public void WrapperIndex_SyncedOnUpsert()
    {
        var wrapper = "0xwrapper0000000000000000000000000000000000";
        var avatar = "0xavatar00000000000000000000000000000000000";

        _cache.UpsertWrapper(100, wrapper, avatar, CirclesType.DemurrageCircles);

        // Without RebuildSecondaryIndexes, wrapper info should be available
        var info = _cache.GetWrapperInfo(wrapper);
        info.Should().NotBeNull();
        info!.Value.Avatar.Should().Be(avatar.ToLowerInvariant());
        info.Value.CirclesType.Should().Be(0);
    }

    // --- Bug 9: Gap processing now reuses NotificationListenerService processor ---
    // This is tested structurally: GapReplayNotificationListener inherits NotificationListenerService
    // Can't unit-test internal sealed class, but we verify the CacheContainer methods it calls work correctly

    [Fact]
    public void GapProcessing_UpsertMethods_UpdateCacheAndIndex_Atomically()
    {
        // Simulate gap replay: insert trust, verify both cache + index updated in one call
        var truster = "0xaaa0000000000000000000000000000000000000";
        var trustee = "0xbbb0000000000000000000000000000000000000";

        _cache.UpsertV1Trust(100, truster, trustee, 42);

        // Primary cache updated
        var key = $"{truster}:{trustee}";
        _cache.V1TrustRelations.TryGetValue(key, out var limit).Should().BeTrue();
        limit.Should().Be(42);

        // Secondary index updated in same call
        _cache.GetTrustsFor(truster, isV1: true).Should().NotBeEmpty();
    }
}
