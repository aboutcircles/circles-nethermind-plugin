using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Models;
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
    /// Supports ?include=balances,trust,groups,groupTrusts,consentedFlow for selective loading.
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
                Balances: sections.Contains("balances") ? BuildBalances() : null,
                Trust: sections.Contains("trust") ? BuildTrust(routerGroups!, avatarToWrappers!) : null,
                Groups: sections.Contains("groups") ? BuildGroups(routerGroups!) : null,
                GroupTrusts: sections.Contains("grouptrusts") ? BuildGroupTrusts(routerGroups!) : null,
                ConsentedFlow: sections.Contains("consentedflow") ? BuildConsentedFlow() : null
            );

            _logger.LogDebug(
                "Pathfinder graph snapshot: block={Block}, balances={Balances}, trust={Trust}, groups={Groups}, groupTrusts={GroupTrusts}, consent={Consent}",
                lastBlock,
                response.Balances?.Count ?? 0,
                response.Trust?.Count ?? 0,
                response.Groups?.Count ?? 0,
                response.GroupTrusts?.Count ?? 0,
                response.ConsentedFlow?.Count ?? 0);

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
            return new HashSet<string> { "balances", "trust", "groups", "grouptrusts", "consentedflow" };

        return new HashSet<string>(
            include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant()));
    }

    /// <summary>
    /// Builds balance rows from V2BalancesByAccountAndToken.
    /// Only includes balances for registered V2 avatars.
    /// Marks wrapper tokens and includes circlesType metadata.
    /// </summary>
    private List<PathfinderBalanceRow> BuildBalances()
    {
        var balances = new List<PathfinderBalanceRow>();

        foreach (var kvp in _caches.V2BalancesByAccountAndToken.ReadOnlyDictionary)
        {
            if (kvp.Value <= 0)
                continue;

            var separatorIndex = kvp.Key.IndexOf(':');
            if (separatorIndex < 0)
                continue;

            var account = kvp.Key[..separatorIndex];
            var tokenAddress = kvp.Key[(separatorIndex + 1)..];

            // Only include balances for registered V2 avatars
            if (!_caches.V2Avatars.ContainsKey(account))
                continue;

            // Determine if this is a wrapper token
            var isWrapped = false;
            var circlesType = "Demurrage"; // default for native ERC1155

            if (_caches.Erc20WrapperAddresses.TryGetValue(tokenAddress, out var wrapperInfo))
            {
                isWrapped = true;
                // Verify the underlying avatar is registered
                if (!_caches.V2Avatars.ContainsKey(wrapperInfo.Avatar))
                    continue;

                // circlesType: 0 = demurraged, 1 = inflationary/static
                circlesType = wrapperInfo.CirclesType == 1 ? "Static" : "Demurrage";
            }

            // Get last activity timestamp for demurrage calculation
            _caches.V2LastActivity.TryGetValue(kvp.Key, out var lastActivity);

            balances.Add(new PathfinderBalanceRow(
                Balance: kvp.Value.ToString(),
                Account: account,
                TokenAddress: tokenAddress,
                LastActivity: lastActivity,
                IsWrapped: isWrapped,
                CirclesType: circlesType
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

            // Skip revoked trust (expiryTime == 0 means indefinite/never set which is active)
            if (expiryTime > 0 && expiryTime <= now)
                continue;

            // Both must be registered avatars
            if (!_caches.V2Avatars.ContainsKey(truster) || !_caches.V2Avatars.ContainsKey(trustee))
                continue;

            // Skip group trusters — they're handled separately in GroupTrusts
            if (routerGroups.Contains(truster))
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
    private List<PathfinderGroupTrustRow> BuildGroupTrusts(HashSet<string> routerGroups)
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

            // Only group trusters from the router-filtered set
            if (!routerGroups.Contains(truster))
                continue;

            // Skip revoked trust
            if (expiryTime > 0 && expiryTime <= now)
                continue;

            groupTrusts.Add(new PathfinderGroupTrustRow(
                GroupAddress: truster,
                TrustedToken: trustee
            ));
        }

        return groupTrusts;
    }

    /// <summary>
    /// Builds consented flow rows from the ConsentedFlowFlags cache.
    /// Extracts bit 0 of byte[31] to determine consent status.
    /// </summary>
    private List<PathfinderConsentedFlowRow> BuildConsentedFlow()
    {
        var consent = new List<PathfinderConsentedFlowRow>();

        foreach (var kvp in _caches.ConsentedFlowFlags.ReadOnlyDictionary)
        {
            // Only include flags for registered V2 avatars (mirrors BuildBalances/BuildTrust filters)
            if (!_caches.V2Avatars.ContainsKey(kvp.Key))
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
