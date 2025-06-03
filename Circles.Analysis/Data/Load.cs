using Npgsql;

namespace Circles.Analysis.Data;

public class DataLoader(string connectionString)
{
    public record Transfer(
        long BlockNumber,
        long TransactionIndex,
        long LogIndex,
        long BatchIndex,
        long Timestamp,
        Account? Operator,
        Account From,
        Account? FromDefi,
        Account To,
        Account TokenOwner,
        Account? ToDefi,
        string Value,
        string Type,
        string TokenType);

    public (long FromBlock, long ToBlock) GetBlockRange()
    {
        var sql = @"
                select min(""blockNumber""), max(""blockNumber"")
                from ""V_CrcV2_Transfers"";
            ";

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = new NpgsqlCommand(sql, connection);
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            var fromBlock = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
            var toBlock = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            return (fromBlock, toBlock);
        }

        throw new InvalidOperationException("No block range found in the database.");
    }

    public IEnumerable<Transfer> LoadTransfers()
    {
        // We now only have one trust query that includes wrap tokens
        var sql = @"
            select t.""blockNumber"",
                t.""transactionIndex"",
                t.""logIndex"",
                t.""batchIndex"",
                t.""timestamp"",
                t.""operator"",
                fo.payload->'name' as ""operatorName"",
                t.""from"",
                -- fa.metadata_digest as ""fromProfile"",
                fa.payload->'name' as ""fromName"",
                lbpa.c2 as ""fromDefi"",
                t.""to"",
                -- fb.metadata_digest as ""toProfile"",
                fb.payload->'name' as ""toName"",
                lbpb.c2 as ""toDefi"",
                t.""tokenAddress"",
                fc.payload->'name' as ""tokenOwnerName"",
                t.""value""::text,
                t.""type"",
                t.""tokenType""
            from ""V_CrcV2_Transfers"" t
                left join ""V_CrcV2_Avatars"" ao on t.""operator"" = ao.""avatar""
                left join ""V_CrcV2_Avatars"" aa on t.""from"" = aa.""avatar""
                left join ""V_CrcV2_Avatars"" ab on t.""to"" = ab.""avatar""
                left join ""V_CrcV2_Avatars"" ac on t.""tokenAddress"" = ac.""avatar""
                left join ""ipfs_files"" fo on fo.metadata_digest = ao.""cidV0Digest""
                left join ""ipfs_files"" fa on fa.metadata_digest = aa.""cidV0Digest""
                left join ""ipfs_files"" fb on fb.metadata_digest = ab.""cidV0Digest""
                left join ""ipfs_files"" fc on fc.metadata_digest = ac.""cidV0Digest""
                left join ""defi_labels"" lbpa on lbpa.c1 = t.""from""
                left join ""defi_labels"" lbpb on lbpb.c1 = t.""to""
            order by t.""blockNumber"",
                t.""transactionIndex"",
                t.""logIndex"",
                t.""batchIndex""
            ";

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = new NpgsqlCommand(sql, connection);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var blockNumber = reader.GetInt64(0);
            var transactionIndex = reader.GetInt64(1);
            var logIndex = reader.GetInt64(2);
            var batchIndex = reader.GetInt64(3);
            var timestamp = reader.GetInt64(4);
            var @operator = reader.IsDBNull(5) ? null : reader.GetString(5);
            var operatorName = reader.IsDBNull(6) ? null : reader.GetString(6);
            var from = reader.GetString(7);
            var fromName = reader.IsDBNull(8) ? null : reader.GetString(8);
            var fromDefi = reader.IsDBNull(9) ? null : reader.GetString(9);
            var to = reader.GetString(10);
            var toName = reader.IsDBNull(11) ? null : reader.GetString(11);
            var toDefi = reader.IsDBNull(12) ? null : reader.GetString(12);
            var tokenAddress = reader.GetString(13);
            var tokenOwnerName = reader.IsDBNull(14) ? null : reader.GetString(14);
            var value = reader.GetString(15);
            var type = reader.GetString(16);
            var tokenType = reader.GetString(17);

            yield return new Transfer(
                blockNumber,
                transactionIndex,
                logIndex,
                batchIndex,
                timestamp,
                @operator == null ? null : new Account(@operator, operatorName),
                new Account(from, fromName),
                fromDefi == null ? null : new Account(from, fromDefi),
                new Account(to, toName),
                new Account(tokenAddress, tokenOwnerName),
                toDefi == null ? null : new Account(to, toDefi),
                value,
                type,
                tokenType);
        }
    }
}