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
using Npgsql;

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
        var dummyDataSource = NpgsqlDataSource.Create("Host=localhost;Database=test");
        _ipfsCache = new IpfsContentCache(dummyDataSource, maxEntries: 100, ipfsLogger.Object);
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
        // Arrange — use hex addresses so registration works (tokenId IS the token owner)
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var tokenId = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        var balance = 1250.75m;

        _cache.V2Avatars.Add(1000, address, ("Human", 1000));
        _cache.V2Avatars.Add(1000, tokenId, ("Human", 1000));
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
        var v2TokenOwner = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        _cache.V2Avatars.Add(1000, address, ("Human", 1000));
        _cache.V2Avatars.Add(1000, v2TokenOwner, ("Human", 1000));
        _cache.V1BalancesByAccountAndToken.Add(1000, $"{address}:0xtoken1", 100m); // V1 CRC (will be converted)
        _cache.V2BalancesByAccountAndToken.Add(2000, $"{address}:{v2TokenOwner}", 200m); // V2 already in Circles
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

    #region AvatarsController Tests

    [Fact]
    public void GetAvatarInfo_ShouldReturnV2Avatar()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V2Avatars.Add(2000, address, ("CrcV2_RegisterHuman", 1704067200L));
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
        avatar.Type.Should().Be("CrcV2_RegisterHuman");
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
        _cache.V1Avatars.Add(1000, address, ("CrcV1_Signup", token));

        var controller = CreateAvatarsController();

        // Act
        var result = controller.GetAvatarInfo(address);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var avatar = okResult!.Value as AvatarInfoResponse;
        avatar.Should().NotBeNull();
        avatar!.Version.Should().Be(1);
        avatar.Type.Should().Be("CrcV1_Signup");
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
        _cache.V2Avatars.Add(2000, address, ("CrcV2_RegisterGroup", 1704067200L));
        _cache.Groups.Add(2000, address, ("Community DAO", "0xmint", "CDAO"));

        var controller = CreateAvatarsController();

        // Act
        var result = controller.GetAvatarInfo(address);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var avatar = okResult!.Value as AvatarInfoResponse;
        avatar.Should().NotBeNull();
        avatar!.Type.Should().Be("CrcV2_RegisterGroup");
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

        _cache.V2Avatars.Add(2000, addr1, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V1Avatars.Add(1000, addr2, ("CrcV1_OrganizationSignup", null));

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
        _cache.Groups.Add(1000, group, ("TestGroup", "0xmint", "TG"));
        _cache.V2Avatars.Add(1000, member, ("Human", 1000));
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
        _cache.Groups.Add(1000, group, ("TestGroup", "0xmint", "TG"));
        _cache.V2Avatars.Add(1000, member, ("Human", 1000));
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

    #region TokensController Tests

    /// <summary>
    /// Creates a TokensController with mocked HttpContext.
    /// </summary>
    private TokensController CreateTokensController()
    {
        var logger = new Mock<ILogger<TokensController>>();
        var controller = new TokensController(_cache, _state, logger.Object);
        controller.ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() };
        return controller;
    }

    [Fact]
    public void TokensController_V1Token_ReturnsInfo()
    {
        var tokenAddr = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        var owner = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V1TokenOwnerByToken.Add(1000, tokenAddr, owner);

        var controller = CreateTokensController();
        var result = controller.GetTokenInfo(tokenAddr);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var info = okResult!.Value as TokenInfoResponse;
        info.Should().NotBeNull();
        info!.Version.Should().Be(1);
        info.TokenType.Should().Be("CrcV1_Signup");
        info.TokenOwner.Should().Be(owner);
        info.IsErc20.Should().BeTrue();
        info.IsErc1155.Should().BeFalse();
    }

    [Fact]
    public void TokensController_V2Avatar_ReturnsInfo()
    {
        var addr = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V2Avatars.Add(2000, addr, ("CrcV2_RegisterHuman", 1704067200L));

        var controller = CreateTokensController();
        var result = controller.GetTokenInfo(addr);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var info = okResult!.Value as TokenInfoResponse;
        info.Should().NotBeNull();
        info!.Version.Should().Be(2);
        info.TokenType.Should().Be("CrcV2_RegisterHuman");
        info.IsErc1155.Should().BeTrue();
        info.IsGroup.Should().BeFalse();
    }

    [Fact]
    public void TokensController_ERC20Wrapper_ReturnsInfo()
    {
        var wrapper = "0xwrapper0000000000000000000000000000000000";
        var avatar = "0xavatar00000000000000000000000000000000000";
        _cache.UpsertWrapper(2000, wrapper, avatar, 1); // inflationary

        var controller = CreateTokensController();
        var result = controller.GetTokenInfo(wrapper);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var info = okResult!.Value as TokenInfoResponse;
        info.Should().NotBeNull();
        info!.Version.Should().Be(2);
        info.IsWrapped.Should().BeTrue();
        info.IsErc20.Should().BeTrue();
        info.IsInflationary.Should().BeTrue();
        info.TokenType.Should().Contain("Inflationary");
        info.TokenOwner.Should().Be(avatar.ToLowerInvariant());
    }

    [Fact]
    public void TokensController_Group_ReturnsInfo()
    {
        var groupAddr = "0xgroup000000000000000000000000000000000000";
        _cache.Groups.Add(2000, groupAddr, ("MyGroup", "0xmint", "MG"));

        var controller = CreateTokensController();
        var result = controller.GetTokenInfo(groupAddr);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var info = okResult!.Value as TokenInfoResponse;
        info.Should().NotBeNull();
        info!.Version.Should().Be(2);
        info.TokenType.Should().Be("CrcV2_RegisterGroup");
        info.IsGroup.Should().BeTrue();
        info.IsErc1155.Should().BeTrue();
    }

    [Fact]
    public void TokensController_NotFound_Returns404()
    {
        var controller = CreateTokensController();
        var result = controller.GetTokenInfo("0xnonexistent0000000000000000000000000000");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void TokensController_Batch_ReturnsMultiple()
    {
        var v1Token = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        var v2Addr = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var unknown = "0xnonexistent0000000000000000000000000000";

        _cache.V1TokenOwnerByToken.Add(1000, v1Token, "0xowner");
        _cache.V2Avatars.Add(2000, v2Addr, ("CrcV2_RegisterHuman", 12345L));

        var controller = CreateTokensController();
        var request = new TokenInfoBatchRequest(new[] { v1Token, v2Addr, unknown });
        var result = controller.GetTokenInfoBatch(request);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var infos = okResult!.Value as TokenInfoResponse?[];
        infos.Should().NotBeNull();
        infos!.Length.Should().Be(3);
        infos[0].Should().NotBeNull();
        infos[0]!.Version.Should().Be(1);
        infos[1].Should().NotBeNull();
        infos[1]!.Version.Should().Be(2);
        infos[2].Should().BeNull();
    }

    [Fact]
    public void TokensController_BatchLimit_Rejects()
    {
        var controller = CreateTokensController();
        var addresses = Enumerable.Range(0, 1001).Select(i => $"0x{i:X40}").ToArray();

        var request = new TokenInfoBatchRequest(addresses);
        var result = controller.GetTokenInfoBatch(request);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void BalancesController_V2ERC20Wrapper_IdentifiedCorrectly()
    {
        // Guards the old StartsWith("0x") bug: both ERC1155 and wrapper tokens
        // can have hex addresses as keys. The fix uses GetWrapperInfo() lookup instead.
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var wrapperAddr = "0xwrapper0000000000000000000000000000000000";
        var erc1155Id = "0xavatar00000000000000000000000000000000000";

        // Register account, wrapper underlying, and ERC1155 token owner
        _cache.V2Avatars.Add(2000, address, ("CrcV2_RegisterHuman", 12345L));
        _cache.V2Avatars.Add(2000, "0xunderlying", ("CrcV2_RegisterHuman", 12345L));
        _cache.UpsertWrapper(2000, wrapperAddr, "0xunderlying", 0); // demurraged wrapper
        _cache.V2Avatars.Add(2000, erc1155Id, ("CrcV2_RegisterHuman", 12345L));

        // Add balances for both — both are hex addresses starting with "0x"
        _cache.V2BalancesByAccountAndToken.Add(2000, $"{address}:{wrapperAddr}", 100m);
        _cache.V2BalancesByAccountAndToken.Add(2000, $"{address}:{erc1155Id}", 200m);
        _cache.RebuildSecondaryIndexes();

        var controller = CreateBalancesController();
        var result = controller.GetTokenBalances(address);

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var balances = okResult!.Value as TokenBalanceResponse[];
        balances.Should().NotBeNull();
        balances!.Length.Should().Be(2);

        // One should be wrapped ERC20, the other ERC1155
        var wrapper = balances.SingleOrDefault(b => b.TokenId == wrapperAddr);
        wrapper.Should().NotBeNull();
        wrapper!.IsWrapped.Should().BeTrue();
        wrapper.IsErc20.Should().BeTrue();

        var erc1155 = balances.SingleOrDefault(b => b.TokenId == erc1155Id);
        erc1155.Should().NotBeNull();
        erc1155!.IsWrapped.Should().BeFalse();
        erc1155.IsErc1155.Should().BeTrue();
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
        // Register V2 addresses for registration filter
        _cache.V2Avatars.Add(1000, address, ("Human", 1000));
        _cache.V2Avatars.Add(1000, v2Trustee, ("Human", 1000));
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

    [Fact]
    public void GetTrustRelations_ShouldFilterExpiredV2ByCurrentBlockTimestamp()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        // Register all V2 addresses
        _cache.V2Avatars.Add(1000, address, ("Human", 1000));
        _cache.V2Avatars.Add(1000, "0xactive", ("Human", 1000));
        _cache.V2Avatars.Add(1000, "0xexpired", ("Human", 1000));
        _cache.V2Avatars.Add(1000, "0xincomingactive", ("Human", 1000));
        _cache.V2Avatars.Add(1000, "0xincomingexpired", ("Human", 1000));
        _cache.V2TrustRelations.Add(2000, $"{address}:0xactive", 2000L);
        _cache.V2TrustRelations.Add(2000, $"{address}:0xexpired", 1000L);
        _cache.V2TrustRelations.Add(2000, $"0xincomingactive:{address}", 3000L);
        _cache.V2TrustRelations.Add(2000, $"0xincomingexpired:{address}", 1000L);
        _cache.RebuildSecondaryIndexes();
        var controller = CreateTrustRelationsController();

        // Act / Assert - before expiry of active trust
        _state.CurrentBlockTimestamp = 1500;
        var resultAt1500 = controller.GetTrustRelations(address, version: 2);
        var okAt1500 = resultAt1500.Result as OkObjectResult;
        okAt1500.Should().NotBeNull();
        var responseAt1500 = okAt1500!.Value as TrustRelationsResponse;
        responseAt1500.Should().NotBeNull();
        responseAt1500!.Trusts.Should().ContainSingle(t => t.Trustee == "0xactive");
        responseAt1500.Trusts.Should().NotContain(t => t.Trustee == "0xexpired");
        responseAt1500.TrustedBy.Should().ContainSingle(t => t.Truster == "0xincomingactive");
        responseAt1500.TrustedBy.Should().NotContain(t => t.Truster == "0xincomingexpired");

        // Act / Assert - after expiry
        _state.CurrentBlockTimestamp = 2500;
        var resultAt2500 = controller.GetTrustRelations(address, version: 2);
        var okAt2500 = resultAt2500.Result as OkObjectResult;
        okAt2500.Should().NotBeNull();
        var responseAt2500 = okAt2500!.Value as TrustRelationsResponse;
        responseAt2500.Should().NotBeNull();
        responseAt2500!.Trusts.Should().BeEmpty();
        responseAt2500.TrustedBy.Should().ContainSingle(t => t.Truster == "0xincomingactive");
    }

    [Fact]
    public void GroupMemberships_ShouldFilterExpiredByCurrentBlockTimestamp()
    {
        // Arrange
        var group = "0xgroup000000000000000000000000000000000000";
        var member = "0xmember0000000000000000000000000000000000";
        // Register groups and members
        _cache.Groups.Add(1000, group, ("TestGroup", "0xmint", "TG"));
        _cache.Groups.Add(1000, "0xothergroup", ("OtherGroup", "0xmint", "OG"));
        _cache.V2Avatars.Add(1000, member, ("Human", 1000));
        _cache.V2Avatars.Add(1000, "0xexpiredmember", ("Human", 1000));
        _cache.GroupMemberships.Add(1200, $"{group}:{member}", (member, 2000L));
        _cache.GroupMemberships.Add(1200, $"{group}:0xexpiredmember", ("0xexpiredmember", 1000L));
        _cache.GroupMemberships.Add(1200, $"0xothergroup:{member}", (member, 1000L));
        _cache.RebuildSecondaryIndexes();
        var controller = CreateGroupMembershipsController();

        // Act / Assert - before expiry
        _state.CurrentBlockTimestamp = 1500;
        var membersAt1500 = controller.GetGroupMembers(group).Result as OkObjectResult;
        membersAt1500.Should().NotBeNull();
        var membersResponseAt1500 = membersAt1500!.Value as GroupMembersResponse;
        membersResponseAt1500.Should().NotBeNull();
        membersResponseAt1500!.Members.Should().ContainSingle(m => m.Member == member);
        membersResponseAt1500.Members.Should().NotContain(m => m.Member == "0xexpiredmember");

        // Act / Assert - after expiry
        _state.CurrentBlockTimestamp = 2500;
        var membersAt2500 = controller.GetGroupMembers(group).Result as OkObjectResult;
        membersAt2500.Should().NotBeNull();
        var membersResponseAt2500 = membersAt2500!.Value as GroupMembersResponse;
        membersResponseAt2500.Should().NotBeNull();
        membersResponseAt2500!.Members.Should().BeEmpty();

        var groupsAt2500 = controller.GetMemberGroups(member).Result as OkObjectResult;
        groupsAt2500.Should().NotBeNull();
        var groupsResponseAt2500 = groupsAt2500!.Value as MemberGroupsResponse;
        groupsResponseAt2500.Should().NotBeNull();
        groupsResponseAt2500!.Groups.Should().BeEmpty();
    }

    #endregion
}
