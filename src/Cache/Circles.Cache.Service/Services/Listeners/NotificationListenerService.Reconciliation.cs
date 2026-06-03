using System.Numerics;
using Circles.Cache.Service.Metrics;
using Circles.Common;
using Npgsql;

namespace Circles.Cache.Service.Services;

/// <summary>
/// Tail self-heal reconciliation (#74).
///
/// <para>The incremental balance path (<see cref="ProcessV2TransfersAsync"/>) is an optimization:
/// it applies per-block deltas and advances <c>LastProcessedBlock</c> past each block, never to
/// re-read it. A reorg can leave a one-sided over-credit in that running total (an IN applied
/// without its matching OUT), and because the block is never revisited, the drift sticks until a
/// full re-warmup. Observed in production as a group-mint router (BaseGroupMintRouter) the cache
/// reported sourcing 9–115 CRC of group tokens it nets to zero on-chain; a block-pinned (DB-backed)
/// replay at the same height gave the correct ~0.</para>
///
/// <para>The DB is authoritative and self-consistent: aggregating all transfers <c>blockNumber &lt;=
/// head</c> yields the correct balance for every account at every height. This pass re-derives the
/// balances of accounts active in the recent window straight from that aggregation (the same
/// <c>SUM(delta) HAVING &gt; 0</c> the warmup uses) and overwrites any cache entry that diverged.
/// A net-zero account simply returns no row, so its phantom balance is healed to 0 and dropped from
/// the read index. This is trigger-agnostic: it corrects the drift regardless of which reorg
/// interleaving produced it.</para>
///
/// <para>Reconciliation runs inline under <c>_notificationGate</c> (never on a background thread):
/// it writes via <see cref="RollbackCache{TKey,TValue}.Add"/> at the cache head, which requires
/// monotonically non-decreasing block numbers — a concurrent pass at an older block while the next
/// block is being processed would violate that invariant.</para>
/// </summary>
public partial class NotificationListenerService
{
    /// <summary>
    /// Sub-µCRC tolerance below which a cache/authoritative difference is treated as equal. Raw
    /// balance sums are exact in <see cref="decimal"/>, so a real drift (the #74 phantom is 9–115
    /// CRC) is orders of magnitude above this; the guard only avoids churn on negligible noise.
    /// </summary>
    private const decimal ReconcileEpsilon = 0.0000005m;

    /// <summary>
    /// Re-derives the balances of accounts active in the recent window from the authoritative DB
    /// aggregation (pinned to <paramref name="toBlock"/> = the cache head) and overwrites any cache
    /// entry that drifted. Returns the number of corrections applied.
    /// </summary>
    internal virtual async Task<int> ReconcileRecentV2BalancesAsync(long toBlock, CancellationToken ct)
    {
        if (toBlock < 0)
            return 0;

        // Count the attempt up-front, before any DB read, so the runs counter reflects "did a pass
        // run" regardless of where it might fail. A flatlining counter then means reconciliation
        // stopped (vs the chain being quiet), and failures are tracked separately by the caller.
        CacheMetrics.ReconciliationRunsTotal.Inc();

        var window = Math.Max(1, _settings.ReconciliationWindowBlocks);
        var fromBlock = Math.Max(0, toBlock - window + 1);
        var corrections = 0;

        await WithReadonlyConnectionAsync(async (conn, token) =>
        {
            var accounts = await GetRecentlyActiveAccountsAsync(conn, fromBlock, toBlock, token);
            CacheMetrics.ReconciliationAccountsScanned.Set(accounts.Count);

            if (accounts.Count == 0)
                return;

            // Reconciliation runs inline on the block-ingestion path. A pathological window
            // (airdrop, bulk migration) could return a huge account set whose aggregation stalls
            // ingestion — refuse it rather than block. A narrower later window heals normally.
            if (accounts.Count > _settings.ReconciliationMaxAccounts)
            {
                CacheMetrics.ReconciliationSkippedOversizedTotal.Inc();
                _logger.LogWarning(
                    "Tail reconciliation skipped: {Count} active accounts in [{From},{To}] exceeds cap {Cap}",
                    accounts.Count, fromBlock, toBlock, _settings.ReconciliationMaxAccounts);
                return;
            }

            var authoritative = await FetchAuthoritativeV2BalancesAsync(conn, accounts, toBlock, token);
            corrections = ApplyReconciliation(accounts, authoritative, toBlock);
        }, ct);

        return corrections;
    }

    /// <summary>
    /// Diffs the cache against the authoritative balances for the active accounts and applies
    /// corrections. Pure cache logic (no DB) so it is unit-testable with stubbed reads.
    /// </summary>
    private int ApplyReconciliation(
        IReadOnlyCollection<string> accounts,
        IReadOnlyDictionary<string, (decimal Balance, long LastActivity)> authoritative,
        long toBlock)
    {
        // Group the authoritative "account:token" rows by account for O(1) per-account lookup.
        var authByAccount = new Dictionary<string, Dictionary<string, (decimal Balance, long LastActivity)>>();
        foreach (var (key, value) in authoritative)
        {
            var sep = key.IndexOf(':');
            if (sep < 0)
                continue;
            var acct = key[..sep];
            var token = key[(sep + 1)..];
            if (!authByAccount.TryGetValue(acct, out var tokens))
            {
                tokens = new Dictionary<string, (decimal, long)>();
                authByAccount[acct] = tokens;
            }
            tokens[token] = value;
        }

        // Reconcile every account we considered (recently-active) plus any the authoritative query
        // surfaced — covers both directions: cached-but-not-authoritative (heal to 0) and
        // authoritative-but-not-cached (add the missing credit).
        var accountSet = new HashSet<string>(accounts.Select(a => a.ToLowerInvariant()));
        foreach (var acct in authByAccount.Keys)
            accountSet.Add(acct);

        var corrections = 0;
        foreach (var account in accountSet)
        {
            authByAccount.TryGetValue(account, out var authTokens);

            var tokens = new HashSet<string>(_caches.GetTokenIdsForAddress(account, isV1: false));
            if (authTokens != null)
                foreach (var token in authTokens.Keys)
                    tokens.Add(token);

            foreach (var token in tokens)
            {
                var key = $"{account}:{token}";

                decimal desired = 0m;
                long lastActivity = 0;
                if (authTokens != null && authTokens.TryGetValue(token, out var auth))
                {
                    desired = auth.Balance;
                    lastActivity = auth.LastActivity;
                }

                _caches.V2BalancesByAccountAndToken.TryGetValue(key, out var current);
                if (Math.Abs(current - desired) <= ReconcileEpsilon)
                    continue;

                _caches.V2BalancesByAccountAndToken.Add(toBlock, key, desired);
                _caches.UpdateBalanceIndex(key, isV1: false, desired);
                if (lastActivity > 0)
                    _caches.V2LastActivity.Add(toBlock, key, lastActivity);

                var kind = desired <= 0m ? "phantom_cleared" : "value_corrected";
                CacheMetrics.ReconciliationCorrectionsTotal.WithLabels(kind).Inc();
                corrections++;

                _logger.LogWarning(
                    "CACHE BALANCE RECONCILE corrected account={Account} token={Token} cached={Cached} " +
                    "authoritative={Authoritative} kind={Kind} block={Block} recentReorgs=[{RecentReorgs}]",
                    account, token, current, desired, kind, toBlock, RecentReorgPointsCsv());
            }
        }

        return corrections;
    }

    /// <summary>
    /// Distinct, non-zero accounts that appear as sender or receiver in any V2 transfer table within
    /// [<paramref name="fromBlock"/>, <paramref name="toBlock"/>]. Addresses are stored lowercase in
    /// the DB, so the result is already normalized. Bounded by the blockNumber index.
    /// </summary>
    protected virtual async Task<List<string>> GetRecentlyActiveAccountsAsync(
        NpgsqlConnection conn, long fromBlock, long toBlock, CancellationToken ct)
    {
        const string sql = @"
            SELECT DISTINCT account FROM (
                SELECT ""from"" AS account FROM ""CrcV2_TransferSingle"" WHERE ""blockNumber"" BETWEEN @fromBlock AND @toBlock
                UNION
                SELECT ""to""        FROM ""CrcV2_TransferSingle"" WHERE ""blockNumber"" BETWEEN @fromBlock AND @toBlock
                UNION
                SELECT ""from""      FROM ""CrcV2_TransferBatch""  WHERE ""blockNumber"" BETWEEN @fromBlock AND @toBlock
                UNION
                SELECT ""to""        FROM ""CrcV2_TransferBatch""  WHERE ""blockNumber"" BETWEEN @fromBlock AND @toBlock
                UNION
                SELECT ""from""      FROM ""CrcV2_Erc20WrapperTransfer"" WHERE ""blockNumber"" BETWEEN @fromBlock AND @toBlock
                UNION
                SELECT ""to""        FROM ""CrcV2_Erc20WrapperTransfer"" WHERE ""blockNumber"" BETWEEN @fromBlock AND @toBlock
            ) s
            WHERE account <> '0x0000000000000000000000000000000000000000'";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("fromBlock", fromBlock);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        // Bounded: this runs inline on the block-ingestion path, so a slow query must fail fast
        // (caught best-effort) rather than stall ingestion. The blockNumber-indexed window scan is
        // sub-second in steady state.
        cmd.CommandTimeout = 20;

        var accounts = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            accounts.Add(reader.GetString(0).ToLowerInvariant());

        return accounts;
    }

    /// <summary>
    /// Authoritative raw (non-demurraged) V2 balances for the given accounts, pinned to
    /// <paramref name="toBlock"/>. Mirrors the warmup aggregation exactly: ERC1155 (TransferSingle +
    /// TransferBatch) filtered by a registered token owner, plus ERC20 wrapper transfers filtered by
    /// a registered holder, each kept only when the net is positive. Keyed by "account:token".
    /// </summary>
    protected virtual async Task<Dictionary<string, (decimal Balance, long LastActivity)>>
        FetchAuthoritativeV2BalancesAsync(
            NpgsqlConnection conn, IReadOnlyCollection<string> accounts, long toBlock, CancellationToken ct)
    {
        var result = new Dictionary<string, (decimal, long)>();
        if (accounts.Count == 0)
            return result;

        const string sql = @"
            WITH registered_avatars AS MATERIALIZED (
                SELECT organization AS avatar FROM ""CrcV2_RegisterOrganization"" WHERE ""blockNumber"" <= @toBlock
                UNION ALL
                SELECT ""group"" AS avatar FROM ""CrcV2_RegisterGroup"" WHERE ""blockNumber"" <= @toBlock
                UNION ALL
                SELECT avatar FROM ""CrcV2_RegisterHuman"" WHERE ""blockNumber"" <= @toBlock
            ),
            erc1155 AS (
                SELECT account, ""tokenAddress"", SUM(delta) AS balance, MAX(ts) AS last_activity
                FROM (
                    SELECT ""from"" AS account, ""tokenAddress"", -value AS delta, ""timestamp"" AS ts
                    FROM ""CrcV2_TransferSingle"" WHERE ""blockNumber"" <= @toBlock AND ""from"" = ANY(@accounts)
                    UNION ALL
                    SELECT ""to"", ""tokenAddress"", value, ""timestamp""
                    FROM ""CrcV2_TransferSingle"" WHERE ""blockNumber"" <= @toBlock AND ""to"" = ANY(@accounts)
                    UNION ALL
                    SELECT ""from"", ""tokenAddress"", -value, ""timestamp""
                    FROM ""CrcV2_TransferBatch"" WHERE ""blockNumber"" <= @toBlock AND ""from"" = ANY(@accounts)
                    UNION ALL
                    SELECT ""to"", ""tokenAddress"", value, ""timestamp""
                    FROM ""CrcV2_TransferBatch"" WHERE ""blockNumber"" <= @toBlock AND ""to"" = ANY(@accounts)
                ) t
                GROUP BY account, ""tokenAddress""
                HAVING SUM(delta) > 0
            ),
            wrapper AS (
                SELECT account, ""tokenAddress"", SUM(delta) AS balance, MAX(ts) AS last_activity
                FROM (
                    SELECT ""from"" AS account, ""tokenAddress"", -amount AS delta, ""timestamp"" AS ts
                    FROM ""CrcV2_Erc20WrapperTransfer"" WHERE ""blockNumber"" <= @toBlock AND ""from"" = ANY(@accounts)
                    UNION ALL
                    SELECT ""to"", ""tokenAddress"", amount, ""timestamp""
                    FROM ""CrcV2_Erc20WrapperTransfer"" WHERE ""blockNumber"" <= @toBlock AND ""to"" = ANY(@accounts)
                ) t
                GROUP BY account, ""tokenAddress""
                HAVING SUM(delta) > 0
            )
            SELECT e.account, e.""tokenAddress"", e.balance, e.last_activity
            FROM erc1155 e INNER JOIN registered_avatars ra ON ra.avatar = e.""tokenAddress""
            UNION ALL
            SELECT w.account, w.""tokenAddress"", w.balance, w.last_activity
            FROM wrapper w INNER JOIN registered_avatars ra ON ra.avatar = w.account";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("toBlock", toBlock);
        cmd.Parameters.AddWithValue("accounts", accounts.ToArray());
        // Bounded inline query (see GetRecentlyActiveAccountsAsync): the account set is capped and
        // each account is served by the (from|to, tokenAddress) composite index.
        cmd.CommandTimeout = 30;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var account = reader.GetString(0).ToLowerInvariant();
            var tokenAddress = reader.GetString(1).ToLowerInvariant();
            var balanceBig = reader.GetFieldValue<BigInteger>(2);
            var lastActivity = reader.GetInt64(3);

            decimal balance;
            try
            {
                balance = CirclesConverter.AttoCirclesToCircles(balanceBig);
            }
            catch (OverflowException ex)
            {
                // A partial authoritative map is unsafe to drive corrections: downstream, a missing
                // row reads as "desired = 0" and would zero a correct cached balance. Abort the whole
                // pass instead of silently dropping one row. (Caught best-effort by MaybeReconcileAsync
                // and counted as a failure.) Unreachable for real Circles balances (> ~7.9e28 CRC).
                _logger.LogError(ex,
                    "Reconcile aborted: balance {Value} for account={Account} token={Token} overflows decimal",
                    balanceBig, account, tokenAddress);
                throw;
            }

            if (balance <= 0)
                continue;

            // ERC1155 and wrapper keys are disjoint namespaces (token-owner vs wrapper contract),
            // so a straight assignment is correct; tolerate a duplicate by summing defensively.
            var key = $"{account}:{tokenAddress}";
            if (result.TryGetValue(key, out var existing))
                result[key] = (existing.Item1 + balance, Math.Max(existing.Item2, lastActivity));
            else
                result[key] = (balance, lastActivity);
        }

        return result;
    }
}
