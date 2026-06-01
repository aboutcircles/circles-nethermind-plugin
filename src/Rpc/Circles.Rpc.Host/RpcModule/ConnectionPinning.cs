using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Header-gated, per-connection block pinning for the test environment.
///
/// This is deliberately a small, Nethermind-free static helper. The production
/// <see cref="CirclesRpcModule"/> can only be constructed with the Nethermind runtime present
/// (it implements a Nethermind RPC interface), but this security-critical pin logic must be
/// unit-testable in plain CI against a real Postgres. Keeping it isolated here lets
/// <c>BlockPinningRoutingTests</c> exercise the exact SET statements (and the header parsing)
/// without loading any Nethermind assembly.
///
/// PHASED COVERAGE: a request carrying the header is pinned only for views that have a twin in
/// <see cref="PinnedSchema"/>. Views without a twin resolve to <c>public</c> (head) by design —
/// the pinned set grows as later phases add twins. Unqualified view names participate in pinning
/// automatically; <c>public."..."</c>-qualified references in SQL intentionally stay on head until
/// their twin lands. This means a not-yet-pinned view silently returns head under the header; that
/// is an accepted, documented property of the incremental rollout, not a bug.
///
/// MECHANISM differs from the pathfinder's <c>HistoricalLoadGraph</c> (which inlines
/// <c>WHERE "blockNumber" &lt;= N</c> into each query): here the block filter is per-connection
/// session state (a GUC the twins read + a search_path prepend), so correctness depends on Npgsql's
/// default reset-on-return clearing that state between pooled requests. The production connection
/// string MUST therefore keep <c>No Reset On Close=false</c> (the default); see
/// <c>BlockPinningRoutingTests.Pin_DoesNotLeak_AcrossPooledConnections</c>.
/// </summary>
internal static class ConnectionPinning
{
    /// <summary>
    /// Schema holding block-reconstructing twins of the public views. It is created only by the
    /// test-environment service and is absent from every production database. MUST match the schema
    /// name installed by circles-test-environment (SchemaInstallerService) — the two repos are
    /// coupled by this string, and a mismatch degrades silently to "always reads public" because
    /// setting a search_path to a missing schema is not an error.
    /// </summary>
    public const string PinnedSchema = "circles_at_block";

    /// <summary>
    /// Parses an <c>X-Max-Block-Number</c> header value into a pin directive. Returns the block
    /// number only when it is a valid, strictly-positive long; otherwise null (no pin). A
    /// non-positive value (0 / negative) is treated as "no pin" rather than a degenerate pin to an
    /// empty-history reconstruction. Extracted so the inertness-deciding parse is CI-testable.
    /// </summary>
    public static long? ParseMaxBlockHeaderValue(string? rawHeaderValue)
        => long.TryParse(rawHeaderValue, out var block) && block > 0 ? block : null;

    /// <summary>
    /// Pins <paramref name="connection"/> to <paramref name="maxBlockNumber"/> when it is a
    /// strictly-positive block: (1) sets the <c>circles.max_block_number</c> GUC that the pinned
    /// views read via <c>current_setting()</c>, and (2) prepends <see cref="PinnedSchema"/> to the
    /// <c>search_path</c> so unqualified view names (e.g. <c>"V_CrcV2_Avatars"</c>) resolve to the
    /// pinned twin instead of the public view. Both are session-local to this pooled connection and
    /// are cleared by Npgsql's reset-on-return, so they never leak to the next request that reuses
    /// the physical connection.
    ///
    /// When <paramref name="maxBlockNumber"/> is null or non-positive — i.e. every production
    /// request, which never carries the header — this is a no-op: the connection keeps its default
    /// <c>public</c> search_path with no GUC set, byte-identical to pre-pinning behavior. Setting
    /// the search_path to a schema that does not exist (production, or before the test-env installs
    /// it) is not an error in Postgres; unqualified names simply fall through to <c>public</c>.
    /// </summary>
    public static async Task ApplyAsync(NpgsqlConnection connection, long? maxBlockNumber, ILogger? logger = null)
    {
        // null / 0 / negative → no pin. Keeps the production path inert and avoids a degenerate
        // pin to a non-positive block.
        if (maxBlockNumber is not > 0)
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        // maxBlockNumber is a parsed long (header parsed to long?), never raw user text → safe to interpolate.
        cmd.CommandText =
            $"SET circles.max_block_number = {maxBlockNumber.Value}; " +
            $"SET search_path = {PinnedSchema}, public";
        await cmd.ExecuteNonQueryAsync();

        logger?.LogDebug(
            "Pinned connection to block {BlockNumber} (search_path={Schema},public)",
            maxBlockNumber.Value, PinnedSchema);
    }
}
