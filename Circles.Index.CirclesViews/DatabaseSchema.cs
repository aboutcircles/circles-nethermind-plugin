using Circles.Index.Common;
using System.Reflection;

namespace Circles.Index.CirclesViews;

public class DatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();

    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    private string LoadSqlFromResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fullResourceName = $"Circles.Index.CirclesViews.Sql.{resourceName}";

        using var stream = assembly.GetManifestResourceStream(fullResourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"SQL query resource not found: {fullResourceName}");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Define a schema for the database functions
    public static readonly EventSchema DatabaseFunctions = new("System", "Functions", new byte[32], [])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("functions.sql"))
    };

    public static readonly EventSchema V_CrcV2_GroupVaultBalancesByToken = new("V_CrcV2", "GroupVaultBalancesByToken",
        new byte[32], [
            new("vault", ValueTypes.Address, true),
            new("id", ValueTypes.BigInt, true),
            new("balance", ValueTypes.BigInt, true),
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_CrcV2_GroupVaultBalancesByToken.sql"))
    };


    public static readonly EventSchema V_CrcV2_TotalSupply = new("V_CrcV2", "TotalSupply", new byte[32], [
        new("tokenAddress", ValueTypes.Address, true),
        new("tokenId", ValueTypes.BigInt, true),
        new("totalSupply", ValueTypes.BigInt, false),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_CrcV2_TotalSupply.sql"))
    };

    public static readonly EventSchema V_CrcV1_TotalSupply = new("V_CrcV1", "TotalSupply", new byte[32], [
        new("tokenAddress", ValueTypes.Address, true),
        new("user", ValueTypes.Address, true),
        new("totalSupply", ValueTypes.BigInt, false),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_CrcV1_TotalSupply.sql"))
    };

    public static readonly EventSchema V_CrcV1_TrustRelations = new("V_CrcV1", "TrustRelations", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("batchIndex", ValueTypes.Int, true, true),
        new("transactionHash", ValueTypes.String, true),
        new("user", ValueTypes.Address, true),
        new("canSendTo", ValueTypes.Address, true),
        new("limit", ValueTypes.Int, false),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_CrcV1_TrustRelations.sql"))
    };

    public static readonly EventSchema V_CrcV1_Avatars = new("V_CrcV1", "Avatars", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("user", ValueTypes.Address, true),
        new("token", ValueTypes.Address, true),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_CrcV1_Avatars.sql"))
    };

    /// <summary>
    /// All Circles v1 hub transfers + personal minting
    /// </summary>
    public static readonly EventSchema V_CrcV1_Transfers = new("V_CrcV1", "Transfers",
        new byte[32],
        [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true),
            new("logIndex", ValueTypes.Int, true),
            new("transactionHash", ValueTypes.String, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("amount", ValueTypes.BigInt, false),
            new("type", ValueTypes.String, true),
            new("tokenType", ValueTypes.String, true)
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_CrcV1_Transfers.sql"))
    };

    public static readonly EventSchema V_CrcV2_Avatars = new("V_CrcV2", "Avatars", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("type", ValueTypes.String, false),
        new("invitedBy", ValueTypes.String, false),
        new("avatar", ValueTypes.String, false),
        new("tokenId", ValueTypes.String, false),
        new("name", ValueTypes.String, false),
        new("cidV0Digest", ValueTypes.Bytes, false),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_CrcV2_Avatars.sql"))
    };

    public static readonly EventSchema V_CrcV2_Transfers = new("V_CrcV2", "Transfers",
        new byte[32],
        [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true),
            new("logIndex", ValueTypes.Int, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("operator", ValueTypes.Address, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("id", ValueTypes.BigInt, true),
            new("value", ValueTypes.BigInt, false),
            new("type", ValueTypes.String, true),
            new("tokenType", ValueTypes.String, true)
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_CrcV2_Transfers.sql"))
    };

    public static readonly EventSchema V_CrcV2_GroupMemberships = new("V_CrcV2", "GroupMemberships", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("group", ValueTypes.Address, true),
        new("member", ValueTypes.Address, true),
        new("expiryTime", ValueTypes.BigInt, true),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_CrcV2_GroupMemberships.sql"))
    };

    public static readonly EventSchema V_CrcV2_TrustRelations = new("V_CrcV2", "TrustRelations", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("batchIndex", ValueTypes.Int, true, true),
        new("transactionHash", ValueTypes.String, true),
        new("trustee", ValueTypes.Address, true),
        new("truster", ValueTypes.Address, true),
        new("expiryTime", ValueTypes.BigInt, true),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_CrcV2_TrustRelations.sql"))
    };

    public static readonly EventSchema V_Crc_TrustRelations = new("V_Crc", "TrustRelations", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("version", ValueTypes.Int, false),
        new("trustee", ValueTypes.String, false),
        new("truster", ValueTypes.String, false),
        new("expiryTime", ValueTypes.Int, false),
        new("limit", ValueTypes.Int, false)
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_Crc_TrustRelations.sql"))
    };

    public static readonly EventSchema V_Crc_Avatars = new("V_Crc", "Avatars", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("version", ValueTypes.Int, false),
        new("type", ValueTypes.String, false),
        new("invitedBy", ValueTypes.String, false),
        new("avatar", ValueTypes.String, false),
        new("tokenId", ValueTypes.String, false),
        new("name", ValueTypes.String, false),
        new("cidV0Digest", ValueTypes.Bytes, false),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_Crc_Avatars.sql"))
    };

    public static readonly EventSchema V_Crc_Transfers = new("V_Crc", "Transfers",
        new byte[32],
        [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true),
            new("logIndex", ValueTypes.Int, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("version", ValueTypes.Int, false),
            new("operator", ValueTypes.Address, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("id", ValueTypes.BigInt, true),
            new("value", ValueTypes.BigInt, false),
            new("type", ValueTypes.String, true),
            new("tokenType", ValueTypes.String, true)
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_Crc_Transfers.sql"))
    };

    public static readonly EventSchema V_Crc_TransferSummary = new("V_Crc", "TransferSummary",
        new byte[32],
        [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true),
            new("logIndex", ValueTypes.Int, true),
            new("transactionHash", ValueTypes.String, true),
            new("version", ValueTypes.Int, false),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("value", ValueTypes.BigInt, false),
            new("events", ValueTypes.Json, false)
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_Crc_TransferSummary.sql"))
    };

    public static readonly EventSchema V_CrcV2_Groups = new("V_CrcV2", "Groups", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("group", ValueTypes.Address, true),
        new("type", ValueTypes.String, true),
        new("owner", ValueTypes.Address, true),
        new("mintPolicy", ValueTypes.Address, true),
        new("mintHandler", ValueTypes.Address, true),
        new("treasury", ValueTypes.Address, true),
        new("service", ValueTypes.Address, true),
        new("feeCollection", ValueTypes.Address, true),
        new("memberCount", ValueTypes.Int, true),
        new("name", ValueTypes.String, true),
        new("symbol", ValueTypes.String, true),
        new("cidV0Digest", ValueTypes.Bytes, true)
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_CrcV2_Groups.sql"))
    };

    public static readonly EventSchema V_CrcV1_BalancesByAccountAndToken = new("V_CrcV1", "BalancesByAccountAndToken",
        new byte[32],
        [
            new("account", ValueTypes.Address, true),
            new("tokenAddress", ValueTypes.String, true),
            new("lastActivity", ValueTypes.Int, true),
            new("totalBalance", ValueTypes.BigInt, true),
            new("tokenOwner", ValueTypes.String, true)
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_CrcV1_BalancesByAccountAndToken.sql"))
    };


    public static readonly EventSchema V_CrcV2_BalancesByAccountAndToken = new("V_CrcV2", "BalancesByAccountAndToken",
        new byte[32],
        [
            new("account", ValueTypes.Address, true),
            new("tokenId", ValueTypes.String, true),
            new("tokenAddress", ValueTypes.String, true),
            new("lastActivity", ValueTypes.Int, true),
            new("demurragedTotalBalance", ValueTypes.BigInt, true),
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_CrcV2_BalancesByAccountAndToken.sql"))
    };

    public static readonly EventSchema V_Crc_Tokens = new("V_Crc", "Tokens", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("version", ValueTypes.Int, false),
        new("type", ValueTypes.String, false),
        new("token", ValueTypes.String, true),
        new("tokenOwner", ValueTypes.String, true)
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_Crc_Tokens.sql"))
    };

    public static readonly EventSchema V_Crc_Stats = new("V_Crc", "Stats", new byte[32], [
        new("measure", ValueTypes.String, false),
        new("value", ValueTypes.Int, false)
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(LazySqlLoader.LoadSql("V_Crc_Stats.sql"))
    };

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } =
        new Dictionary<(string Namespace, string Table), EventSchema>
        {
             {
                ("System", "Functions"),
                DatabaseFunctions
            },
            {
                ("V_CrcV1", "TrustRelations"),
                V_CrcV1_TrustRelations
            },
            {
                ("V_CrcV1", "Avatars"),
                V_CrcV1_Avatars
            },
            {
                ("V_CrcV2", "Avatars"),
                V_CrcV2_Avatars
            },
            {
                ("V_CrcV2", "TrustRelations"),
                V_CrcV2_TrustRelations
            },
            {
                ("V_CrcV2", "GroupMemberships"),
                V_CrcV2_GroupMemberships
            },
            {
                ("V_Crc", "Avatars"),
                V_Crc_Avatars
            },
            {
                ("V_Crc", "Tokens"),
                V_Crc_Tokens
            },
            {
                ("V_CrcV1", "Transfers"),
                V_CrcV1_Transfers
            },
            {
                ("V_CrcV2", "Transfers"),
                V_CrcV2_Transfers
            },
            {
                ("V_Crc", "TrustRelations"),
                V_Crc_TrustRelations
            },
            {
                ("V_Crc", "Transfers"),
                V_Crc_Transfers
            },
            {
                ("V_CrcV2", "Groups"),
                V_CrcV2_Groups
            },
            {
                ("V_CrcV1", "BalancesByAccountAndToken"),
                V_CrcV1_BalancesByAccountAndToken
            },
            {
                ("V_CrcV2", "BalancesByAccountAndToken"),
                V_CrcV2_BalancesByAccountAndToken
            },
            {
                ("V_Crc", "Stats"),
                V_Crc_Stats
            },
            {
                ("V_CrcV1", "TotalSupply"),
                V_CrcV1_TotalSupply
            },
            {
                ("V_CrcV2", "TotalSupply"),
                V_CrcV2_TotalSupply
            },
            {
                ("V_Crc", "TransferSummary"),
                V_Crc_TransferSummary
            },
            {
                ("V_CrcV2", "GroupVaultBalancesByToken"),
                V_CrcV2_GroupVaultBalancesByToken
            }
        };
}