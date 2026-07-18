using System.Globalization;
using System.Text.Json.Serialization;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Community ("multi-affiliate-group willingness") methods for CirclesRpcModule.
///
/// A community = an underlying Circles group + a bilateral handshake (GA 2.0). Backed by the
/// MultiAffiliateGroupRegistry contract: an avatar signals on-chain *intent* to join a community
/// via AffiliateGroupAdded / AffiliateGroupRemoved. The registry stores intent only — it does NOT
/// enforce the membership-fee cap or any criteria. Those are computed off-chain here (fee % from
/// the community's group profile) and in the TMS (trust reconciliation).
///
/// Current intent ("wishlist") is read from the latest-event-wins view V_CrcV2_AffiliateGroupMembers
/// (the DB objects keep the contract-derived "affiliate" name). The "trusted" subset additionally
/// requires the community to trust the avatar on-chain (V_CrcV2_TrustRelations: truster=community,
/// trustee=avatar, not expired) — i.e. the bilateral handshake. Each community's membership fee is the
/// group profile's `membershipCriteria.membershipFee` (a percent in [0,100] of daily gCRC mint,
/// enforced by the profile pinning service), resolved through the ipfs_files jsonb payload and summed
/// into a totalFeePercentage for the 100%-cap check. The fee is null when a profile carries no
/// membershipCriteria; absent fees contribute 0 to the total.
///
/// Recompute-per-request (no cache-service path). NOTE: the V_CrcV2_AffiliateGroupMembers /
/// V_CrcV2_AffiliateGroupSeedConflicts views have no circles_at_block twins yet, so X-Max-Block-Number
/// is currently a NO-OP for these endpoints — they always read head. Add twins in the test-env repo to
/// make them block-pinnable (the trusted-subset join to V_CrcV2_TrustRelations is also head-only here).
/// </summary>
public partial class CirclesRpcModule
{
    // ------------------------------------------------------------------
    // Per-avatar endpoints
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the communities an avatar has signalled intent to join (the wishlist), each with its
    /// membership fee, plus the total committed fee percentage across all of them.
    /// </summary>
    public async Task<AvatarCommunityListResponse> GetAvatarCommunitiesWishlist(string avatar)
    {
        avatar = ValidateAndNormalizeAddress(avatar, nameof(avatar));
        await using var connection = await CreateConnectionAsync();
        var rows = await LoadAvatarCommunityRowsAsync(connection, avatar, trustedOnly: false);
        return new AvatarCommunityListResponse(SumFees(rows), rows.ToArray());
    }

    /// <summary>
    /// Returns the confirmed-membership subset of the wishlist: communities that currently trust the
    /// avatar on-chain. Reflects TMS delay (a wished community only appears once it trusts the
    /// avatar). The totalFeePercentage is summed over this confirmed subset.
    /// </summary>
    public async Task<AvatarCommunityListResponse> GetAvatarCommunities(string avatar)
    {
        avatar = ValidateAndNormalizeAddress(avatar, nameof(avatar));
        await using var connection = await CreateConnectionAsync();
        var rows = await LoadAvatarCommunityRowsAsync(connection, avatar, trustedOnly: true);
        return new AvatarCommunityListResponse(SumFees(rows), rows.ToArray());
    }

    /// <summary>
    /// Returns the avatar's current total committed fee percentage across all communities it has
    /// signalled intent to join (the wishlist/intent set). Used by GA to block joins that would
    /// exceed 100% and by the TMS to enforce the cap off-chain.
    /// </summary>
    public async Task<CommunityFeesPercentageResponse> GetAvatarCommunityFeesPercentage(string avatar)
    {
        avatar = ValidateAndNormalizeAddress(avatar, nameof(avatar));
        await using var connection = await CreateConnectionAsync();
        var rows = await LoadAvatarCommunityRowsAsync(connection, avatar, trustedOnly: false);
        return new CommunityFeesPercentageResponse(SumFees(rows));
    }

    // ------------------------------------------------------------------
    // Per-community endpoints
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the avatars that have signalled intent to join the given community (the members
    /// wishlist). This is the endpoint the TMS reads to reconcile trust. Paginated.
    /// </summary>
    public async Task<PagedResponse<CommunityMemberRow>> GetCommunityMembersWishlist(
        string communityAddress, int limit = 100, string? cursor = null)
    {
        communityAddress = ValidateAndNormalizeAddress(communityAddress, nameof(communityAddress));
        return await GetCommunityMembersInternal(communityAddress, limit, cursor, trustedOnly: false);
    }

    /// <summary>
    /// Returns the confirmed-membership subset of a community's wishlist: avatars the community
    /// actually trusts on-chain. Reflects TMS delay. Paginated.
    /// </summary>
    public async Task<PagedResponse<CommunityMemberRow>> GetCommunityMembers(
        string communityAddress, int limit = 100, string? cursor = null)
    {
        communityAddress = ValidateAndNormalizeAddress(communityAddress, nameof(communityAddress));
        return await GetCommunityMembersInternal(communityAddress, limit, cursor, trustedOnly: true);
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private async Task<List<AvatarCommunityRow>> LoadAvatarCommunityRowsAsync(
        NpgsqlConnection connection, string avatar, bool trustedOnly)
    {
        // trustedOnly: require the community to currently trust the avatar (bilateral handshake).
        var trustJoin = trustedOnly
            ? @"INNER JOIN ""V_CrcV2_TrustRelations"" t
                    ON t.truster = m.""affiliateGroup"" AND t.trustee = m.avatar"
            : "";

        var sql = $@"
            SELECT
                m.""affiliateGroup"",
                g.name AS community_name,
                f.payload->'membershipCriteria'->>'membershipFee' AS membership_fee,
                m.""timestamp""
            FROM ""V_CrcV2_AffiliateGroupMembers"" m
            {trustJoin}
            LEFT JOIN ""CrcV2_RegisterGroup"" g ON g.""group"" = m.""affiliateGroup""
            LEFT JOIN ""V_CrcV2_Avatars"" a ON a.avatar = m.""affiliateGroup""
            LEFT JOIN ipfs_files f ON f.metadata_digest = a.""cidV0Digest""
            WHERE m.avatar = @avatar
            ORDER BY m.""timestamp"" DESC, m.""affiliateGroup""
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("avatar", avatar));

        var rows = new List<AvatarCommunityRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var communityAddress = reader.GetString(0);
            var communityName = reader.IsDBNull(1) ? null : reader.GetString(1);
            var feeRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
            var timestamp = reader.GetInt64(3);

            rows.Add(new AvatarCommunityRow(communityName, communityAddress, ParseFee(feeRaw), timestamp));
        }

        return rows;
    }

    private async Task<PagedResponse<CommunityMemberRow>> GetCommunityMembersInternal(
        string communityAddress, int limit, string? cursor, bool trustedOnly)
    {
        // Clamp to a sane page size — an unbounded LIMIT would be a result-set DoS (matches the
        // clamping convention used by the other paginated circles_* endpoints).
        limit = Math.Clamp(limit, 1, 1000);

        await using var connection = await CreateConnectionAsync();
        var (cursorBlock, cursorTxIndex, cursorLogIndex) = CursorUtils.DecodeCursor(cursor);

        var trustJoin = trustedOnly
            ? @"INNER JOIN ""V_CrcV2_TrustRelations"" t
                    ON t.truster = m.""affiliateGroup"" AND t.trustee = m.avatar"
            : "";

        var parameters = new List<NpgsqlParameter> { new("community", communityAddress) };

        // Keyset cursor predicate lives INSIDE the page CTE so the LIMIT bounds the page before any
        // name enrichment runs.
        var cursorPredicate = "";
        if (cursorBlock.HasValue && cursorTxIndex.HasValue && cursorLogIndex.HasValue)
        {
            cursorPredicate = @"
                AND (m.""blockNumber"", m.""transactionIndex"", m.""logIndex"") < (@cursorBlock, @cursorTxIndex, @cursorLogIndex)";
            parameters.Add(new NpgsqlParameter("cursorBlock", cursorBlock.Value));
            parameters.Add(new NpgsqlParameter("cursorTxIndex", cursorTxIndex.Value));
            parameters.Add(new NpgsqlParameter("cursorLogIndex", cursorLogIndex.Value));
        }
        parameters.Add(new NpgsqlParameter("limit", limit + 1));

        // Two-stage so name enrichment never drives the query plan:
        //  1. `page` (MATERIALIZED) selects + orders + LIMITs the bounded page straight off the cheap
        //     V_CrcV2_AffiliateGroupMembers view (its reset/window logic is ~tens of ms).
        //  2. `names` resolves avatar names ONLY for that page's avatars, scoped via
        //     `= ANY(ARRAY(SELECT avatar FROM page))`, so the expensive (un-indexable) V_CrcV2_Avatars
        //     view is touched once via its PK index instead of being re-scanned per member.
        // Without this fence the planner mis-estimates the view at 1 row and nested-loops V_CrcV2_Avatars
        // once per member (measured 37s for a ~2.8k-member community; this rewrite: ~0.2s).
        var sql = $@"
            WITH page AS MATERIALIZED (
                SELECT m.""blockNumber"", m.""timestamp"", m.""transactionIndex"", m.""logIndex"", m.avatar
                FROM ""V_CrcV2_AffiliateGroupMembers"" m
                {trustJoin}
                WHERE m.""affiliateGroup"" = @community{cursorPredicate}
                ORDER BY m.""blockNumber"" DESC, m.""transactionIndex"" DESC, m.""logIndex"" DESC
                LIMIT @limit
            ),
            names AS (
                SELECT a.avatar, f.payload->>'name' AS avatar_name
                FROM ""V_CrcV2_Avatars"" a
                LEFT JOIN ipfs_files f ON f.metadata_digest = a.""cidV0Digest""
                WHERE a.avatar = ANY(ARRAY(SELECT avatar FROM page))
            )
            SELECT p.""blockNumber"", p.""timestamp"", p.""transactionIndex"", p.""logIndex"", p.avatar, n.avatar_name
            FROM page p
            LEFT JOIN names n ON n.avatar = p.avatar
            ORDER BY p.""blockNumber"" DESC, p.""transactionIndex"" DESC, p.""logIndex"" DESC
        ";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters.ToArray());

        var results = new List<CommunityMemberRow>();
        var cursorData = new List<(long blockNumber, int txIndex, int logIndex)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var blockNumber = reader.GetInt64(0);
            var timestamp = reader.GetInt64(1);
            var txIndex = reader.GetInt32(2);
            var logIndex = reader.GetInt32(3);
            var avatar = reader.GetString(4);
            var avatarName = reader.IsDBNull(5) ? null : reader.GetString(5);

            results.Add(new CommunityMemberRow(avatarName, avatar, timestamp));
            cursorData.Add((blockNumber, txIndex, logIndex));
        }

        var hasMore = results.Count > limit;
        if (hasMore)
        {
            results.RemoveAt(results.Count - 1);
            cursorData.RemoveAt(cursorData.Count - 1);
        }

        string? nextCursor = null;
        if (hasMore && cursorData.Count > 0)
        {
            var last = cursorData[^1];
            nextCursor = CursorUtils.EncodeCursor(last.blockNumber, last.txIndex, last.logIndex);
        }

        return new PagedResponse<CommunityMemberRow>(results.ToArray(), hasMore, nextCursor);
    }

    private static decimal SumFees(IEnumerable<AvatarCommunityRow> rows) =>
        rows.Aggregate(0m, (acc, r) => acc + (r.MembershipFee ?? 0m));

    /// <summary>
    /// Parses a community profile's <c>membershipFee</c> value defensively.
    ///
    /// By the profile schema the field is a JSON number, but a community could publish a malformed profile
    /// document directly to IPFS. <c>payload->&gt;'membershipFee'</c> yields the value's text form whether
    /// it was stored as a JSON number (<c>0.1</c>) or a JSON string (<c>"0.1"</c>), so both shapes parse.
    /// A stray trailing '%' is tolerated. Anything else — non-numeric, negative, or absent — is treated as
    /// "no fee" (null → contributes 0 to <c>totalFeePercentage</c>), so a single mis-published community can
    /// never break or under-count the 100%-cap sum.
    ///
    /// The value is a percent in [0,100] per the profile pinning service's schema (MIN/MAX 0..100),
    /// summed verbatim so the totalFeePercentage and the GA 100%-cap check use the same scale.
    /// </summary>
    internal static decimal? ParseFee(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.EndsWith('%')) s = s[..^1].Trim();
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var fee) && fee >= 0m
            ? fee
            : null;
    }
}

// ========================================================================
// Community DTOs
// ========================================================================

/// <summary>
/// One community in a per-avatar wishlist / confirmed-membership list.
/// <c>membershipFee</c> is the community group profile's membershipCriteria.membershipFee — a percent
/// in [0,100] of daily gCRC mint — or null when the community sets no membership criteria.
/// </summary>
public record AvatarCommunityRow(
    [property: JsonPropertyName("communityName")] string? CommunityName,
    [property: JsonPropertyName("communityAddress")] string CommunityAddress,
    [property: JsonPropertyName("membershipFee")] decimal? MembershipFee,
    [property: JsonPropertyName("timestamp")] long Timestamp
);

/// <summary>
/// Per-avatar wishlist / confirmed-membership response: the communities plus the summed total fee.
/// </summary>
public record AvatarCommunityListResponse(
    [property: JsonPropertyName("totalFeePercentage")] decimal TotalFeePercentage,
    [property: JsonPropertyName("communities")] AvatarCommunityRow[] Communities
);

/// <summary>
/// One member in a per-community members wishlist / confirmed-members list.
/// </summary>
public record CommunityMemberRow(
    [property: JsonPropertyName("avatarName")] string? AvatarName,
    [property: JsonPropertyName("avatarAddress")] string AvatarAddress,
    [property: JsonPropertyName("timestamp")] long Timestamp
);

/// <summary>
/// Response for circles_getAvatarCommunityFeesPercentage: the avatar's total committed fee %.
/// </summary>
public record CommunityFeesPercentageResponse(
    [property: JsonPropertyName("totalFeePercentage")] decimal TotalFeePercentage
);
