using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Pure SQL WHERE-clause + parameter builder for circles_getTransferData. Kept standalone (not on the
/// CirclesRpcModule partial class, whose type depends on the reference-only Nethermind runtime) so the
/// direction / counterparty / block-range / cursor branching can be unit-tested without a database.
/// </summary>
public static class TransferDataQuery
{
    /// <summary>
    /// Builds the WHERE clause and bound parameters for the CrcV2_TransferData query.
    ///
    /// Inputs are assumed already validated/normalized by the caller: <paramref name="addr"/> is a
    /// lowercased address, <paramref name="direction"/> is "sent", "received", or null, and the cursor
    /// components come from <see cref="CursorUtils.DecodeCursor(string?)"/>. The limit parameter is
    /// added by the caller — it is not part of the WHERE clause.
    ///
    /// Conditions containing " OR " are wrapped in parentheses before being AND-joined so the OR binds
    /// correctly against the other filters (the keyset/block filters must not leak into an OR branch).
    /// </summary>
    public static (string Where, List<NpgsqlParameter> Parameters) BuildWhereClause(
        string addr,
        string? direction,
        string? counterparty,
        long? fromBlock,
        long? toBlock,
        long? cursorBlock,
        int? cursorTxIndex,
        int? cursorLogIndex)
    {
        var conditions = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        var counterAddr = counterparty?.ToLower();

        if (direction == "sent")
        {
            conditions.Add(@"""from"" = @addr");
            parameters.Add(new NpgsqlParameter("addr", addr));
            if (counterAddr != null)
            {
                conditions.Add(@"""to"" = @counterparty");
                parameters.Add(new NpgsqlParameter("counterparty", counterAddr));
            }
        }
        else if (direction == "received")
        {
            conditions.Add(@"""to"" = @addr");
            parameters.Add(new NpgsqlParameter("addr", addr));
            if (counterAddr != null)
            {
                conditions.Add(@"""from"" = @counterparty");
                parameters.Add(new NpgsqlParameter("counterparty", counterAddr));
            }
        }
        else // both directions
        {
            if (counterAddr != null)
            {
                // (from=addr AND to=counter) OR (from=counter AND to=addr)
                conditions.Add(@"(""from"" = @addr AND ""to"" = @counterparty) OR (""from"" = @counterparty AND ""to"" = @addr)");
                parameters.Add(new NpgsqlParameter("addr", addr));
                parameters.Add(new NpgsqlParameter("counterparty", counterAddr));
            }
            else
            {
                conditions.Add(@"(""from"" = @addr OR ""to"" = @addr)");
                parameters.Add(new NpgsqlParameter("addr", addr));
            }
        }

        if (fromBlock.HasValue)
        {
            conditions.Add(@"""blockNumber"" >= @fromBlock");
            parameters.Add(new NpgsqlParameter("fromBlock", fromBlock.Value));
        }

        if (toBlock.HasValue)
        {
            conditions.Add(@"""blockNumber"" <= @toBlock");
            parameters.Add(new NpgsqlParameter("toBlock", toBlock.Value));
        }

        if (cursorBlock.HasValue)
        {
            conditions.Add(
                @"(""blockNumber"", ""transactionIndex"", ""logIndex"") < (@cursorBlock, @cursorTxIndex, @cursorLogIndex)");
            parameters.Add(new NpgsqlParameter("cursorBlock", cursorBlock.Value));
            parameters.Add(new NpgsqlParameter("cursorTxIndex", cursorTxIndex!.Value));
            parameters.Add(new NpgsqlParameter("cursorLogIndex", cursorLogIndex!.Value));
        }

        var where = string.Join(" AND ", conditions.Select(c =>
            // Wrap the OR clause in parens so AND binds correctly
            c.Contains(" OR ") ? $"({c})" : c));

        return (where, parameters);
    }
}
