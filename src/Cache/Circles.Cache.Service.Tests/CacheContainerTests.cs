using Circles.Cache.Service.Caches;
using Xunit;
using FluentAssertions;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Tests for CacheContainer functionality including rollback and secondary indexes.
/// </summary>
public class CacheContainerTests
{
    private readonly CacheContainer _cache;

    public CacheContainerTests()
    {
        _cache = new CacheContainer(rollbackCapacity: 12);
    }

    [Fact]
    public void AllCaches_ShouldBeInitialized()
    {
        // Assert all caches are created
        _cache.V1Avatars.Should().NotBeNull();
        _cache.V1TokenOwnerByToken.Should().NotBeNull();
        _cache.V1AvatarToCidMap.Should().NotBeNull();
        _cache.V2Avatars.Should().NotBeNull();
        _cache.Erc20WrapperAddresses.Should().NotBeNull();
        _cache.Groups.Should().NotBeNull();
        _cache.GroupMemberships.Should().NotBeNull();
        _cache.V2AvatarToCidMap.Should().NotBeNull();
        _cache.V2AvatarToShortNameMap.Should().NotBeNull();
        _cache.V1BalancesByAccountAndToken.Should().NotBeNull();
        _cache.V2BalancesByAccountAndToken.Should().NotBeNull();
        _cache.V1TrustRelations.Should().NotBeNull();
        _cache.V2TrustRelations.Should().NotBeNull();
    }

    [Fact]
    public void V1Avatars_Add_ShouldStoreHumanWithToken()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var tokenAddress = "0x42cedde51198d1773590311e2a340dc06b24cb37";

        // Act
        _cache.V1Avatars.Add(1000, address, ("CrcV1_Signup", tokenAddress));

        // Assert
        _cache.V1Avatars.TryGetValue(address, out var info).Should().BeTrue();
        info.Type.Should().Be("CrcV1_Signup");
        info.TokenAddress.Should().Be(tokenAddress);
    }

    [Fact]
    public void V1Avatars_Add_ShouldStoreOrganizationWithoutToken()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";

        // Act
        _cache.V1Avatars.Add(1000, address, ("CrcV1_OrganizationSignup", null));

        // Assert
        _cache.V1Avatars.TryGetValue(address, out var info).Should().BeTrue();
        info.Type.Should().Be("CrcV1_OrganizationSignup");
        info.TokenAddress.Should().BeNull();
    }

    [Fact]
    public void V2Avatars_Add_ShouldStoreWithTimestamp()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var timestamp = 1704067200L; // 2024-01-01

        // Act
        _cache.V2Avatars.Add(2000, address, ("CrcV2_RegisterHuman", timestamp));

        // Assert
        _cache.V2Avatars.TryGetValue(address, out var info).Should().BeTrue();
        info.Type.Should().Be("CrcV2_RegisterHuman");
        info.RegisteredAt.Should().Be(timestamp);
    }

    [Fact]
    public void Groups_Add_ShouldStoreGroupInfo()
    {
        // Arrange
        var groupAddress = "0x1234567890abcdef1234567890abcdef12345678";
        var name = "Test Group";
        var mint = "0xmintaddress";
        var symbol = "TG";

        // Act
        _cache.Groups.Add(3000, groupAddress, (name, mint, symbol));

        // Assert
        _cache.Groups.TryGetValue(groupAddress, out var info).Should().BeTrue();
        info.Name.Should().Be(name);
        info.Mint.Should().Be(mint);
        info.Symbol.Should().Be(symbol);
    }

    [Fact]
    public void BalanceIndex_ShouldTrackTokensByAddress()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var tokenId1 = "0xtoken1";
        var tokenId2 = "0xtoken2";

        // Act - Add balances
        _cache.V1BalancesByAccountAndToken.Add(1000, $"{address}:{tokenId1}", 100m);
        _cache.V1BalancesByAccountAndToken.Add(1000, $"{address}:{tokenId2}", 200m);
        _cache.RebuildSecondaryIndexes();

        // Assert
        var tokens = _cache.GetTokenIdsForAddress(address, isV1: true).ToList();
        tokens.Should().HaveCount(2);
        tokens.Should().Contain(tokenId1);
        tokens.Should().Contain(tokenId2);
    }

    [Fact]
    public void BalanceIndex_ShouldRemoveZeroBalances()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var tokenId = "0xtoken1";

        // Act - Add then zero out balance
        _cache.V1BalancesByAccountAndToken.Add(1000, $"{address}:{tokenId}", 100m);
        _cache.RebuildSecondaryIndexes();
        _cache.V1BalancesByAccountAndToken.Add(1001, $"{address}:{tokenId}", 0m);
        _cache.UpdateBalanceIndex($"{address}:{tokenId}", isV1: true, 0m);

        // Assert
        var tokens = _cache.GetTokenIdsForAddress(address, isV1: true).ToList();
        tokens.Should().BeEmpty();
    }

    [Fact]
    public void RollbackAll_ShouldRevertAllCaches()
    {
        // Arrange - Add data at block 1000
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V1Avatars.Add(1000, address, ("CrcV1_Signup", "0xtoken"));
        _cache.V2Avatars.Add(1000, address, ("CrcV2_RegisterHuman", 12345L));

        // Add more data at block 2000
        var address2 = "0x1234567890abcdef1234567890abcdef12345678";
        _cache.V1Avatars.Add(2000, address2, ("CrcV1_OrganizationSignup", null));
        _cache.V2Avatars.Add(2000, address2, ("CrcV2_RegisterOrganization", 67890L));

        // Act - Rollback to block 1500
        _cache.RollbackAll(1500);

        // Assert - Block 1000 data should remain
        _cache.V1Avatars.TryGetValue(address, out _).Should().BeTrue();
        _cache.V2Avatars.TryGetValue(address, out _).Should().BeTrue();

        // Block 2000 data should be removed
        _cache.V1Avatars.TryGetValue(address2, out _).Should().BeFalse();
        _cache.V2Avatars.TryGetValue(address2, out _).Should().BeFalse();
    }

    [Fact]
    public void GetStatistics_ShouldReturnCorrectCounts()
    {
        // Arrange
        _cache.V1Avatars.Add(1000, "0xaddr1", ("CrcV1_Signup", "0xtoken1"));
        _cache.V1Avatars.Add(1000, "0xaddr2", ("CrcV1_Signup", "0xtoken2"));
        _cache.V2Avatars.Add(2000, "0xaddr3", ("CrcV2_RegisterHuman", 12345L));

        // Act
        var stats = _cache.GetStatistics();

        // Assert
        stats["v1_avatars"].Should().Be(2);
        stats["v2_avatars"].Should().Be(1);
        ((int)stats["total_entries"]).Should().BeGreaterOrEqualTo(3);
    }

    [Fact]
    public void TrustRelations_ShouldUseCompositeKey()
    {
        // Arrange
        var truster = "0xtruster";
        var trustee = "0xtrustee";
        var key = $"{truster}:{trustee}";
        var expiryTime = 1735689600L;

        // Act
        _cache.V2TrustRelations.Add(1000, key, expiryTime);

        // Assert
        _cache.V2TrustRelations.TryGetValue(key, out var expiry).Should().BeTrue();
        expiry.Should().Be(expiryTime);
    }

    [Fact]
    public void GroupMemberships_ShouldUseCompositeKey()
    {
        // Arrange
        var group = "0xgroup";
        var member = "0xmember";
        var key = $"{group}:{member}";
        var expiryTime = 1735689600L;

        // Act
        _cache.GroupMemberships.Add(1000, key, (member, expiryTime));

        // Assert
        _cache.GroupMemberships.TryGetValue(key, out var info).Should().BeTrue();
        info.Member.Should().Be(member);
        info.ExpiryTime.Should().Be(expiryTime);
    }

    // --- Phase 2: Secondary Index Consistency Tests ---

    [Fact]
    public void V2BalanceIndex_TracksByAddress()
    {
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var token1 = "0xtoken1";
        var token2 = "0xtoken2";

        _cache.V2BalancesByAccountAndToken.Add(1000, $"{address}:{token1}", 100m);
        _cache.V2BalancesByAccountAndToken.Add(1000, $"{address}:{token2}", 200m);
        _cache.RebuildSecondaryIndexes();

        var tokens = _cache.GetTokenIdsForAddress(address, isV1: false).ToList();
        tokens.Should().HaveCount(2);
        tokens.Should().Contain(token1);
        tokens.Should().Contain(token2);
    }

    [Fact]
    public void TrustIndex_V1_TracksByTruster()
    {
        var truster = "0xaaa0000000000000000000000000000000000000";
        var trustee1 = "0xbbb0000000000000000000000000000000000000";
        var trustee2 = "0xccc0000000000000000000000000000000000000";

        _cache.V1TrustRelations.Add(100, $"{truster}:{trustee1}", 50L);
        _cache.V1TrustRelations.Add(100, $"{truster}:{trustee2}", 75L);
        _cache.RebuildSecondaryIndexes();

        var trusts = _cache.GetTrustsFor(truster, isV1: true).ToList();
        trusts.Should().HaveCount(2);
        trusts.Select(t => t.Trustee).Should().Contain(trustee1);
        trusts.Select(t => t.Trustee).Should().Contain(trustee2);
    }

    [Fact]
    public void TrustIndex_V1_TracksByTrustee()
    {
        var truster1 = "0xaaa0000000000000000000000000000000000000";
        var truster2 = "0xbbb0000000000000000000000000000000000000";
        var trustee = "0xccc0000000000000000000000000000000000000";

        _cache.V1TrustRelations.Add(100, $"{truster1}:{trustee}", 50L);
        _cache.V1TrustRelations.Add(100, $"{truster2}:{trustee}", 75L);
        _cache.RebuildSecondaryIndexes();

        var trustedBy = _cache.GetTrustedByFor(trustee, isV1: true).ToList();
        trustedBy.Should().HaveCount(2);
        trustedBy.Select(t => t.Truster).Should().Contain(truster1);
        trustedBy.Select(t => t.Truster).Should().Contain(truster2);
    }

    [Fact]
    public void TrustIndex_V2_TracksByTruster()
    {
        var truster = "0xaaa0000000000000000000000000000000000000";
        var trustee = "0xbbb0000000000000000000000000000000000000";

        _cache.V2TrustRelations.Add(100, $"{truster}:{trustee}", 999999L);
        _cache.RebuildSecondaryIndexes();

        _cache.GetTrustsFor(truster, isV1: false).Should().HaveCount(1);
    }

    [Fact]
    public void TrustIndex_V2_TracksByTrustee()
    {
        var truster = "0xaaa0000000000000000000000000000000000000";
        var trustee = "0xbbb0000000000000000000000000000000000000";

        _cache.V2TrustRelations.Add(100, $"{truster}:{trustee}", 999999L);
        _cache.RebuildSecondaryIndexes();

        _cache.GetTrustedByFor(trustee, isV1: false).Should().HaveCount(1);
    }

    [Fact]
    public void MembershipIndex_TracksByGroup()
    {
        var group = "0xgroup000000000000000000000000000000000000";
        var member1 = "0xmember1000000000000000000000000000000000";
        var member2 = "0xmember2000000000000000000000000000000000";

        _cache.GroupMemberships.Add(100, $"{group}:{member1}", (member1, 999999L));
        _cache.GroupMemberships.Add(100, $"{group}:{member2}", (member2, 888888L));
        _cache.RebuildSecondaryIndexes();

        var members = _cache.GetGroupMembers(group).ToList();
        members.Should().HaveCount(2);
        members.Select(m => m.Member).Should().Contain(member1);
        members.Select(m => m.Member).Should().Contain(member2);
    }

    [Fact]
    public void MembershipIndex_TracksByMember()
    {
        var group1 = "0xgroup1000000000000000000000000000000000000";
        var group2 = "0xgroup2000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";

        _cache.GroupMemberships.Add(100, $"{group1}:{member}", (member, 999999L));
        _cache.GroupMemberships.Add(100, $"{group2}:{member}", (member, 888888L));
        _cache.RebuildSecondaryIndexes();

        var groups = _cache.GetMemberGroups(member).ToList();
        groups.Should().HaveCount(2);
        groups.Select(g => g.Group).Should().Contain(group1);
        groups.Select(g => g.Group).Should().Contain(group2);
    }

    [Fact]
    public void WrapperReverseIndex_MapsWrapperToAvatar()
    {
        var wrapper = "0xwrapper0000000000000000000000000000000000";
        var avatar = "0xavatar00000000000000000000000000000000000";

        _cache.Erc20WrapperAddresses.Add(100, wrapper, (avatar, 0));
        _cache.RebuildSecondaryIndexes();

        var info = _cache.GetWrapperInfo(wrapper);
        info.Should().NotBeNull();
        info!.Value.Avatar.Should().Be(avatar);
        info.Value.CirclesType.Should().Be(0);
    }

    [Fact]
    public void WrapperReverseIndex_NullForUnknown()
    {
        _cache.GetWrapperInfo("0xnonexistent0000000000000000000000000000").Should().BeNull();
        _cache.GetAvatarForWrapper("0xnonexistent0000000000000000000000000000").Should().BeNull();
    }

    [Fact]
    public void RollbackAll_ThenRebuild_RestoresIndexConsistency()
    {
        var addr = "0xholder00000000000000000000000000000000000";
        var token1 = "0xtoken1";
        var token2 = "0xtoken2";

        // Block 100
        _cache.V2BalancesByAccountAndToken.Add(100, $"{addr}:{token1}", 100m);
        _cache.RebuildSecondaryIndexes();

        // Block 101
        _cache.V2BalancesByAccountAndToken.Add(101, $"{addr}:{token2}", 200m);
        _cache.RebuildSecondaryIndexes();
        _cache.GetTokenIdsForAddress(addr, isV1: false).Should().HaveCount(2);

        // Rollback to block 100
        _cache.RollbackAll(101);
        _cache.RebuildSecondaryIndexes();

        _cache.GetTokenIdsForAddress(addr, isV1: false).Should().HaveCount(1);
        _cache.GetTokenIdsForAddress(addr, isV1: false).Should().Contain(token1);
    }

    [Fact]
    public void V2LastActivity_RolledBackWithBalance()
    {
        var key = "0xaddr:0xtoken";

        // Block 100 - initial activity
        _cache.V2BalancesByAccountAndToken.Add(100, key, 100m);
        _cache.V2LastActivity.Add(100, key, 1000L);

        // Block 101 - update
        _cache.V2BalancesByAccountAndToken.Add(101, key, 200m);
        _cache.V2LastActivity.Add(101, key, 2000L);

        // Rollback block 101
        _cache.RollbackAll(101);

        // V2LastActivity (now a RollbackCache) should be rolled back too
        _cache.V2LastActivity.TryGetValue(key, out var restoredTs).Should().BeTrue();
        restoredTs.Should().Be(1000L);
    }

    [Fact]
    public void MultipleTokens_SameAddress_AllTracked()
    {
        var address = "0xholder00000000000000000000000000000000000";

        // Add 5 different V2 tokens for the same address
        for (int i = 1; i <= 5; i++)
        {
            var token = $"0xtoken{i:d38}";
            _cache.V2BalancesByAccountAndToken.Add(100, $"{address}:{token}", i * 100m);
        }
        _cache.RebuildSecondaryIndexes();

        var tokens = _cache.GetTokenIdsForAddress(address, isV1: false).ToList();
        tokens.Should().HaveCount(5);
    }
}
