using System.Collections.Immutable;
using System.Numerics;
using Circles.Index.Common;
using Circles.Pathfinder;
using Nethermind.JsonRpc;

namespace Circles.Index.Rpc;

public class NetworkSnapshotRpc(Context indexerContext)
{
    private readonly Context _indexerContext =
        indexerContext ?? throw new ArgumentNullException(nameof(indexerContext));


    /// <summary>
    /// Returns the highest safe block across *all* rollback-caches.
    /// </summary>
    private static long GetGlobalMaxSafeBlock()
    {
        long safe = long.MaxValue;

        void Consider(long last, int cap)
        {
            if (last == long.MaxValue)
            {
                return; // no data
            }

            long safeBlock = last - cap;
            if (safeBlock > safe)
            {
                safe = safeBlock;
            }
        }

        Consider(CirclesV2.LogParser.BalancesByAccountAndToken.LastBlockNo,
            CirclesV2.LogParser.BalancesByAccountAndToken.RollbackCapacity);

        Consider(CirclesV2.LogParser.Erc20WrapperAddresses.LastBlockNo,
            CirclesV2.LogParser.Erc20WrapperAddresses.RollbackCapacity);

        Consider(CirclesV2.LogParser.LastTokenMovement.LastBlockNo,
            CirclesV2.LogParser.LastTokenMovement.RollbackCapacity);

        Consider(CirclesV2.LogParser.Groups.LastBlockNo,
            CirclesV2.LogParser.Groups.RollbackCapacity);

        Consider(CirclesV2.LogParser.V2Avatars.LastBlockNo,
            CirclesV2.LogParser.V2Avatars.RollbackCapacity);

        return safe == long.MaxValue ? long.MinValue : safe;
    }

    /// <summary>Fetches the last-safe snapshot of the Balances cache.</summary>
    private static IReadOnlyDictionary<string,
        ImmutableDictionary<string, (
            BigInteger Balance,
            TokenValueRepresentation ValueRepresentation,
            string TokenOwner
            )>> GetSafeBalances()
    {
        var snapshot = CirclesV2.LogParser.BalancesByAccountAndToken.GetLastSafeSnapshot();
        return snapshot ?? throw new InvalidOperationException("Not enough history for a safe balances snapshot.");
    }

    /// <summary>
    /// Builds the trust lookup (avatar-id ➜ set of avatar-ids) from the DB,
    /// but only up to <paramref name="safeBlock"/> (inclusive).
    /// </summary>
    private Dictionary<int, HashSet<int>> BuildTrustLookup()
    {
        const string sql = @"
            SELECT truster,
                   trustee,
                   ""expiryTime""
            FROM   ""V_CrcV2_TrustRelations"";";

        var rows = _indexerContext.ReadonlyDatabase.Select(new ParameterizedSql(sql, [])).Rows;

        var trust = new Dictionary<int, HashSet<int>>();

        foreach (var row in rows)
        {
            int fromId = AddressIdPool.IdOf(row[0].ToString()!);
            int toId = AddressIdPool.IdOf(row[1].ToString()!);

            if (!trust.TryGetValue(fromId, out var set))
            {
                set = new HashSet<int>();
                trust.Add(fromId, set);
            }

            set.Add(toId);
        }

        return trust;
    }

    /// <summary>
    /// Re-implements the `/snapshot` endpoint as an RPC call backed by the
    /// live rollback-caches.  Only data that sits *outside* every cache’s
    /// rollback window is published.
    /// </summary>
    public async Task<ResultWrapper<NetworkSnapshot>> circles_getNetworkSnapshot()
    {
        long safeBlock = GetGlobalMaxSafeBlock();
        if (safeBlock == long.MinValue)
        {
            return ResultWrapper<NetworkSnapshot>.Fail("Caches have not accumulated a safe window yet.");
        }

        var safeBalances = GetSafeBalances();

        // -----------------------------------------------------------------
        // build BalanceNodes grouped by holder
        // -----------------------------------------------------------------
        var balancesByHolder = new Dictionary<int, List<BalanceNode>>();

        ulong today = CirclesConverter.DayFromTimestamp(DateTimeOffset.UtcNow, 1_602_720_000);

        foreach (var (accountStr, tokenMap) in safeBalances)
        {
            int holderId = AddressIdPool.IdOf(accountStr);
            foreach (var (tokenStr, data) in tokenMap)
            {
                var valueRep = data.ValueRepresentation;

                bool isInflationary = valueRep.HasFlag(TokenValueRepresentation.Inflationary);
                bool isWrapped = valueRep.HasFlag(TokenValueRepresentation.IsWrapped);

                BigInteger attoCircles;
                if (isInflationary)
                {
                    attoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(data.Balance);
                }
                else
                {
                    CirclesV2.LogParser.LastTokenMovement.TryGetValue(
                        (accountStr, tokenStr), out var lastMove);
                    (attoCircles, _) = Demurrage.ApplyDemurrage(
                        data.Balance,
                        (ulong)lastMove,
                        today);
                }

                if (attoCircles == BigInteger.Zero)
                {
                    continue;
                }

                int tokenId = AddressIdPool.IdOf(tokenStr);
                int balanceNodeId = AddressIdPool.BalanceNodeIdOf($"{holderId}-{tokenId}");

                // int balanceNodeId, int holder, int token, long amount, bool isWrapped, bool isStatic
                var node = new BalanceNode(
                    balanceNodeId,
                    holderId,
                    tokenId,
                    CirclesConverter.TruncateToInt64(attoCircles),
                    isWrapped,
                    isInflationary
                );

                if (!balancesByHolder.TryGetValue(holderId, out var list))
                {
                    list = new List<BalanceNode>();
                    balancesByHolder.Add(holderId, list);
                }

                list.Add(node);
            }
        }

        // -----------------------------------------------------------------
        // trust graph
        // -----------------------------------------------------------------
        var trustLookup = BuildTrustLookup();

        // -----------------------------------------------------------------
        // assemble snapshot
        // -----------------------------------------------------------------
        var snapshot = new NetworkSnapshot
        {
            BlockNumber = safeBlock,
            Addresses = AddressIdPool.GetAvatarSnapshot(),
            Trust = trustLookup.ToDictionary(k => k.Key, v => new HashSet<int>(v.Value)),
            Balance = balancesByHolder
        };

        return ResultWrapper<NetworkSnapshot>.Success(snapshot);
    }
}