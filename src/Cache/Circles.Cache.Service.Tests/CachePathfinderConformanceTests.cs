using System.Numerics;
using System.Text.Json;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Controllers;
using Circles.Cache.Service.Models;
using Circles.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Conformance tests that verify the PathfinderGraphController output correctly
/// filters data through the shared CirclesInvariants predicates.
///
/// These tests use a synthetic cache populated with both registered and unregistered
/// addresses, and verify that the controller output contains ONLY valid entries.
///
/// Key invariants tested:
/// 1. Balances: only registered accounts with registered token owners
/// 2. Trust: only registered truster/trustee, non-group trusters, non-expired
/// 3. GroupTrusts: only router-group trusters, registered trustees
/// 4. Wrappers: only wrappers whose underlying avatar is registered
/// 5. ConsentedFlow: only registered avatars
/// </summary>
public class CachePathfinderConformanceTests
{
    // Registered addresses
    private const string Human1 = "0x0000000000000000000000000000000000000001";
    private const string Human2 = "0x0000000000000000000000000000000000000002";
    private const string Org1 = "0x0000000000000000000000000000000000000003";
    private const string RouterGroup = "0x0000000000000000000000000000000000000004";
    private const string NonRouterGroup = "0x0000000000000000000000000000000000000005";
    private const string StandardTreasuryMint = "0xcdfc5135aec0afbf102c108e7f5c8a88c6112842";

    // Unregistered address (never added to avatars/groups)
    private const string Unregistered = "0x0000000000000000000000000000000000000099";

    // Wrapper addresses
    private const string Wrapper1 = "0x0000000000000000000000000000000000000011";
    private const string WrapperUnregistered = "0x0000000000000000000000000000000000000012";

    private const long Block = 100;
    private const long CurrentTimestamp = 10000;
    // Must be after wall-clock time since BuildTrust uses DateTimeOffset.UtcNow
    private static readonly long FutureExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400 * 365;
    private const long PastExpiry = 500;

    /// <summary>
    /// Creates a cache container seeded with both valid and invalid test data.
    /// </summary>
    private static CacheContainer CreateSeededCache()
    {
        var caches = new CacheContainer(rollbackCapacity: 4);

        // --- Register avatars ---
        caches.V2Avatars.Add(Block, Human1, ("Human", Block));
        caches.V2Avatars.Add(Block, Human2, ("Human", Block));
        caches.V2Avatars.Add(Block, Org1, ("Organization", Block));

        // --- Register groups ---
        caches.Groups.Add(Block, RouterGroup, ("RouterGroup", StandardTreasuryMint, "RG"));
        caches.Groups.Add(Block, NonRouterGroup, ("NonRouterGroup", "0xothermint", "NRG"));

        // --- Wrappers ---
        caches.UpsertWrapper(Block, Wrapper1, Human1, 0); // valid: underlying is registered
        caches.UpsertWrapper(Block, WrapperUnregistered, Unregistered, 0); // INVALID: underlying is unregistered

        // --- V2 Balances ---
        // Valid: registered account, registered token owner (native)
        caches.V2BalancesByAccountAndToken.Add(Block, $"{Human1}:{Human2}", 100m);
        caches.V2LastActivity.Add(Block, $"{Human1}:{Human2}", CurrentTimestamp);
        // Valid: registered account, group token
        caches.V2BalancesByAccountAndToken.Add(Block, $"{Human1}:{RouterGroup}", 50m);
        caches.V2LastActivity.Add(Block, $"{Human1}:{RouterGroup}", CurrentTimestamp);
        // INVALID: unregistered account
        caches.V2BalancesByAccountAndToken.Add(Block, $"{Unregistered}:{Human1}", 200m);
        caches.V2LastActivity.Add(Block, $"{Unregistered}:{Human1}", CurrentTimestamp);
        // INVALID: unregistered token owner
        caches.V2BalancesByAccountAndToken.Add(Block, $"{Human1}:{Unregistered}", 150m);
        caches.V2LastActivity.Add(Block, $"{Human1}:{Unregistered}", CurrentTimestamp);
        // Valid: wrapper token with registered underlying
        caches.V2BalancesByAccountAndToken.Add(Block, $"{Human2}:{Wrapper1}", 75m);
        caches.V2LastActivity.Add(Block, $"{Human2}:{Wrapper1}", CurrentTimestamp);
        // INVALID: wrapper token with unregistered underlying
        caches.V2BalancesByAccountAndToken.Add(Block, $"{Human1}:{WrapperUnregistered}", 60m);
        caches.V2LastActivity.Add(Block, $"{Human1}:{WrapperUnregistered}", CurrentTimestamp);

        // --- V2 Trust ---
        // Valid: human trusts human, not expired
        caches.UpsertV2Trust(Block, Human1, Human2, FutureExpiry);
        // Valid: human trusts group
        caches.UpsertV2Trust(Block, Human1, RouterGroup, FutureExpiry);
        // Valid: human trusts non-router group (this is where old code had a bug!)
        caches.UpsertV2Trust(Block, Human2, NonRouterGroup, FutureExpiry);
        // INVALID: unregistered truster
        caches.UpsertV2Trust(Block, Unregistered, Human1, FutureExpiry);
        // INVALID: unregistered trustee
        caches.UpsertV2Trust(Block, Human1, Unregistered, FutureExpiry);
        // INVALID: expired trust
        caches.UpsertV2Trust(Block, Human2, Org1, PastExpiry);
        // Group trust (should appear in GroupTrusts, NOT in Trust)
        caches.UpsertV2Trust(Block, RouterGroup, Human1, FutureExpiry);
        caches.UpsertV2Trust(Block, RouterGroup, Unregistered, FutureExpiry); // INVALID trustee

        // --- Consented flow flags ---
        var consentFlag = new byte[32];
        consentFlag[31] = 0x01; // bit 0 set = consented
        caches.ConsentedFlowFlags.Add(Block, Human1, consentFlag);
        caches.ConsentedFlowFlags.Add(Block, Unregistered, consentFlag); // INVALID: unregistered

        caches.RebuildSecondaryIndexes();
        return caches;
    }

    private static PathfinderGraphController CreateController(CacheContainer caches)
    {
        var state = new CacheServiceState(rollbackCapacity: 4);
        state.WarmupTargetBlock = Block;
        state.LastProcessedBlock = Block;
        state.WarmupComplete = true;

        var controller = new PathfinderGraphController(
            caches,
            state,
            NullLogger<PathfinderGraphController>.Instance);

        // Create a minimal HttpContext so the controller can read headers
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        return controller;
    }

    // --- Balance Conformance ---

    [Fact]
    public void Balances_ExcludeUnregisteredAccounts()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("balances");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var response = ok!.Value as PathfinderGraphResponse;
        response.Should().NotBeNull();

        var accounts = response!.Balances!.Select(b => b.Account).ToHashSet();
        accounts.Should().NotContain(Unregistered, "unregistered accounts must be filtered");
    }

    [Fact]
    public void Balances_ExcludeUnregisteredTokenOwners()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("balances");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        // Should not contain a balance where tokenAddress = Unregistered
        var tokenAddresses = response!.Balances!.Select(b => b.TokenAddress).ToHashSet();
        tokenAddresses.Should().NotContain(Unregistered, "unregistered token owners must be filtered");
    }

    [Fact]
    public void Balances_ExcludeWrappersWithUnregisteredUnderlying()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("balances");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        var tokenAddresses = response!.Balances!.Select(b => b.TokenAddress).ToHashSet();
        tokenAddresses.Should().NotContain(WrapperUnregistered,
            "wrappers with unregistered underlying avatar must be filtered");
    }

    [Fact]
    public void Balances_IncludeValidEntries()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("balances");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        // Valid entries: Human1:Human2, Human1:RouterGroup, Human2:Wrapper1
        response!.Balances!.Count.Should().Be(3, "exactly 3 valid balance entries should remain");
    }

    // --- Trust Conformance ---

    [Fact]
    public void Trust_ExcludeUnregisteredTrusterOrTrustee()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("trust");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        var addresses = response!.Trust!
            .SelectMany(t => new[] { t.Truster, t.Trustee })
            .ToHashSet();
        addresses.Should().NotContain(Unregistered, "unregistered addresses must not appear in trust edges");
    }

    [Fact]
    public void Trust_ExcludeExpiredEdges()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("trust");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        // Human2 -> Org1 has PastExpiry (500), currentTimestamp is ~now (>>500)
        var human2Trusts = response!.Trust!.Where(t => t.Truster == Human2).Select(t => t.Trustee).ToList();
        human2Trusts.Should().NotContain(Org1, "expired trust edges must be filtered");
    }

    [Fact]
    public void Trust_ExcludeGroupTrusters()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("trust");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        var trusters = response!.Trust!.Select(t => t.Truster).ToHashSet();
        trusters.Should().NotContain(RouterGroup, "group trusters must be excluded from Trust (handled in GroupTrusts)");
    }

    [Fact]
    public void Trust_IncludeNonRouterGroupAsTrustee()
    {
        // BUG FIX verification: old code checked routerGroups.Contains(trustee) which missed non-router groups
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("trust");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        var trustees = response!.Trust!.Select(t => t.Trustee).ToHashSet();
        trustees.Should().Contain(NonRouterGroup,
            "non-router groups as trustees should be included (they're registered avatars)");
    }

    // --- GroupTrust Conformance ---

    [Fact]
    public void GroupTrusts_OnlyRouterGroups()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("grouptrusts");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        var groups = response!.GroupTrusts!.Select(gt => gt.GroupAddress).ToHashSet();
        groups.Should().OnlyContain(g => g == RouterGroup, "only router groups should appear as group trusters");
    }

    [Fact]
    public void GroupTrusts_ExcludeUnregisteredTrustees()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("grouptrusts");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        var trustees = response!.GroupTrusts!.Select(gt => gt.TrustedToken).ToHashSet();
        trustees.Should().NotContain(Unregistered, "unregistered trustees must be filtered from group trusts");
    }

    [Fact]
    public void GroupTrusts_IncludeValidEntries()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("grouptrusts");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        // RouterGroup trusts Human1 (valid), RouterGroup trusts Unregistered (invalid)
        response!.GroupTrusts!.Count.Should().Be(1, "only one valid group trust edge should remain");
        response.GroupTrusts[0].TrustedToken.Should().Be(Human1);
    }

    // --- WrapperMapping Conformance ---

    [Fact]
    public void WrapperMappings_ExcludeUnregisteredUnderlying()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("wrappermappings");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        var underlyingAvatars = response!.WrapperMappings!.Select(w => w.UnderlyingAvatar).ToHashSet();
        underlyingAvatars.Should().NotContain(Unregistered.ToLowerInvariant(),
            "wrappers with unregistered underlying must be filtered");
    }

    [Fact]
    public void WrapperMappings_IncludeValidEntries()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("wrappermappings");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        response!.WrapperMappings!.Count.Should().Be(1, "only one valid wrapper mapping should remain");
        response.WrapperMappings[0].WrapperAddress.Should().Be(Wrapper1);
    }

    // --- ConsentedFlow Conformance ---

    [Fact]
    public void ConsentedFlow_ExcludeUnregisteredAvatars()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("consentedflow");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        var avatars = response!.ConsentedFlow!.Select(c => c.Avatar).ToHashSet();
        avatars.Should().NotContain(Unregistered, "unregistered avatars must not have consented flow flags");
    }

    [Fact]
    public void ConsentedFlow_IncludeValidEntries()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph("consentedflow");

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        response!.ConsentedFlow!.Count.Should().Be(1, "only one valid consented flow flag should remain");
        response.ConsentedFlow[0].Avatar.Should().Be(Human1);
    }

    // --- Cross-cutting: no unregistered address appears in ANY section ---

    [Fact]
    public void FullGraph_NoUnregisteredAddressInAnySection()
    {
        var caches = CreateSeededCache();
        var controller = CreateController(caches);
        var result = controller.GetGraph(null); // all sections

        var ok = result.Result as OkObjectResult;
        var response = ok!.Value as PathfinderGraphResponse;

        // Collect all addresses from all sections
        var allAddresses = new HashSet<string>();

        foreach (var b in response!.Balances!)
        {
            allAddresses.Add(b.Account);
            allAddresses.Add(b.TokenAddress);
        }

        foreach (var t in response.Trust!)
        {
            allAddresses.Add(t.Truster);
            allAddresses.Add(t.Trustee);
        }

        foreach (var gt in response.GroupTrusts!)
        {
            allAddresses.Add(gt.GroupAddress);
            allAddresses.Add(gt.TrustedToken);
        }

        foreach (var c in response.ConsentedFlow!)
        {
            allAddresses.Add(c.Avatar);
        }

        foreach (var w in response.WrapperMappings!)
        {
            allAddresses.Add(w.UnderlyingAvatar);
        }

        allAddresses.Should().NotContain(Unregistered);
        allAddresses.Should().NotContain(Unregistered.ToLowerInvariant());
    }
}
