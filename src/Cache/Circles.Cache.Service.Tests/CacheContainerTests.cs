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
        _cache.V1Avatars.Add(1000, address, ("Human", tokenAddress));

        // Assert
        _cache.V1Avatars.TryGetValue(address, out var info).Should().BeTrue();
        info.Type.Should().Be("Human");
        info.TokenAddress.Should().Be(tokenAddress);
    }

    [Fact]
    public void V1Avatars_Add_ShouldStoreOrganizationWithoutToken()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";

        // Act
        _cache.V1Avatars.Add(1000, address, ("Organization", null));

        // Assert
        _cache.V1Avatars.TryGetValue(address, out var info).Should().BeTrue();
        info.Type.Should().Be("Organization");
        info.TokenAddress.Should().BeNull();
    }

    [Fact]
    public void V2Avatars_Add_ShouldStoreWithTimestamp()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var timestamp = 1704067200L; // 2024-01-01

        // Act
        _cache.V2Avatars.Add(2000, address, ("Human", timestamp));

        // Assert
        _cache.V2Avatars.TryGetValue(address, out var info).Should().BeTrue();
        info.Type.Should().Be("Human");
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
        _cache.V1Avatars.Add(1000, address, ("Human", "0xtoken"));
        _cache.V2Avatars.Add(1000, address, ("Human", 12345L));

        // Add more data at block 2000
        var address2 = "0x1234567890abcdef1234567890abcdef12345678";
        _cache.V1Avatars.Add(2000, address2, ("Organization", null));
        _cache.V2Avatars.Add(2000, address2, ("Organization", 67890L));

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
        _cache.V1Avatars.Add(1000, "0xaddr1", ("Human", "0xtoken1"));
        _cache.V1Avatars.Add(1000, "0xaddr2", ("Human", "0xtoken2"));
        _cache.V2Avatars.Add(2000, "0xaddr3", ("Human", 12345L));

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
}
