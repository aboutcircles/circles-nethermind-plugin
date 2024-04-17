using System.Collections.Immutable;
using System.Text;
using Circles.Index.Data.Query;
using Npgsql;

namespace Circles.Index.Data;

public static class Schema
{
    public static void Migrate(NpgsqlConnection connection)
    {
        var sqlCommands = GenerateSqlCommands();

        using var command = connection.CreateCommand();
        command.CommandText = string.Join("\n\n", sqlCommands);
        command.ExecuteNonQuery();
    }

    private static List<string> GenerateSqlCommands()
    {
        List<string> commands = new List<string>();

        foreach (var table in TableSchemas)
        {
            StringBuilder tableCommand = new StringBuilder();
            string tableName = table.Key.GetIdentifier();
            tableCommand.AppendLine($"CREATE TABLE IF NOT EXISTS {tableName} (");

            List<string> columnDefinitions = new List<string>();
            List<string> primaryKeyColumns = new List<string>();

            foreach (var column in table.Value.Columns)
            {
                string columnType = GetSqlType(column.Type);
                string columnName = column.Column.GetIdentifier();
                string columnDefinition = $"{columnName} {columnType}";

                if (column.IsPrimaryKey)
                {
                    primaryKeyColumns.Add(columnName);
                }

                columnDefinitions.Add(columnDefinition);
            }

            if (primaryKeyColumns.Any())
            {
                columnDefinitions.Add($"PRIMARY KEY ({string.Join(", ", primaryKeyColumns)})");
            }

            tableCommand.AppendLine(string.Join(",\n", columnDefinitions));
            tableCommand.AppendLine(");");
            commands.Add(tableCommand.ToString());

            // Generate index creation statements
            foreach (var column in table.Value.Columns.Where(c => c.IsIndexed && !c.IsPrimaryKey))
            {
                string columnName = column.Column.GetIdentifier();
                string indexName = $"idx_{tableName.ToLower()}_{columnName.ToLower()}";
                commands.Add($"CREATE INDEX IF NOT EXISTS {indexName} ON {tableName} ({columnName});");
            }
        }

        return commands;
    }

    private static string GetSqlType(ValueTypes type)
    {
        return type switch
        {
            ValueTypes.BigInt => "NUMERIC",
            ValueTypes.Int => "BIGINT",
            ValueTypes.String => "TEXT",
            ValueTypes.Address => "TEXT",
            ValueTypes.Boolean => "BOOLEAN",
            _ => throw new ArgumentException("Unsupported type")
        };
    }


    public static readonly IDictionary<Tables, TableSchema> TableSchemas = new Dictionary<Tables, TableSchema>
    {
        {
            Tables.Block,
            new TableSchema(Tables.Block, [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.BlockHash, ValueTypes.String, false, true)
            ])
        },

        {
            Tables.CrcV1HubTransfer,
            new TableSchema(Tables.CrcV1HubTransfer,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.FromAddress, ValueTypes.Address, false, true),
                (Columns.ToAddress, ValueTypes.Address, false, true),
                (Columns.Amount, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Tables.CrcV1Signup,
            new TableSchema(Tables.CrcV1Signup,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.CirclesAddress, ValueTypes.Address, false, true),
                (Columns.TokenAddress, ValueTypes.Address, false, true)
            ])
        },
        {
            Tables.CrcV1Trust,
            new TableSchema(Tables.CrcV1Trust,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.UserAddress, ValueTypes.Address, false, true),
                (Columns.CanSendToAddress, ValueTypes.Address, false, true),
                (Columns.Limit, ValueTypes.Int, false, false)
            ])
        },
        {
            Tables.CrcV2ConvertInflation,
            new TableSchema(Tables.CrcV2ConvertInflation,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.InflationValue, ValueTypes.BigInt, false, false),
                (Columns.DemurrageValue, ValueTypes.BigInt, false, false),
                (Columns.Day, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Tables.CrcV2InviteHuman,
            new TableSchema(Tables.CrcV2InviteHuman,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.InviterAddress, ValueTypes.Address, false, true),
                (Columns.InviteeAddress, ValueTypes.Address, false, true)
            ])
        },
        {
            Tables.CrcV2PersonalMint,
            new TableSchema(Tables.CrcV2PersonalMint,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.ToAddress, ValueTypes.Address, false, true),
                (Columns.Amount, ValueTypes.BigInt, false, false),
                (Columns.StartPeriod, ValueTypes.BigInt, false, false),
                (Columns.EndPeriod, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Tables.CrcV2RegisterGroup,
            new TableSchema(Tables.CrcV2RegisterGroup,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.GroupAddress, ValueTypes.Address, false, true),
                (Columns.GroupMintPolicy, ValueTypes.Address, false, true),
                (Columns.GroupTreasury, ValueTypes.Address, false, true),
                (Columns.GroupName, ValueTypes.String, false, true),
                (Columns.GroupSymbol, ValueTypes.String, false, true)
            ])
        },
        {
            Tables.CrcV2RegisterHuman,
            new TableSchema(Tables.CrcV2RegisterHuman,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.Address, ValueTypes.Address, false, true)
            ])
        },
        {
            Tables.CrcV2RegisterOrganization,
            new TableSchema(Tables.CrcV2RegisterOrganization,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.OrganizationAddress, ValueTypes.Address, false, true),
                (Columns.OrganizationName, ValueTypes.String, false, true)
            ])
        },
        {
            Tables.CrcV2Stopped,
            new TableSchema(Tables.CrcV2Stopped,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.Address, ValueTypes.Address, false, true)
            ])
        },
        {
            Tables.CrcV2Trust,
            new TableSchema(Tables.CrcV2Trust,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.TrusterAddress, ValueTypes.Address, false, true),
                (Columns.TrusteeAddress, ValueTypes.Address, false, true),
                (Columns.ExpiryTime, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Tables.Erc20Transfer,
            new TableSchema(Tables.Erc20Transfer,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.TokenAddress, ValueTypes.Address, false, true),
                (Columns.FromAddress, ValueTypes.Address, false, true),
                (Columns.ToAddress, ValueTypes.Address, false, true),
                (Columns.Amount, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Tables.Erc1155ApprovalForAll,
            new TableSchema(Tables.Erc1155ApprovalForAll,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.OwnerAddress, ValueTypes.Address, false, true),
                (Columns.OperatorAddress, ValueTypes.Address, false, true),
                (Columns.Approved, ValueTypes.Boolean, false, true)
            ])
        },
        {
            Tables.Erc1155TransferBatch,
            new TableSchema(Tables.Erc1155TransferBatch,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.BatchIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.OperatorAddress, ValueTypes.Address, false, true),
                (Columns.FromAddress, ValueTypes.Address, false, true),
                (Columns.ToAddress, ValueTypes.Address, false, true),
                (Columns.TokenId, ValueTypes.BigInt, false, true),
                (Columns.Amount, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Tables.Erc1155TransferSingle,
            new TableSchema(Tables.Erc1155TransferSingle,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.OperatorAddress, ValueTypes.Address, false, true),
                (Columns.FromAddress, ValueTypes.Address, false, true),
                (Columns.ToAddress, ValueTypes.Address, false, true),
                (Columns.TokenId, ValueTypes.BigInt, false, true),
                (Columns.Amount, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Tables.Erc1155Uri,
            new TableSchema(Tables.Erc1155Uri,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.TokenId, ValueTypes.BigInt, false, true),
                (Columns.Uri, ValueTypes.BigInt, false, false)
            ])
        }
    }.ToImmutableDictionary();
}