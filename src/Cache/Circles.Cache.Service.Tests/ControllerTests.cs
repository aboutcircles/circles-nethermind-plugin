using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Controllers;
using Circles.Cache.Service.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using FluentAssertions;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Tests for API controllers - validates HTTP endpoints return correct data.
/// </summary>
public class ControllerTests
{
    private readonly CacheContainer _cache;
    private readonly CacheServiceState _state;

    public ControllerTests()
    {
        _cache = new CacheContainer(rollbackCapacity: 12);
        _state = new CacheServiceState(rollbackCapacity: 12);
        _state.LastProcessedBlock = 1000;
        _state.WarmupComplete = true;
        _state.ListenerConnected = true;
    }

    #region BalancesController Tests

    [Fact]
    public void GetTokenBalances_ShouldReturnEmpty_WhenNoBalances()
    {
        // Arrange
        var logger = new Mock<ILogger<BalancesController>>();
        var controller = new BalancesController(_cache, _state, logger.Object);

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

        var logger = new Mock<ILogger<BalancesController>>();
        var controller = new BalancesController(_cache, _state, logger.Object);

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

        var logger = new Mock<ILogger<BalancesController>>();
        var controller = new BalancesController(_cache, _state, logger.Object);

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
        balances[0].TokenOwner.Should().BeNull(); // V2 doesn't have token owners
    }

    [Fact]
    public void GetTokenBalances_ShouldReject_InvalidAddress()
    {
        // Arrange
        var logger = new Mock<ILogger<BalancesController>>();
        var controller = new BalancesController(_cache, _state, logger.Object);

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
        _cache.V1BalancesByAccountAndToken.Add(1000, $"{address}:0xtoken1", 100m);
        _cache.V2BalancesByAccountAndToken.Add(2000, $"{address}:12345", 200m);
        _cache.RebuildSecondaryIndexes();

        var logger = new Mock<ILogger<BalancesController>>();
        var controller = new BalancesController(_cache, _state, logger.Object);

        // Act
        var result = controller.GetTotalBalance(address);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var response = okResult!.Value as TotalBalanceResponse;
        response.Should().NotBeNull();
        response!.Balance.Should().Be("300");
    }

    [Fact]
    public void GetTotalBalanceV1_ShouldOnlySumV1()
    {
        // Arrange
        var address = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V1BalancesByAccountAndToken.Add(1000, $"{address}:0xtoken1", 100m);
        _cache.V1BalancesByAccountAndToken.Add(1000, $"{address}:0xtoken2", 50m);
        _cache.V2BalancesByAccountAndToken.Add(2000, $"{address}:12345", 200m);
        _cache.RebuildSecondaryIndexes();

        var logger = new Mock<ILogger<BalancesController>>();
        var controller = new BalancesController(_cache, _state, logger.Object);

        // Act
        var result = controller.GetTotalBalanceV1(address);

        // Assert
        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        var response = okResult!.Value as TotalBalanceResponse;
        response.Should().NotBeNull();
        response!.Balance.Should().Be("150");
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

        var logger = new Mock<ILogger<AvatarsController>>();
        var controller = new AvatarsController(_cache, _state, logger.Object);

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

        var logger = new Mock<ILogger<AvatarsController>>();
        var controller = new AvatarsController(_cache, _state, logger.Object);

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
        var logger = new Mock<ILogger<AvatarsController>>();
        var controller = new AvatarsController(_cache, _state, logger.Object);

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

        var logger = new Mock<ILogger<AvatarsController>>();
        var controller = new AvatarsController(_cache, _state, logger.Object);

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

        var logger = new Mock<ILogger<AvatarsController>>();
        var controller = new AvatarsController(_cache, _state, logger.Object);

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
        var logger = new Mock<ILogger<AvatarsController>>();
        var controller = new AvatarsController(_cache, _state, logger.Object);
        var addresses = Enumerable.Range(0, 101).Select(i => $"0x{i:X40}").ToArray();

        // Act
        var request = new AvatarInfoBatchRequest(addresses);
        var result = controller.GetAvatarInfoBatch(request);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion
}
