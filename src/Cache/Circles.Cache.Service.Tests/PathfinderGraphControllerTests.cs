using System.Numerics;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Controllers;
using Circles.Cache.Service.Models;
using Circles.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;
using Xunit;
using FluentAssertions;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Tests for PathfinderGraphController — the zero-SQL cache-feed endpoint.
/// Covers: balance conversion + demurrage, wrapper trust derivation, consent bit extraction,
/// ETag conditional requests, warmup gating, group filtering, and include param parsing.
/// </summary>
public class PathfinderGraphControllerTests
{
    private const string StandardTreasuryMint = "0xcdfc5135aec0afbf102c108e7f5c8a88c6112842";
    // V2 Hub epoch on gnosis mainnet (same as V1): 2020-10-15 00:00 UTC
    private const uint V2InflationDayZero = 1_602_720_000;

    private readonly CacheContainer _cache;
    private readonly CacheServiceState _state;

    public PathfinderGraphControllerTests()
    {
        _cache = new CacheContainer(rollbackCapacity: 12);
        _state = new CacheServiceState(rollbackCapacity: 12);
        _state.LastProcessedBlock = 5000;
        _state.WarmupComplete = true;
        _state.ListenerConnected = true;
    }

    private PathfinderGraphController CreateController(string? ifNoneMatch = null)
    {
        var logger = new Mock<ILogger<PathfinderGraphController>>();
        var controller = new PathfinderGraphController(_cache, _state, logger.Object);
        var context = new DefaultHttpContext();
        if (ifNoneMatch != null)
            context.Request.Headers.IfNoneMatch = new StringValues(ifNoneMatch);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return controller;
    }

    // ── Warmup / ETag ──────────────────────────────────────────────────

    [Fact]
    public void GetGraph_ShouldReturn503_WhenWarmupIncomplete()
    {
        _state.WarmupComplete = false;
        var controller = CreateController();

        var result = controller.GetGraph();

        var obj = result.Result as ObjectResult;
        obj.Should().NotBeNull();
        obj!.StatusCode.Should().Be(503);
    }

    [Fact]
    public void GetGraph_ShouldReturn304_WhenETagMatches()
    {
        var etag = $"\"{_state.LastProcessedBlock}\"";
        var controller = CreateController(ifNoneMatch: etag);

        var result = controller.GetGraph();

        var status = result.Result as StatusCodeResult;
        status.Should().NotBeNull();
        status!.StatusCode.Should().Be(304);
    }

    [Fact]
    public void GetGraph_ShouldReturnOk_WhenETagDoesNotMatch()
    {
        var controller = CreateController(ifNoneMatch: "\"999\"");

        var result = controller.GetGraph();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var response = ok!.Value as PathfinderGraphResponse;
        response.Should().NotBeNull();
        response!.LastProcessedBlock.Should().Be(5000);
        response.SchemaVersion.Should().Be(1);
    }

    // ── ParseInclude ───────────────────────────────────────────────────

    [Fact]
    public void GetGraph_ShouldReturnAllSections_WhenNoInclude()
    {
        SeedMinimalData();
        var controller = CreateController();

        var result = controller.GetGraph();
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Balances.Should().NotBeNull();
        response.Trust.Should().NotBeNull();
        response.Groups.Should().NotBeNull();
        response.GroupTrusts.Should().NotBeNull();
        response.ConsentedFlow.Should().NotBeNull();
        // C3 fail-closed gate inputs — must be in the default snapshot so
        // CapacityGraphContractState.IsApprovedForAll has the data it needs.
        response.ScoreRouters.Should().NotBeNull();
        response.OperatorApprovals.Should().NotBeNull();
    }

    [Fact]
    public void GetGraph_ScoreRouters_AreEmpty_WhenNoDataSourceConfigured()
    {
        // Unit test constructor passes _dataSource = null. Builder must no-op gracefully:
        // emit empty list (section requested), not null and not throw.
        var controller = CreateController();

        var result = controller.GetGraph(include: "scorerouters,operatorapprovals");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.ScoreRouters.Should().NotBeNull().And.BeEmpty();
        response.OperatorApprovals.Should().NotBeNull().And.BeEmpty();
        response.Trust.Should().BeNull();
        response.Groups.Should().BeNull();
    }

    [Fact]
    public void GetGraph_ScoreRouters_OmittedWhenNotInInclude()
    {
        var controller = CreateController();

        var result = controller.GetGraph(include: "balances");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.ScoreRouters.Should().BeNull();
        response.OperatorApprovals.Should().BeNull();
    }

    [Fact]
    public void GetGraph_ShouldReturnOnlyRequestedSections()
    {
        SeedMinimalData();
        var controller = CreateController();

        var result = controller.GetGraph(include: "balances,groups");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Balances.Should().NotBeNull();
        response.Groups.Should().NotBeNull();
        response.Trust.Should().BeNull();
        response.GroupTrusts.Should().BeNull();
        response.ConsentedFlow.Should().BeNull();
    }

    // ── BuildBalances ──────────────────────────────────────────────────

    [Fact]
    public void BuildBalances_ShouldConvertDecimalToAttoCirclesIntegerString()
    {
        var account = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var token = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        _cache.V2Avatars.Add(1000, account, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2Avatars.Add(1000, token, ("CrcV2_RegisterHuman", 1704067200L)); // token owner must be registered
        _cache.V2BalancesByAccountAndToken.Add(1000, $"{account}:{token}", 1.5m);
        // Set a recent lastActivity so demurrage is minimal
        var recentTimestamp = (long)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
        _cache.V2LastActivity.Add(1000, $"{account}:{token}", recentTimestamp);

        var controller = CreateController();
        var result = controller.GetGraph(include: "balances");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Balances.Should().HaveCount(1);
        var balance = response.Balances![0];
        balance.Account.Should().Be(account);
        balance.TokenAddress.Should().Be(token);

        // Balance should be a valid integer string (no decimals, no scientific notation)
        BigInteger.TryParse(balance.Balance, out var parsed).Should().BeTrue(
            $"Balance '{balance.Balance}' should be a valid integer string");
        parsed.Should().BeGreaterThan(BigInteger.Zero);

        // 1.5 Circles = 1.5e18 attoCircles ≈ 1500000000000000000 (before demurrage)
        // After demurrage (very recent), should be close but slightly less
        var expected = CirclesConverter.CirclesToAttoCircles(1.5m);
        parsed.Should().BeLessOrEqualTo(expected, "demurrage should only reduce the balance");
        parsed.Should().BeGreaterThan(expected * 99 / 100, "1 minute of demurrage should be < 1%");
    }

    [Fact]
    public void BuildBalances_ShouldSkipUnregisteredAvatars()
    {
        var unregistered = "0xaaaa000000000000000000000000000000000001";
        var token = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        // NOT in V2Avatars — should be skipped
        _cache.V2BalancesByAccountAndToken.Add(1000, $"{unregistered}:{token}", 100m);

        var controller = CreateController();
        var result = controller.GetGraph(include: "balances");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Balances.Should().BeEmpty();
    }

    [Fact]
    public void BuildBalances_ShouldSkipZeroBalances()
    {
        var account = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var token = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        _cache.V2Avatars.Add(1000, account, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2BalancesByAccountAndToken.Add(1000, $"{account}:{token}", 0m);

        var controller = CreateController();
        var result = controller.GetGraph(include: "balances");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Balances.Should().BeEmpty();
    }

    [Fact]
    public void BuildBalances_ShouldMarkWrappedTokens()
    {
        var account = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var wrapperAddress = "0xbbbb000000000000000000000000000000000001";
        var underlyingAvatar = "0xcccc000000000000000000000000000000000001";

        _cache.V2Avatars.Add(1000, account, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2Avatars.Add(1000, underlyingAvatar, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.Erc20WrapperAddresses.Add(1000, wrapperAddress, (underlyingAvatar, 0)); // 0 = demurraged
        _cache.V2BalancesByAccountAndToken.Add(1000, $"{account}:{wrapperAddress}", 50m);
        _cache.V2LastActivity.Add(1000, $"{account}:{wrapperAddress}",
            (long)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var controller = CreateController();
        var result = controller.GetGraph(include: "balances");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Balances.Should().HaveCount(1);
        response.Balances![0].IsWrapped.Should().BeTrue();
        response.Balances[0].CirclesType.Should().Be("demurraged");
    }

    [Fact]
    public void BuildBalances_ShouldMarkStaticWrappedTokens()
    {
        var account = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var wrapperAddress = "0xbbbb000000000000000000000000000000000002";
        var underlyingAvatar = "0xcccc000000000000000000000000000000000001";

        _cache.V2Avatars.Add(1000, account, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2Avatars.Add(1000, underlyingAvatar, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.Erc20WrapperAddresses.Add(1000, wrapperAddress, (underlyingAvatar, 1)); // 1 = static
        _cache.V2BalancesByAccountAndToken.Add(1000, $"{account}:{wrapperAddress}", 50m);

        var controller = CreateController();
        var result = controller.GetGraph(include: "balances");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Balances.Should().HaveCount(1);
        response.Balances![0].IsWrapped.Should().BeTrue();
        response.Balances[0].CirclesType.Should().Be("static");
    }

    [Fact]
    public void BuildBalances_ShouldFilterWrappersOfUnregisteredAvatars()
    {
        var account = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var wrapperAddress = "0xbbbb000000000000000000000000000000000001";
        var unregisteredAvatar = "0xaaaa000000000000000000000000000000000001";

        _cache.V2Avatars.Add(1000, account, ("CrcV2_RegisterHuman", 1704067200L));
        // underlyingAvatar NOT registered in V2Avatars
        _cache.Erc20WrapperAddresses.Add(1000, wrapperAddress, (unregisteredAvatar, 0));
        _cache.V2BalancesByAccountAndToken.Add(1000, $"{account}:{wrapperAddress}", 50m);

        var controller = CreateController();
        var result = controller.GetGraph(include: "balances");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Balances.Should().BeEmpty();
    }

    [Fact]
    public void BuildBalances_ShouldIncludeWrappersOfRegisteredGroups()
    {
        var account = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var wrapperAddress = "0xee00ee00ee00ee00ee00ee00ee00ee00ee00ee00";
        var groupAvatar = "0xaa00aa00aa00aa00aa00aa00aa00aa00aa00aa00";

        _cache.V2Avatars.Add(1000, account, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.Groups.Add(1000, groupAvatar, ("Group", StandardTreasuryMint, "G"));
        _cache.Erc20WrapperAddresses.Add(1000, wrapperAddress, (groupAvatar, 1)); // static wrapper
        _cache.V2BalancesByAccountAndToken.Add(1000, $"{account}:{wrapperAddress}", 50m);

        var controller = CreateController();
        var result = controller.GetGraph(include: "balances");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Balances.Should().HaveCount(1);
        response.Balances![0].TokenAddress.Should().Be(wrapperAddress);
        response.Balances[0].IsWrapped.Should().BeTrue();
        response.Balances[0].CirclesType.Should().Be("static");
    }

    // ── BuildTrust ─────────────────────────────────────────────────────

    [Fact]
    public void BuildTrust_ShouldEmitNativeTrustEdges()
    {
        var truster = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var trustee = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        _cache.V2Avatars.Add(1000, truster, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2Avatars.Add(1000, trustee, ("CrcV2_RegisterHuman", 1704067200L));
        // expiryTime in the far future
        _cache.V2TrustRelations.Add(1000, $"{truster}:{trustee}", 9999999999L);

        var controller = CreateController();
        var result = controller.GetGraph(include: "trust");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Trust.Should().ContainSingle(t => t.Truster == truster && t.Trustee == trustee);
        response.Trust![0].Limit.Should().Be(100);
    }

    [Fact]
    public void BuildTrust_ShouldSkipRevokedTrust()
    {
        var truster = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var trustee = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        _cache.V2Avatars.Add(1000, truster, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2Avatars.Add(1000, trustee, ("CrcV2_RegisterHuman", 1704067200L));
        // expiryTime in the past → revoked
        _cache.V2TrustRelations.Add(1000, $"{truster}:{trustee}", 1L);

        var controller = CreateController();
        var result = controller.GetGraph(include: "trust");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Trust.Should().BeEmpty();
    }

    [Fact]
    public void BuildTrust_ShouldDeriveWrapperTrustEdges()
    {
        var truster = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var trustee = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        var wrapperAddress = "0xbbbb000000000000000000000000000000000001";

        _cache.V2Avatars.Add(1000, truster, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2Avatars.Add(1000, trustee, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2TrustRelations.Add(1000, $"{truster}:{trustee}", 9999999999L);
        // trustee has a wrapper deployed
        _cache.Erc20WrapperAddresses.Add(1000, wrapperAddress, (trustee, 0));

        var controller = CreateController();
        var result = controller.GetGraph(include: "trust");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        // Should have 2 edges: native + wrapper
        response!.Trust.Should().HaveCount(2);
        response.Trust.Should().Contain(t => t.Truster == truster && t.Trustee == trustee);
        response.Trust.Should().Contain(t => t.Truster == truster && t.Trustee == wrapperAddress);
    }

    [Fact]
    public void BuildTrust_ShouldDeriveMultipleWrapperEdgesPerTrustee()
    {
        var truster = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var trustee = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        var wrapper1 = "0xbbbb000000000000000000000000000000000003";
        var wrapper2 = "0xbbbb000000000000000000000000000000000004";

        _cache.V2Avatars.Add(1000, truster, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2Avatars.Add(1000, trustee, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2TrustRelations.Add(1000, $"{truster}:{trustee}", 9999999999L);
        _cache.Erc20WrapperAddresses.Add(1000, wrapper1, (trustee, 0));
        _cache.Erc20WrapperAddresses.Add(1000, wrapper2, (trustee, 1));

        var controller = CreateController();
        var result = controller.GetGraph(include: "trust");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        // 1 native + 2 wrappers = 3
        response!.Trust.Should().HaveCount(3);
    }

    [Fact]
    public void BuildTrust_ShouldIncludeEdgesWhereGroupIsTrustee()
    {
        var org = "0xdddd000000000000000000000000000000000001";
        var group = "0xaa00aa00aa00aa00aa00aa00aa00aa00aa00aa00";

        _cache.V2Avatars.Add(1000, org, ("CrcV2_RegisterOrganization", 1704067200L));
        // Group is NOT in V2Avatars — only in Groups cache (like production)
        _cache.Groups.Add(1000, group, ("Gnosis Group", StandardTreasuryMint, "GG"));
        _cache.V2TrustRelations.Add(1000, $"{org}:{group}", 9999999999L);

        var controller = CreateController();
        var result = controller.GetGraph(include: "trust");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        // Org trusting a router group should produce a trust edge
        response!.Trust.Should().ContainSingle(t => t.Truster == org && t.Trustee == group);
    }

    [Fact]
    public void BuildTrust_ShouldExcludeGroupTrusters()
    {
        var group = "0xaa00aa00aa00aa00aa00aa00aa00aa00aa00aa00";
        var trustee = "0x42cedde51198d1773590311e2a340dc06b24cb37";

        _cache.V2Avatars.Add(1000, group, ("CrcV2_RegisterGroup", 1704067200L));
        _cache.V2Avatars.Add(1000, trustee, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.Groups.Add(1000, group, ("Test Group", StandardTreasuryMint, "TG"));
        _cache.V2TrustRelations.Add(1000, $"{group}:{trustee}", 9999999999L);

        var controller = CreateController();
        var result = controller.GetGraph(include: "trust");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        // Group trusters are excluded from BuildTrust (handled in BuildGroupTrusts)
        response!.Trust.Should().BeEmpty();
    }

    // ── BuildGroups ────────────────────────────────────────────────────

    [Fact]
    public void BuildGroups_ShouldOnlyIncludeRouterGroups()
    {
        var routerGroup = "0xdddd000000000000000000000000000000000002";
        var otherGroup = "0xdddd000000000000000000000000000000000003";

        _cache.Groups.Add(1000, routerGroup, ("Router Group", StandardTreasuryMint, "RG"));
        _cache.Groups.Add(1000, otherGroup, ("Custom Group", "0xffff000000000000000000000000000000000001", "CG"));

        var controller = CreateController();
        var result = controller.GetGraph(include: "groups");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Groups.Should().HaveCount(1);
        response.Groups![0].GroupAddress.Should().Be(routerGroup);
    }

    // ── BuildGroupTrusts ───────────────────────────────────────────────

    [Fact]
    public void BuildGroupTrusts_ShouldOnlyIncludeRouterGroupTrusters()
    {
        var routerGroup = "0xdddd000000000000000000000000000000000002";
        var member = "0xeeee000000000000000000000000000000000001";

        _cache.V2Avatars.Add(1000, routerGroup, ("CrcV2_RegisterGroup", 1704067200L));
        _cache.V2Avatars.Add(1000, member, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.Groups.Add(1000, routerGroup, ("Router Group", StandardTreasuryMint, "RG"));
        _cache.V2TrustRelations.Add(1000, $"{routerGroup}:{member}", 9999999999L);

        var controller = CreateController();
        var result = controller.GetGraph(include: "groupTrusts");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.GroupTrusts.Should().HaveCount(1);
        response.GroupTrusts![0].GroupAddress.Should().Be(routerGroup);
        response.GroupTrusts[0].TrustedToken.Should().Be(member);
    }

    [Fact]
    public void BuildGroupTrusts_ShouldExcludeNonRouterGroups()
    {
        var nonRouterGroup = "0xdddd000000000000000000000000000000000004";
        var member = "0xeeee000000000000000000000000000000000001";

        _cache.V2Avatars.Add(1000, nonRouterGroup, ("CrcV2_RegisterGroup", 1704067200L));
        _cache.V2Avatars.Add(1000, member, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.Groups.Add(1000, nonRouterGroup, ("Custom Group", "0xffff000000000000000000000000000000000001", "CG"));
        _cache.V2TrustRelations.Add(1000, $"{nonRouterGroup}:{member}", 9999999999L);

        var controller = CreateController();
        var result = controller.GetGraph(include: "groupTrusts");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.GroupTrusts.Should().BeEmpty();
    }

    // ── BuildConsentedFlow ─────────────────────────────────────────────

    [Fact]
    public void BuildConsentedFlow_ShouldExtractBit0OfByte31()
    {
        var avatar = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V2Avatars.Add(1000, avatar, ("CrcV2_RegisterHuman", 1704067200L));

        // Create 32-byte flag with bit 0 of byte[31] set to 1
        var flags = new byte[32];
        flags[31] = 0x01; // consent = true
        _cache.ConsentedFlowFlags.Add(1000, avatar, flags);

        var controller = CreateController();
        var result = controller.GetGraph(include: "consentedFlow");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.ConsentedFlow.Should().HaveCount(1);
        response.ConsentedFlow![0].Avatar.Should().Be(avatar);
        response.ConsentedFlow[0].HasConsentedFlow.Should().BeTrue();
    }

    [Fact]
    public void BuildConsentedFlow_ShouldReturnFalse_WhenBit0IsZero()
    {
        var avatar = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V2Avatars.Add(1000, avatar, ("CrcV2_RegisterHuman", 1704067200L));

        var flags = new byte[32];
        flags[31] = 0x00; // consent = false
        _cache.ConsentedFlowFlags.Add(1000, avatar, flags);

        var controller = CreateController();
        var result = controller.GetGraph(include: "consentedFlow");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.ConsentedFlow.Should().HaveCount(1);
        response.ConsentedFlow![0].HasConsentedFlow.Should().BeFalse();
    }

    [Fact]
    public void BuildConsentedFlow_ShouldHandleOnlyBit0_NotOtherBits()
    {
        var avatar = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V2Avatars.Add(1000, avatar, ("CrcV2_RegisterHuman", 1704067200L));

        var flags = new byte[32];
        flags[31] = 0xFE; // all bits except bit 0 → consent = false
        _cache.ConsentedFlowFlags.Add(1000, avatar, flags);

        var controller = CreateController();
        var result = controller.GetGraph(include: "consentedFlow");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.ConsentedFlow![0].HasConsentedFlow.Should().BeFalse();
    }

    [Fact]
    public void BuildConsentedFlow_ShouldSkipShortFlagBytes()
    {
        var avatar = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V2Avatars.Add(1000, avatar, ("CrcV2_RegisterHuman", 1704067200L));

        // Only 16 bytes — should be skipped with warning
        var shortFlags = new byte[16];
        shortFlags[15] = 0x01;
        _cache.ConsentedFlowFlags.Add(1000, avatar, shortFlags);

        var controller = CreateController();
        var result = controller.GetGraph(include: "consentedFlow");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.ConsentedFlow.Should().BeEmpty();
    }

    [Fact]
    public void BuildConsentedFlow_ShouldSkipUnregisteredAvatars()
    {
        var unregistered = "0xaaaa000000000000000000000000000000000001";
        var flags = new byte[32];
        flags[31] = 0x01;
        _cache.ConsentedFlowFlags.Add(1000, unregistered, flags);

        var controller = CreateController();
        var result = controller.GetGraph(include: "consentedFlow");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.ConsentedFlow.Should().BeEmpty();
    }

    // ── Full snapshot consistency ──────────────────────────────────────

    [Fact]
    public void GetGraph_FullSnapshot_ShouldIncludeAllSections()
    {
        SeedFullData();
        var controller = CreateController();

        var result = controller.GetGraph();
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Balances.Should().NotBeEmpty("seeded balance data");
        response.Trust.Should().NotBeEmpty("seeded trust data");
        response.Groups.Should().NotBeEmpty("seeded group data");
        response.GroupTrusts.Should().NotBeEmpty("seeded group trust data");
        response.ConsentedFlow.Should().NotBeEmpty("seeded consent data");
    }

    [Fact]
    public void GetGraph_ShouldReturnBalancesWithDemurrageApplied()
    {
        var account = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var token = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        _cache.V2Avatars.Add(1000, account, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2Avatars.Add(1000, token, ("CrcV2_RegisterHuman", 1704067200L)); // token owner must be registered
        _cache.V2BalancesByAccountAndToken.Add(1000, $"{account}:{token}", 100m);

        // lastActivity 30 days ago — should show noticeable demurrage
        var thirtyDaysAgo = (long)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (30 * 86400);
        _cache.V2LastActivity.Add(1000, $"{account}:{token}", thirtyDaysAgo);

        var controller = CreateController();
        var result = controller.GetGraph(include: "balances");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        var parsed = BigInteger.Parse(response!.Balances![0].Balance);
        var rawAtto = CirclesConverter.CirclesToAttoCircles(100m);

        // 30 days of 7%/year demurrage ≈ ~0.6% reduction
        parsed.Should().BeLessThan(rawAtto, "30 days of demurrage should reduce the balance");
        parsed.Should().BeGreaterThan(rawAtto * 98 / 100, "30 days should be < 2% demurrage");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private void SeedMinimalData()
    {
        var addr = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        _cache.V2Avatars.Add(1000, addr, ("CrcV2_RegisterHuman", 1704067200L));
    }

    private void SeedFullData()
    {
        var alice = "0xaaaa000000000000000000000000000000000002";
        var bob = "0xaaaa000000000000000000000000000000000003";
        var group = "0xaa00aa00aa00aa00aa00aa00aa00aa00aa00aa00";
        var now = (long)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _cache.V2Avatars.Add(1000, alice, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2Avatars.Add(1000, bob, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2Avatars.Add(1000, group, ("CrcV2_RegisterGroup", 1704067200L));

        _cache.V2BalancesByAccountAndToken.Add(1000, $"{alice}:{bob}", 100m);
        _cache.V2LastActivity.Add(1000, $"{alice}:{bob}", now);

        _cache.V2TrustRelations.Add(1000, $"{alice}:{bob}", 9999999999L);
        _cache.V2TrustRelations.Add(1000, $"{group}:{alice}", 9999999999L);

        _cache.Groups.Add(1000, group, ("Test Group", StandardTreasuryMint, "TG"));

        var flags = new byte[32];
        flags[31] = 0x01;
        _cache.ConsentedFlowFlags.Add(1000, alice, flags);
    }

    // ── Avatars Section ─────────────────────────────────────────────────

    [Fact]
    public void GetGraph_ShouldIncludeAvatarsSection_WithAllRegisteredV2Avatars()
    {
        var alice = "0xaaaa000000000000000000000000000000000002";
        var bob = "0xaaaa000000000000000000000000000000000003";
        var lonely = "0xaaaa000000000000000000000000000000000004";

        _cache.V2Avatars.Add(1000, alice, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2Avatars.Add(1000, bob, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2Avatars.Add(1000, lonely, ("CrcV2_RegisterOrganization", 1704067200L));

        var controller = CreateController();
        var result = controller.GetGraph();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var response = ok!.Value as PathfinderGraphResponse;
        response.Should().NotBeNull();
        response!.Avatars.Should().NotBeNull();
        response.Avatars!.Count.Should().Be(3);
        response.Avatars.Should().Contain(alice);
        response.Avatars.Should().Contain(bob);
        response.Avatars.Should().Contain(lonely);
    }

    [Fact]
    public void GetGraph_ShouldIncludeGroupsInAvatarsList()
    {
        // Hub.sol considers groups registered avatars (avatars[group] != address(0)).
        // BuildAvatars must include groups so the pathfinder's RegisteredAvatarIds
        // contains them — otherwise validator Rules 9/11 fire false positives.
        var human = "0xaaaa000000000000000000000000000000000002";
        var group = "0xbbbb000000000000000000000000000000000001";

        _cache.V2Avatars.Add(1000, human, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.Groups.Add(1000, group, ("TestGroup", StandardTreasuryMint, "TG"));

        var controller = CreateController();
        var result = controller.GetGraph();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var response = ok!.Value as PathfinderGraphResponse;
        response.Should().NotBeNull();
        response!.Avatars.Should().NotBeNull();
        response.Avatars!.Count.Should().Be(2, "both humans and groups are registered avatars");
        response.Avatars.Should().Contain(human);
        response.Avatars.Should().Contain(group);
    }

    [Fact]
    public void GetGraph_AvatarsInclude_ShouldBeFilterable()
    {
        var alice = "0xaaaa000000000000000000000000000000000002";
        _cache.V2Avatars.Add(1000, alice, ("CrcV2_RegisterHuman", 1704067200L));

        var controller = CreateController();

        // Only request balances — avatars should be null
        var result = controller.GetGraph(include: "balances");
        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;
        response!.Avatars.Should().BeNull();

        // Explicitly request avatars
        var result2 = controller.GetGraph(include: "avatars");
        var ok2 = result2.Result as OkObjectResult;
        var response2 = ok2!.Value as PathfinderGraphResponse;
        response2!.Avatars.Should().NotBeNull();
        response2.Avatars!.Count.Should().Be(1);
    }

    [Fact]
    public void BuildWrapperMappings_ShouldIncludeWrappersOfRegisteredGroups()
    {
        var wrapperAddress = "0xee00ee00ee00ee00ee00ee00ee00ee00ee00ee00";
        var groupAvatar = "0xaa00aa00aa00aa00aa00aa00aa00aa00aa00aa00";

        _cache.Groups.Add(1000, groupAvatar, ("Group", StandardTreasuryMint, "G"));
        _cache.Erc20WrapperAddresses.Add(1000, wrapperAddress, (groupAvatar, 0));

        var controller = CreateController();
        var result = controller.GetGraph(include: "wrapperMappings");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.WrapperMappings.Should().ContainSingle(x =>
            x.WrapperAddress == wrapperAddress && x.UnderlyingAvatar == groupAvatar);
    }

    // ── Regression: Cache Key Format Consistency (PR #188 / NotificationListener fix) ──

    /// <summary>
    /// Regression test for the cache key mismatch bug (PR #188 pattern).
    /// V2 balance cache keys MUST use hex tokenAddress (e.g. "0xaccount:0xtoken...")
    /// not decimal BigInteger id (e.g. "0xaccount:108659...").
    /// If warmup and realtime listeners use different key formats, post-warmup
    /// transfers create duplicate entries and balance updates go to wrong slots.
    /// </summary>
    [Fact]
    public void BalanceRow_TokenAddress_AlwaysHex42Chars()
    {
        var account = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var token1 = "0x42cedde51198d1773590311e2a340dc06b24cb37";
        var token2 = "0xabc123def456789012345678901234567890abcd";
        var now = (long)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _cache.V2Avatars.Add(1000, account, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2Avatars.Add(1000, token1, ("CrcV2_RegisterHuman", 1704067200L)); // token owners must be registered
        _cache.V2Avatars.Add(1000, token2, ("CrcV2_RegisterHuman", 1704067200L));
        _cache.V2BalancesByAccountAndToken.Add(1000, $"{account}:{token1}", 1.0m);
        _cache.V2BalancesByAccountAndToken.Add(1000, $"{account}:{token2}", 2.0m);
        _cache.V2LastActivity.Add(1000, $"{account}:{token1}", now);
        _cache.V2LastActivity.Add(1000, $"{account}:{token2}", now);

        var controller = CreateController();
        var result = controller.GetGraph(include: "balances");
        var response = ((OkObjectResult)result.Result!).Value as PathfinderGraphResponse;

        response!.Balances.Should().HaveCount(2);
        foreach (var balance in response.Balances!)
        {
            balance.TokenAddress.Should().StartWith("0x",
                "token address must be hex, not decimal BigInteger");
            balance.TokenAddress.Should().HaveLength(42,
                "token address must be a full 42-char Ethereum address");
            balance.TokenAddress.Should().MatchRegex("^0x[0-9a-f]{40}$",
                "token address must be lowercase hex");
        }
    }

    /// <summary>
    /// Verifies that V2 balance cache keys use the format "account:tokenAddress" (hex)
    /// matching the CacheWarmupService pattern, not "account:id" (decimal BigInteger).
    /// This is a source-code-level regression guard: if someone changes the SQL back to
    /// 'id' or the reader back to GetFieldValue&lt;BigInteger&gt;, this test catches it.
    /// </summary>
    [Fact]
    public void NotificationListener_V2TransferSql_UsesTokenAddressNotId()
    {
        // Read the source file and verify the SQL uses "tokenAddress" not bare 'id'
        var sourceFile = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Cache", "Circles.Cache.Service", "Services", "NotificationListenerService.cs");

        // Normalize the path
        sourceFile = Path.GetFullPath(sourceFile);

        if (!File.Exists(sourceFile))
        {
            // CI or deployment — source not adjacent to test binary
            return;
        }

        var source = File.ReadAllText(sourceFile);

        // Find the ProcessV2TransfersAsync method DEFINITION (not the call site)
        var methodDef = "private async Task ProcessV2TransfersAsync";
        var methodStart = source.IndexOf(methodDef, StringComparison.Ordinal);
        methodStart.Should().BeGreaterThan(0, "ProcessV2TransfersAsync method definition should exist");

        // Extract the method's SQL + reader section — SQL is ~800 chars, reader access ~400 more
        var methodBody = source.Substring(methodStart, Math.Min(2000, source.Length - methodStart));

        // The SQL SELECT should use tokenAddress (double-quoted in SQL verbatim string)
        // In the source code: ""tokenAddress"" (C# escaped) renders as "tokenAddress" in SQL
        methodBody.Should().Contain("tokenAddress",
            "SQL must SELECT tokenAddress (hex) not id (BigInteger) — PR #188 cache key mismatch");

        // The reader should use GetString for the token column, not GetFieldValue<BigInteger>
        methodBody.Should().Contain("GetString(2)",
            "Reader must use GetString for tokenAddress, not GetFieldValue<BigInteger>");

        // Variable should NOT be named tokenId (old bug pattern)
        methodBody.Should().NotContain("var tokenId",
            "Variable should be tokenAddress not tokenId — PR #188 cache key fix");
    }
}
