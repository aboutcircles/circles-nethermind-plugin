using System.Globalization;
using System.Numerics;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Models;
using Circles.Common;
using Microsoft.AspNetCore.Mvc;

namespace Circles.Cache.Service.Controllers;

/// <summary>
/// Serves the full pathfinder graph snapshot from in-memory caches (zero SQL).
/// The pathfinder fetches this periodically to build its capacity graph.
/// Supports ETag-based conditional requests and selective section inclusion.
/// </summary>
[ApiController]
[Route("api/pathfinder")]
public class PathfinderGraphController : ControllerBase
{
    private readonly CacheContainer _caches;
    private readonly CacheServiceState _state;
    private readonly ILogger<PathfinderGraphController> _logger;

    /// <summary>
    /// Standard treasury mint address — only groups with this mint policy
    /// participate in the pathfinder's transitive transfer routing.
    /// </summary>
    private const string StandardTreasuryMint = "0xcdfc5135aec0afbf102c108e7f5c8a88c6112842";

    private const int SchemaVersion = 1;

    /// <summary>V2 Hub epoch on gnosis mainnet: 2020-10-15 00:00 UTC (same as V1).</summary>
    private const uint V2InflationDayZero = 1_602_720_000;
    private const long SecondsPerDay = 86_400;

    public PathfinderGraphController(
        CacheContainer caches,
        CacheServiceState state,
        ILogger<PathfinderGraphController> logger)
    {
        _caches = caches;
        _state = state;
        _logger = logger;
    }

    /// <summary>
    /// Returns the full pathfinder graph snapshot from in-memory caches.
    /// Supports ?include=balances,trust,groups,groupTrusts,consentedFlow,avatars,wrapperMappings for selective loading.
    /// ETag is based on LastProcessedBlock for conditional 304 responses.
    /// </summary>
    [HttpGet("graph")]
    public ActionResult<PathfinderGraphResponse> GetGraph([FromQuery] string? include = null)
    {
        if (!_state.WarmupComplete)
        {
            return StatusCode(503, new { error = "Cache warmup in progress" });
        }

        var lastBlock = _state.LastProcessedBlock;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var etag = $"\"{lastBlock}\"";

        // ETag-based conditional request
        if (Request.Headers.IfNoneMatch.ToString() == etag)
        {
            return StatusCode(304);
        }

        // Parse include filter (default: all sections)
        var sections = ParseInclude(include);

        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "no-cache";

        try
        {
            // Pre-compute shared lookups once per request
            var registrations = new CacheRegistrationSet(_caches);
            var wrapperLookup = new CacheWrapperLookup(_caches);
            var routerGroups = sections.Contains("trust") || sections.Contains("groups") || sections.Contains("grouptrusts")
                ? GetRouterFilteredGroups()
                : null;
            var avatarToWrappers = sections.Contains("trust")
                ? BuildAvatarToWrappersIndex()
                : null;

            var response = new PathfinderGraphResponse(
                SchemaVersion: SchemaVersion,
                LastProcessedBlock: lastBlock,
                Timestamp: timestamp,
                Balances: sections.Contains("balances") ? BuildBalances(registrations, wrapperLookup) : null,
                Trust: sections.Contains("trust") ? BuildTrust(registrations, routerGroups!, avatarToWrappers!) : null,
                Groups: sections.Contains("groups") ? BuildGroups(routerGroups!) : null,
                GroupTrusts: sections.Contains("grouptrusts") ? BuildGroupTrusts(registrations, routerGroups!) : null,
                ConsentedFlow: sections.Contains("consentedflow") ? BuildConsentedFlow(registrations) : null,
                Avatars: sections.Contains("avatars") ? BuildAvatars() : null,
                WrapperMappings: sections.Contains("wrappermappings") ? BuildWrapperMappings(registrations) : null
            );

            _logger.LogDebug(
                "Pathfinder graph snapshot: block={Block}, balances={Balances}, trust={Trust}, groups={Groups}, groupTrusts={GroupTrusts}, consent={Consent}, avatars={Avatars}, wrappers={Wrappers}",
                lastBlock,
                response.Balances?.Count ?? 0,
                response.Trust?.Count ?? 0,
                response.Groups?.Count ?? 0,
                response.GroupTrusts?.Count ?? 0,
                response.ConsentedFlow?.Count ?? 0,
                response.Avatars?.Count ?? 0,
                response.WrapperMappings?.Count ?? 0);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building pathfinder graph snapshot");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    private static HashSet<string> ParseInclude(string? include)
    {
        if (string.IsNullOrWhiteSpace(include))
            return new HashSet<string> { "balances", "trust", "groups", "grouptrusts", "consentedflow", "avatars", "wrappermappings" };

        return new HashSet<string>(
            include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant()));
    }

    /// <summary>
    /// Builds balance rows from V2BalancesByAccountAndToken.
    /// Converts cached decimal balances to attoCircles integer strings (matching LoadGraph output).
    /// Applies demurrage from lastActivity → now for demurraged balances.
    /// Converts static (inflationary) balances to demurraged equivalent at target day.
    /// </summary>
    private List<PathfinderBalanceRow> BuildBalances(IRegistrationSet registrations, IWrapperLookup wrapperLookup)
    {
        var balances = new List<PathfinderBalanceRow>();
        var targetDay = CirclesConverter.DayFromTimestamp(DateTimeOffset.UtcNow, V2InflationDayZero);

        foreach (var kvp in _caches.V2BalancesByAccountAndToken.ReadOnlyDictionary)
        {
            if (kvp.Value <= 0)
                continue;

            var separatorIndex = kvp.Key.IndexOf(':');
            if (separatorIndex < 0)
                continue;

            var account = kvp.Key[..separatorIndex];
            var tokenAddress = kvp.Key[(separatorIndex + 1)..];

            // Shared invariant: account + token must be registered
            if (!CirclesInvariants.IsValidBalance(account, tokenAddress, registrations, wrapperLookup))
                continue;

            // Determine if this is a wrapper token (for demurrage handling)
            var isWrapped = false;
            var isStatic = false;

            if (_caches.Erc20WrapperAddresses.TryGetValue(tokenAddress, out var wrapperInfo))
            {
                isWrapped = true;
                isStatic = wrapperInfo.CirclesType == 1;
            }

            // Convert decimal Circles → attoCircles BigInteger
            var attoBalance = CirclesConverter.CirclesToAttoCircles(kvp.Value);
            if (attoBalance == BigInteger.Zero)
                continue;

            // Get last activity timestamp
            _caches.V2LastActivity.TryGetValue(kvp.Key, out var lastActivity);

            if (isStatic)
            {
                // Static (inflationary) → convert to demurraged equivalent at target day
                attoBalance = CirclesConverter.InflationaryToDemurrage(attoBalance, targetDay);
            }
            else if (lastActivity == 0 && !_caches.V2LastActivity.ContainsKey(kvp.Key))
            {
                // lastActivity=0 from TryGetValue default means the entry is missing.
                // Emitting a demurraged balance without decay would overestimate capacity
                // and cause ERC1155InsufficientBalance reverts on-chain.
                continue;
            }
            else if (lastActivity >= V2InflationDayZero)
            {
                // Demurraged: apply demurrage from lastActivity → now
                var lastActivityDay = (ulong)(lastActivity - V2InflationDayZero) / (ulong)SecondsPerDay;
                var daysDelta = targetDay > lastActivityDay ? targetDay - lastActivityDay : 0;
                if (daysDelta > 0)
                {
                    attoBalance = CirclesConverter.InflationaryToDemurrage(attoBalance, daysDelta);
                }
            }

            if (attoBalance == BigInteger.Zero)
                continue;

            balances.Add(new PathfinderBalanceRow(
                Balance: attoBalance.ToString(CultureInfo.InvariantCulture),
                Account: account,
                TokenAddress: tokenAddress,
                LastActivity: lastActivity,
                IsWrapped: isWrapped,
                CirclesType: isStatic ? "static" : "demurraged"
            ));
        }

        return balances;
    }

    /// <summary>
    /// Builds trust rows from V2TrustRelations.
    /// Filters: registered avatars only, non-revoked, non-group trusters.
    /// Derives wrapper trust edges: if trustee has a wrapper, emit (truster, wrapperAddress) too.
    /// </summary>
    private List<PathfinderTrustRow> BuildTrust(
        IRegistrationSet registrations,
        HashSet<string> routerGroups,
        Dictionary<string, List<string>> avatarToWrappers)
    {
        var trust = new List<PathfinderTrustRow>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var kvp in _caches.V2TrustRelations.ReadOnlyDictionary)
        {
            var separatorIndex = kvp.Key.IndexOf(':');
            if (separatorIndex < 0)
                continue;

            var truster = kvp.Key[..separatorIndex];
            var trustee = kvp.Key[(separatorIndex + 1)..];
            var expiryTime = kvp.Value;

            // Shared invariant: both registered, not expired, non-group truster
            if (!CirclesInvariants.IsValidTrustEdge(truster, trustee, expiryTime, now, registrations))
                continue;

            // Native trust edge
            trust.Add(new PathfinderTrustRow(
                Truster: truster,
                Trustee: trustee,
                Limit: 100
            ));

            // Derive wrapper trust edges: O(1) lookup via pre-built reverse index
            if (avatarToWrappers.TryGetValue(trustee, out var wrapperAddresses))
            {
                foreach (var wrapperAddr in wrapperAddresses)
                {
                    trust.Add(new PathfinderTrustRow(
                        Truster: truster,
                        Trustee: wrapperAddr,
                        Limit: 100
                    ));
                }
            }
        }

        return trust;
    }

    /// <summary>
    /// Builds group rows — only groups using the standard treasury (router).
    /// </summary>
    private static List<PathfinderGroupRow> BuildGroups(HashSet<string> routerGroups)
    {
        var groups = new List<PathfinderGroupRow>(routerGroups.Count);
        foreach (var groupAddr in routerGroups)
        {
            groups.Add(new PathfinderGroupRow(GroupAddress: groupAddr));
        }
        return groups;
    }

    /// <summary>
    /// Builds group trust rows — trust edges where truster is a router-filtered group.
    /// </summary>
    private List<PathfinderGroupTrustRow> BuildGroupTrusts(IRegistrationSet registrations, HashSet<string> routerGroups)
    {
        var groupTrusts = new List<PathfinderGroupTrustRow>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var kvp in _caches.V2TrustRelations.ReadOnlyDictionary)
        {
            var separatorIndex = kvp.Key.IndexOf(':');
            if (separatorIndex < 0)
                continue;

            var truster = kvp.Key[..separatorIndex];
            var trustee = kvp.Key[(separatorIndex + 1)..];
            var expiryTime = kvp.Value;

            // Shared invariant: router group truster, registered trustee, not expired
            if (!CirclesInvariants.IsValidGroupTrustEdge(truster, trustee, expiryTime, now, registrations, routerGroups))
                continue;

            groupTrusts.Add(new PathfinderGroupTrustRow(
                GroupAddress: truster,
                TrustedToken: trustee
            ));
        }

        return groupTrusts;
    }

    /// <summary>
    /// Returns all registered V2 avatar addresses from the cache.
    /// Hub.sol considers humans, organizations, AND groups as registered avatars
    /// (avatars[addr] != address(0) for all three types). The pathfinder uses this
    /// list to populate RegisteredAvatarIds for graph construction filtering.
    /// </summary>
    private List<string> BuildAvatars()
    {
        var avatars = new List<string>(_caches.V2Avatars.Count + _caches.Groups.Count);
        foreach (var address in _caches.V2Avatars.ReadOnlyDictionary.Keys)
        {
            avatars.Add(address);
        }
        // Groups are registered avatars in Hub.sol — must be included
        foreach (var address in _caches.Groups.ReadOnlyDictionary.Keys)
        {
            avatars.Add(address);
        }
        return avatars;
    }

    /// <summary>
    /// Builds wrapper→avatar mapping rows from the Erc20WrapperAddresses cache.
    /// Only includes wrappers whose underlying avatar is registered.
    /// </summary>
    private List<PathfinderWrapperMappingRow> BuildWrapperMappings(IRegistrationSet registrations)
    {
        var mappings = new List<PathfinderWrapperMappingRow>();
        foreach (var kvp in _caches.Erc20WrapperAddresses.ReadOnlyDictionary)
        {
            // Shared invariant: underlying avatar must be registered
            if (!CirclesInvariants.IsValidWrapperMapping(kvp.Value.Avatar, registrations))
                continue;

            mappings.Add(new PathfinderWrapperMappingRow(
                WrapperAddress: kvp.Key,
                UnderlyingAvatar: kvp.Value.Avatar
            ));
        }
        return mappings;
    }

    /// <summary>
    /// Builds consented flow rows from the ConsentedFlowFlags cache.
    /// Extracts bit 0 of byte[31] to determine consent status.
    /// </summary>
    private List<PathfinderConsentedFlowRow> BuildConsentedFlow(IRegistrationSet registrations)
    {
        var consent = new List<PathfinderConsentedFlowRow>();

        foreach (var kvp in _caches.ConsentedFlowFlags.ReadOnlyDictionary)
        {
            // Shared invariant: avatar must be registered
            if (!CirclesInvariants.IsValidConsentedFlowFlag(kvp.Key, registrations))
                continue;

            var flagBytes = kvp.Value;

            if (flagBytes.Length < 32)
            {
                _logger.LogWarning("ConsentedFlowFlags for avatar {Avatar} has unexpected length {Length}", kvp.Key, flagBytes.Length);
                continue;
            }

            // bytes32 flag — bit 0 of the last byte (index 31) indicates consented flow
            var hasConsent = (flagBytes[31] & 0x01) != 0;

            consent.Add(new PathfinderConsentedFlowRow(
                Avatar: kvp.Key,
                HasConsentedFlow: hasConsent
            ));
        }

        return consent;
    }

    /// <summary>
    /// Builds a reverse index: avatar address → list of wrapper addresses.
    /// Used for O(1) wrapper trust edge derivation instead of O(N) scan.
    /// </summary>
    private Dictionary<string, List<string>> BuildAvatarToWrappersIndex()
    {
        var index = new Dictionary<string, List<string>>();
        foreach (var kvp in _caches.Erc20WrapperAddresses.ReadOnlyDictionary)
        {
            var avatar = kvp.Value.Avatar;
            if (!index.TryGetValue(avatar, out var wrappers))
            {
                wrappers = new List<string>(1);
                index[avatar] = wrappers;
            }
            wrappers.Add(kvp.Key); // wrapper address
        }
        return index;
    }

    /// <summary>
    /// Returns the set of group addresses that use the standard treasury.
    /// </summary>
    private HashSet<string> GetRouterFilteredGroups()
    {
        var result = new HashSet<string>();
        foreach (var kvp in _caches.Groups.ReadOnlyDictionary)
        {
            if (kvp.Value.Mint.Equals(StandardTreasuryMint, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(kvp.Key);
            }
        }
        return result;
    }
}
