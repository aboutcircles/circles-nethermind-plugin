using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Circles.Rpc.CacheServiceClient;
using Circles.Rpc.CacheServiceClient.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;
using FluentAssertions;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Tests for the RPC CacheServiceClient - validates correct HTTP communication
/// and response deserialization between RPC service and Cache service.
/// </summary>
public class RpcCacheClientTests
{
    private static HttpClient CreateMockHttpClient(HttpStatusCode statusCode, object? responseContent)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = responseContent != null
                    ? new StringContent(JsonSerializer.Serialize(responseContent))
                    : null
            });

        return new HttpClient(mockHandler.Object);
    }

    [Fact]
    public async Task GetTokenBalancesAsync_ShouldDeserialize_Correctly()
    {
        // Arrange
        var response = new[]
        {
            new { TokenId = "0xtoken1", Balance = "150.25", TokenOwner = (string?)"0xowner1", Version = 1 },
            new { TokenId = "12345", Balance = "200.50", TokenOwner = (string?)null, Version = 2 }
        };

        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, response);
        var client = new CacheServiceClient(httpClient, "http://localhost:3001");

        // Act
        var balances = await client.GetTokenBalancesAsync("0xtest");

        // Assert
        balances.Should().HaveCount(2);
        balances[0].TokenId.Should().Be("0xtoken1");
        balances[0].Balance.Should().Be("150.25");
        balances[0].Version.Should().Be(1);
        balances[1].TokenId.Should().Be("12345");
        balances[1].Version.Should().Be(2);
    }

    [Fact]
    public async Task GetTotalBalanceAsync_ShouldDeserialize_Correctly()
    {
        // Arrange
        var response = new { Balance = "1476.50" };
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, response);
        var client = new CacheServiceClient(httpClient, "http://localhost:3001");

        // Act
        var balance = await client.GetTotalBalanceAsync("0xtest");

        // Assert
        balance.Should().Be("1476.50");
    }

    [Fact]
    public async Task GetAvatarInfoAsync_ShouldReturn_Null_WhenNotFound()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(HttpStatusCode.NotFound, null);
        var client = new CacheServiceClient(httpClient, "http://localhost:3001");

        // Act
        var avatar = await client.GetAvatarInfoAsync("0xtest");

        // Assert
        avatar.Should().BeNull();
    }

    [Fact]
    public async Task GetAvatarInfoAsync_ShouldDeserialize_V2Avatar()
    {
        // Arrange
        var response = new
        {
            Avatar = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
            Version = 2,
            Type = "Human",
            TokenId = (string?)null,
            HasV1 = false,
            V1Token = (string?)null,
            CidV0 = "QmYxivS5DXZgDUgLE8YTZV9AnFKPSLvd5R5sWEyWAJKXWE",
            IsHuman = true,
            Name = (string?)null,
            Symbol = (string?)null,
            RegisteredAt = 1704067200L
        };

        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, response);
        var client = new CacheServiceClient(httpClient, "http://localhost:3001");

        // Act
        var avatar = await client.GetAvatarInfoAsync("0xtest");

        // Assert
        avatar.Should().NotBeNull();
        avatar!.Version.Should().Be(2);
        avatar.Type.Should().Be("Human");
        avatar.IsHuman.Should().BeTrue();
        avatar.CidV0.Should().Be("QmYxivS5DXZgDUgLE8YTZV9AnFKPSLvd5R5sWEyWAJKXWE");
        avatar.RegisteredAt.Should().Be(1704067200L);
    }

    [Fact]
    public async Task GetAvatarInfoBatchAsync_ShouldDeserialize_WithNulls()
    {
        // Arrange
        var response = new object?[]
        {
            new
            {
                Avatar = "0xaddr1",
                Version = 2,
                Type = "Human",
                IsHuman = true
            },
            null, // Not found
            new
            {
                Avatar = "0xaddr3",
                Version = 1,
                Type = "Human",
                HasV1 = true,
                V1Token = "0xtoken3"
            }
        };

        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, response);
        var client = new CacheServiceClient(httpClient, "http://localhost:3001");

        // Act
        var avatars = await client.GetAvatarInfoBatchAsync(new[] { "0xaddr1", "0xaddr2", "0xaddr3" });

        // Assert
        avatars.Should().HaveCount(3);
        avatars[0].Should().NotBeNull();
        avatars[0]!.Version.Should().Be(2);
        avatars[1].Should().BeNull();
        avatars[2].Should().NotBeNull();
        avatars[2]!.Version.Should().Be(1);
    }

    [Fact]
    public async Task GetProfileCidAsync_ShouldReturn_Cid()
    {
        // Arrange
        var response = new { Cid = "QmYxivS5DXZgDUgLE8YTZV9AnFKPSLvd5R5sWEyWAJKXWE" };
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, response);
        var client = new CacheServiceClient(httpClient, "http://localhost:3001");

        // Act
        var cid = await client.GetProfileCidAsync("0xtest");

        // Assert
        cid.Should().Be("QmYxivS5DXZgDUgLE8YTZV9AnFKPSLvd5R5sWEyWAJKXWE");
    }

    [Fact]
    public async Task GetProfileCidAsync_ShouldReturn_Null_WhenNoProfile()
    {
        // Arrange
        var response = new { Cid = (string?)null };
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, response);
        var client = new CacheServiceClient(httpClient, "http://localhost:3001");

        // Act
        var cid = await client.GetProfileCidAsync("0xtest");

        // Assert
        cid.Should().BeNull();
    }

    [Fact]
    public async Task IsReadyAsync_ShouldReturn_True_When200()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(HttpStatusCode.OK, new { status = "ready" });
        var client = new CacheServiceClient(httpClient, "http://localhost:3001");

        // Act
        var ready = await client.IsReadyAsync();

        // Assert
        ready.Should().BeTrue();
    }

    [Fact]
    public async Task IsReadyAsync_ShouldReturn_False_When503()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(HttpStatusCode.ServiceUnavailable, new { status = "not_ready" });
        var client = new CacheServiceClient(httpClient, "http://localhost:3001");

        // Act
        var ready = await client.IsReadyAsync();

        // Assert
        ready.Should().BeFalse();
    }

    /// <summary>
    /// IMPORTANT: This test documents a known model mismatch.
    /// The cache service returns ShortName but the RPC client model doesn't have it.
    /// </summary>
    [Fact]
    public void ModelMismatch_CacheReturnsShortName_RpcModelMissesIt()
    {
        // Document the issue: Cache service ApiResponses.cs has ShortName
        // but Rpc.CacheServiceClient.Models.CacheServiceModels.cs does NOT

        // This test passes because the RPC model simply ignores the extra field,
        // but it means the ShortName data is lost in transit.

        var cacheResponse = new Circles.Cache.Service.Models.AvatarInfoResponse(
            Avatar: "0xtest",
            Version: 2,
            Type: "Human",
            ShortName: "zAlice" // This field exists in cache response
        );

        var rpcModel = new AvatarInfoResponse(
            Avatar: "0xtest",
            Version: 2,
            Type: "Human"
            // ShortName property does not exist!
        );

        // Verify the cache model has the field
        cacheResponse.ShortName.Should().Be("zAlice");

        // The RPC model doesn't have a ShortName property - this is a gap
        // that should be fixed by adding ShortName to CacheServiceModels.cs
    }
}
