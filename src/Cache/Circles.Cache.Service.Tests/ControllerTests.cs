using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Controllers;
using Circles.Cache.Service.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using FluentAssertions;
using System.Net;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Tests for API controllers - validates HTTP endpoints return correct data.
/// </summary>
public class ControllerTests
{
    private readonly CacheContainer _cache;
    private readonly CacheServiceState _state;
    private readonly IpfsContentCache _ipfsCache;

    public ControllerTests()
    {
        _cache = new CacheContainer(rollbackCapacity: 12);
        _state = new CacheServiceState(rollbackCapacity: 12);
        _state.LastProcessedBlock = 1000;
        _state.WarmupComplete = true;
        _state.ListenerConnected = true;

        // Create IpfsContentCache with dummy connection string (won't be used in tests)
        var ipfsLogger = new Mock<ILogger<IpfsContentCache>>();
        _ipfsCache = new IpfsContentCache("Host=localhost;Database=test", maxEntries: 100, ipfsLogger.Object);
    }

    /// <summary>
    /// Creates a mock HttpContext with a remote IP address for controller tests.
    /// </summary>
    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        return context;
    }

    /// <summary>
    /// Creates a BalancesController with mocked HttpContext.
    /// </summary>
    private BalancesController CreateBalancesController()
    {
        var logger = new Mock<ILogger<BalancesController>>();
        var controller = new BalancesController(_cache, _state, logger.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() };
        return controller;
    }

    /// <summary>
    /// Creates an AvatarsController with mocked HttpContext.
    /// </summary>
    private AvatarsController CreateAvatarsController()
    {
        var logger = new Mock<ILogger<AvatarsController>>();
        var controller = new AvatarsController(_cache, _state, logger.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() };
        return controller;
    }

    /// <summary>
    /// Creates a TrustRelationsController with mocked HttpContext.
    /// </summary>
    private TrustRelationsController CreateTrustRelationsController()
    {
        var logger = new Mock<ILogger<TrustRelationsController>>();
        var controller = new TrustRelationsController(_cache, _state, logger.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() };
        return controller;
    }

    /// <summary>
    /// Creates a ProfilesController with mocked HttpContext.
    /// </summary>
    private ProfilesController CreateProfilesController()
    {
        var logger = new Mock<ILogger<ProfilesController>>();
        var controller = new ProfilesController(_cache, _ipfsCache, _state, logger.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() };
        return controller;
    }

    /// <summary>
    /// Creates a GroupMembershipsController with mocked HttpContext.
    /// </summary>
    private GroupMembershipsController CreateGroupMembershipsController()
    {
        var logger = new Mock<ILogger<GroupMembershipsController>>();
        var controller = new GroupMembershipsController(_cache, _state, logger.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() };
        return controller;
    }

    private PathfinderGraphController CreatePathfinderGraphController()
    {
        var logger = new Mock<ILogger<PathfinderGraphController>>();
        var settings = new CacheServiceSettings
        {
            PostgresConnectionString = "Host=localhost;Database=test;Username=test;Password=test"
        };
        var controller = new PathfinderGraphController(_state, settings, logger.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() };
        return controller;
    }

    #region BalancesController Tests

    [Fact]
    public void GetTokenBalances_ShouldReturnEmpty_WhenNoBalances()
    {
        // Arrange
        var controller = CreateBalancesController();

        // Act
        var result = controller.GetTokenBalances("0xde374ece6fa50e781e81aac78e811b33d16912c7");

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var balances = okResult!.Value as TokenBalanceResponse[];
        balances.Should().NotBeNull();
        balances!.Length.Should().Be(0);
    }

    [Fact]
    public void GetTokenBalances_ShouldReturnV1Balances()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var tokenId = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        var balance = 150.25m;

        _cache.V1BalancesByAccountAndToken.Add(1000, $"{address}:{tokenId}", balance);
        _cache.V1TokenOwnerByToken.Add(1000, tokenId, address);
        _cache.RebuildSecondaryIndexes();

        var controller = CreateBalancesController();

        // Act
        var result = controller.GetTokenBalances(address);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var balances = okResult!.Value as TokenBalanceResponse[];
        balances.Should().NotBeNull();
        balances!.Length.Should().Be(1);
        balances[0].TokenId.Should().Be(tokenId);
        balances[0].Balance.Should().Be("150.25");
        balances[0].Version.Should().Be(1);
        balances[0].TokenOwner.Should().Be(address);
    }

    [Fact]
    public void GetTokenBalances_ShouldReturnV2Balances()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var tokenId = "123456789012345678901234567890";
        var balance = 1250.75m;

        _cache.V2BalancesByAccountAndToken.Add(2000, $"{address}:{tokenId}", balance);
        _cache.RebuildSecondaryIndexes();

        var controller = CreateBalancesController();

        // Act
        var result = controller.GetTokenBalances(address);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var balances = okResult!.Value as TokenBalanceResponse[];
        balances.Should().NotBeNull();
        balances!.Length.Should().Be(1);
        balances[0].TokenId.Should().Be(tokenId);
        balances[0].Balance.Should().Be("1250.75");
        balances[0].Version.Should().Be(2);
        // V2 token owner is derived from tokenId (converted to address)
        balances[0].TokenOwner.Should().NotBeNull();
    }

    [Fact]
    public void GetTokenBalances_ShouldReject_InvalidAddress()
    {
        // Arrange
        var controller = CreateBalancesController();

        // Act
        var result = controller.GetTokenBalances("invalid-address");

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void GetTotalBalance_ShouldSumAllVersions()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V1BalancesByAccountAndToken.Add(1000, $"{address}:0xtoken1", 100m); // V1 CRC (will be converted)
        _cache.V2BalancesByAccountAndToken.Add(2000, $"{address}:12345", 200m);    // V2 already in Circles
        _cache.RebuildSecondaryIndexes();

        var controller = CreateBalancesController();

        // Act
        var result = controller.GetTotalBalance(address);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var response = okResult!.Value as TotalBalanceResponse;
        response.Should().NotBeNull();
        // V1 balance is converted from CRC to Circles (approx 2.1x inflation factor)
        // V2 balance remains 200, V1 100 CRC becomes ~211 Circles
        // Total should be > 300 (would be 300 if no conversion applied)
        var balance = decimal.Parse(response!.Balance);
        balance.Should().BeGreaterThan(300m, "V1 CRC should be converted to time-circles with inflation factor > 1");
        balance.Should().BeLessThan(450m, "Converted balance should be within reasonable range (200 V2 + ~211-250 V1)");
    }

    [Fact]
    public void GetTotalBalanceV1_ShouldOnlySumV1WithCrcConversion()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V1BalancesByAccountAndToken.Add(1000, $"{address}:0xtoken1", 100m); // V1 CRC (will be converted)
        _cache.V1BalancesByAccountAndToken.Add(1000, $"{address}:0xtoken2", 50m);  // V1 CRC (will be converted)
        _cache.V2BalancesByAccountAndToken.Add(2000, $"{address}:12345", 200m);    // Should NOT be included
        _cache.RebuildSecondaryIndexes();

        var controller = CreateBalancesController();

        // Act
        var result = controller.GetTotalBalanceV1(address);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var response = okResult!.Value as TotalBalanceResponse;
        response.Should().NotBeNull();
        // Raw CRC sum is 150, but CRC→Circles conversion applies ~2.1x inflation factor
        // V2 balance (200) should NOT be included
        var balance = decimal.Parse(response!.Balance);
        balance.Should().BeGreaterThan(150m, "V1 CRC should be converted to time-circles with inflation factor > 1");
        balance.Should().BeLessThan(350m, "Converted balance should be within reasonable range (~316 for 150 CRC)");
    }

    #endregion

    #region PathfinderGraphController Tests

    [Fact]
    public async Task PathfinderGraph_ShouldReturn503_WhenCacheNotReady()
    {
        _state.WarmupComplete = false;
        _state.ListenerConnected = false;

        var controller = CreatePathfinderGraphController();
        var result = await controller.GetGraph();

        var objectResult = result as ObjectResult;
        objectResult.Should().NotBeNull();
        objectResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task PathfinderGraph_ShouldReturnBadRequest_ForInvalidFormat()
    {
        var controller = CreatePathfinderGraphController();
        var result = await controller.GetGraph(format: "protobuf");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PathfinderGraph_ShouldReturnBadRequest_ForInvalidInclude()
    {
        var controller = CreatePathfinderGraphController();
        var result = await controller.GetGraph(include: "balances,unknown");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PathfinderGraph_ShouldReturn304_WhenEtagMatches()
    {
        _state.LastProcessedBlock = 4242;

        var controller = CreatePathfinderGraphController();
        controller.HttpContext.Request.Headers.IfNoneMatch = "\"pf-graph-v1-4242-balances,consentedFlow,groupTrusts,groups,trust\"";

        var result = await controller.GetGraph();

        var statusCodeResult = result as StatusCodeResult;
        statusCodeResult.Should().NotBeNull();
        statusCodeResult!.StatusCode.Should().Be(StatusCodes.Status304NotModified);
    }

    #endregion

    #region AvatarsController Tests

    [Fact]
    public void GetAvatarInfo_ShouldReturnV2Avatar()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V2Avatars.Add(2000, address, ("Human", 1704067200L));
        _cache.V2AvatarToCidMap.Add(2000, address, "QmYxivS5DXZgDUgLE8YTZV9AnFKPSLvd5R5sWEyWAJKXWE");

        var controller = CreateAvatarsController();

        // Act
        var result = controller.GetAvatarInfo(address);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var avatar = okResult!.Value as AvatarInfoResponse;
        avatar.Should().NotBeNull();
        avatar!.Version.Should().Be(2);
        avatar.Type.Should().Be("Human");
        avatar.IsHuman.Should().BeTrue();
        avatar.CidV0.Should().Be("QmYxivS5DXZgDUgLE8YTZV9AnFKPSLvd5R5sWEyWAJKXWE");
        avatar.RegisteredAt.Should().Be(1704067200L);
    }

    [Fact]
    public void GetAvatarInfo_ShouldReturnV1Avatar_WhenNoV2()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var token = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        _cache.V1Avatars.Add(1000, address, ("Human", token));

        var controller = CreateAvatarsController();

        // Act
        var result = controller.GetAvatarInfo(address);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var avatar = okResult!.Value as AvatarInfoResponse;
        avatar.Should().NotBeNull();
        avatar!.Version.Should().Be(1);
        avatar.Type.Should().Be("Human");
        avatar.HasV1.Should().BeTrue();
        avatar.V1Token.Should().Be(token);
        avatar.TokenId.Should().Be(token);
    }

    [Fact]
    public void GetAvatarInfo_ShouldReturn404_WhenNotFound()
    {
        // Arrange
        var controller = CreateAvatarsController();

        // Act
        var result = controller.GetAvatarInfo("0xde374ece6fa50e781e81aac78e811b33d16912c7");

        // Assert
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void GetAvatarInfo_ShouldReturnGroupInfo_ForV2Groups()
    {
        // Arrange
        var address = "0x1234567890abcdef1234567890abcdef12345678";
        _cache.V2Avatars.Add(2000, address, ("Group", 1704067200L));
        _cache.Groups.Add(2000, address, ("Community DAO", "0xmint", "CDAO"));

        var controller = CreateAvatarsController();

        // Act
        var result = controller.GetAvatarInfo(address);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var avatar = okResult!.Value as AvatarInfoResponse;
        avatar.Should().NotBeNull();
        avatar!.Type.Should().Be("Group");
        avatar.Name.Should().Be("Community DAO");
        avatar.Symbol.Should().Be("CDAO");
        avatar.IsHuman.Should().BeFalse();
    }

    [Fact]
    public void GetAvatarInfoBatch_ShouldReturnMultipleResults()
    {
        // Arrange
        var addr1 = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var addr2 = "0x1234567890abcdef1234567890abcdef12345678";
        var addr3 = "0xnonexistent0000000000000000000000000000";

        _cache.V2Avatars.Add(2000, addr1, ("Human", 1704067200L));
        _cache.V1Avatars.Add(1000, addr2, ("Organization", null));

        var controller = CreateAvatarsController();

        // Act
        var request = new AvatarInfoBatchRequest(new[] { addr1, addr2, addr3 });
        var result = controller.GetAvatarInfoBatch(request);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var avatars = okResult!.Value as AvatarInfoResponse?[];
        avatars.Should().NotBeNull();
        avatars!.Length.Should().Be(3);
        avatars[0].Should().NotBeNull();
        avatars[0]!.Version.Should().Be(2);
        avatars[1].Should().NotBeNull();
        avatars[1]!.Version.Should().Be(1);
        avatars[2].Should().BeNull(); // Not found
    }

    [Fact]
    public void GetAvatarInfoBatch_ShouldReject_TooManyAddresses()
    {
        // Arrange
        var controller = CreateAvatarsController();
        var addresses = Enumerable.Range(0, 101).Select(i => $"0x{i:X40}").ToArray();

        // Act
        var request = new AvatarInfoBatchRequest(addresses);
        var result = controller.GetAvatarInfoBatch(request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region ProfilesController Tests

    [Fact]
    public void GetProfileCid_ShouldPreferV2OverV1()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V1AvatarToCidMap.Add(1000, address, "cid-v1");
        _cache.V2AvatarToCidMap.Add(2000, address, "cid-v2");
        var controller = CreateProfilesController();

        // Act
        var result = controller.GetProfileCid(address);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var response = Assert.IsType<ProfileCidResponse>(okResult!.Value);
        response.Cid.Should().Be("cid-v2");
    }

    [Fact]
    public void GetProfileCid_ShouldReturnNullWhenMissing()
    {
        // Arrange
        var controller = CreateProfilesController();

        // Act
        var result = controller.GetProfileCid("0xde374ece6fa50e781e81aac78e811b33d16912c7");

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var response = Assert.IsType<ProfileCidResponse>(okResult!.Value);
        response.Cid.Should().BeNull();
    }

    [Fact]
    public void GetProfileCidBatch_ShouldReturnMixedResults()
    {
        // Arrange
        var addr1 = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var addr2 = "0x1234567890abcdef1234567890abcdef12345678";
        var addr3 = "0xfedcba9876543210fedcba9876543210fedcba98";
        _cache.V2AvatarToCidMap.Add(2000, addr1, "cid-v2");
        _cache.V1AvatarToCidMap.Add(1500, addr2, "cid-v1");
        var controller = CreateProfilesController();

        // Act
        var request = new ProfileCidBatchRequest(new[] { addr1, addr2, addr3 });
        var result = controller.GetProfileCidBatch(request);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var responses = Assert.IsType<ProfileCidResponse[]>(okResult!.Value);
        responses.Should().HaveCount(3);
        responses[0].Cid.Should().Be("cid-v2");
        responses[1].Cid.Should().Be("cid-v1");
        responses[2].Cid.Should().BeNull();
    }

    [Fact]
    public void GetProfileCidBatch_ShouldReject_TooManyAddresses()
    {
        // Arrange
        var controller = CreateProfilesController();
        var addresses = Enumerable.Range(0, 150).Select(i => $"0x{i:X40}").ToArray();

        // Act
        var request = new ProfileCidBatchRequest(addresses);
        var result = controller.GetProfileCidBatch(request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region GroupMembershipsController Tests

    [Fact]
    public void GetGroupMembers_ShouldReturnMemberships()
    {
        // Arrange
        var group = "0xgroup000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";
        var key = $"{group}:{member}";
        _cache.GroupMemberships.Add(1200, key, (member, 1735689600L));
        _cache.RebuildSecondaryIndexes();
        var controller = CreateGroupMembershipsController();

        // Act
        var result = controller.GetGroupMembers(group);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var response = Assert.IsType<GroupMembersResponse>(okResult!.Value);
        response.Members.Should().HaveCount(1);
        response.Members[0].Member.Should().Be(member);
        response.Members[0].ExpiryTime.Should().Be(1735689600L);
    }

    [Fact]
    public void GetMemberGroups_ShouldReturnMemberships()
    {
        // Arrange
        var group = "0xgroup000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";
        var key = $"{group}:{member}";
        _cache.GroupMemberships.Add(1200, key, (member, 1735689600L));
        _cache.RebuildSecondaryIndexes();
        var controller = CreateGroupMembershipsController();

        // Act
        var result = controller.GetMemberGroups(member);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var response = Assert.IsType<MemberGroupsResponse>(okResult!.Value);
        response.Groups.Should().HaveCount(1);
        response.Groups[0].Group.Should().Be(group.ToLowerInvariant());
        response.Groups[0].Member.Should().Be(member.ToLowerInvariant());
    }

    #endregion

    #region TrustRelationsController Tests

    [Fact]
    public void GetTrustRelations_ShouldReturnBothDirections()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var v1Trustee = "0xtrustee0000000000000000000000000000000000";
        var v2Trustee = "0xtrustee2000000000000000000000000000000000";
        _cache.V1TrustRelations.Add(1000, $"{address}:{v1Trustee}", 42L);
        _cache.V2TrustRelations.Add(2000, $"{address}:{v2Trustee}", 1735689600L);
        _cache.V1TrustRelations.Add(1100, $"0xanother:{address}", 15L);
        _cache.RebuildSecondaryIndexes();
        var controller = CreateTrustRelationsController();

        // Act
        var result = controller.GetTrustRelations(address);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var response = okResult!.Value as TrustRelationsResponse;
        response.Should().NotBeNull();
        response!.Trusts.Should().HaveCount(2);
        response.TrustedBy.Should().HaveCount(1);
        response.Trusts.Should().ContainSingle(t => t.Version == 1 && t.Trustee == v1Trustee);
        response.Trusts.Should().ContainSingle(t => t.Version == 2 && t.Trustee == v2Trustee);
        response.TrustedBy[0].Truster.Should().Be("0xanother");
        response.TrustedBy[0].Version.Should().Be(1);
    }

    [Fact]
    public void GetTrustRelations_ShouldFilterByVersion()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V1TrustRelations.Add(1000, $"{address}:0xv1trust", 42L);
        _cache.V2TrustRelations.Add(2000, $"{address}:0xv2trust", 1000L);
        _cache.RebuildSecondaryIndexes();
        var controller = CreateTrustRelationsController();

        // Act
        var result = controller.GetTrustRelations(address, version: 1);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var response = okResult!.Value as TrustRelationsResponse;
        response.Should().NotBeNull();
        response!.Trusts.Should().HaveCount(1);
        response.Trusts[0].Version.Should().Be(1);
        response.Trusts[0].Trustee.Should().Be("0xv1trust");
    }

    #endregion
}
