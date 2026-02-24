using System.Numerics;
using Circles.Common;

namespace Circles.Index.Safe;

public record ProxyCreation(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Proxy,
    string? Singleton
) : IIndexEvent;

public record SafeSetup(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string SafeAddress,
    string Initiator,
    string Owner,
    BigInteger Threshold,
    string Initializer,
    string FallbackHandler
) : IIndexEvent;

public record AddedOwner(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string SafeAddress,
    string Owner
) : IIndexEvent;

public record RemovedOwner(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string SafeAddress,
    string Owner
) : IIndexEvent;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema ProxyCreation = EventSchema.FromSolidity(
        "Safe",
        "event ProxyCreation(address indexed proxy, address singleton)"
    );

    public static readonly EventSchema SafeSetup = new(
        "Safe",
        "SafeSetup",
        KeccakHelper.ComputeHash("SafeSetup(address,address[],uint256,address,address)"),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("safeAddress", ValueTypes.Address, true),
            new("initiator", ValueTypes.Address, true),
            new("owner", ValueTypes.Address, true),
            new("threshold", ValueTypes.BigInt, false),
            new("initializer", ValueTypes.Address, true),
            new("fallbackHandler", ValueTypes.Address, true)
        ]
    );

    public static readonly EventSchema AddedOwner = new(
        "Safe",
        "AddedOwner",
        KeccakHelper.ComputeHash("AddedOwner(address)"),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("safeAddress", ValueTypes.Address, true),
            new("owner", ValueTypes.Address, true),
        ]
    );

    public static readonly EventSchema RemovedOwner = new(
        "Safe",
        "RemovedOwner",
        KeccakHelper.ComputeHash("RemovedOwner(address)"),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("safeAddress", ValueTypes.Address, true),
            new("owner", ValueTypes.Address, true),
        ]
    );

    public static readonly EventSchema V_Safe_Owners = new("V_Safe", "Owners", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("safeAddress", ValueTypes.Address, true),
        new("owner", ValueTypes.Address, true)
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
            create or replace view ""V_Safe_Owners"" AS
                WITH events AS (
                    SELECT
                        ""safeAddress"",
                        ""owner"",
                        ""blockNumber"",
                        ""timestamp"",
                        ""transactionIndex"",
                        ""logIndex"",
                        1 AS init_count,
                        0 AS add_count,
                        0 AS remove_count
                    FROM ""Safe_SafeSetup""
                
                    UNION ALL
                
                    SELECT
                        ""safeAddress"",
                        ""owner"",
                        ""blockNumber"",
                        ""timestamp"",
                        ""transactionIndex"",
                        ""logIndex"",
                        0 AS init_count,
                        1 AS add_count,
                        0 AS remove_count
                    FROM ""Safe_AddedOwner""
                
                    UNION ALL
                
                    SELECT
                        ""safeAddress"",
                        ""owner"",
                        ""blockNumber"",
                        ""timestamp"",
                        ""transactionIndex"",
                        ""logIndex"",
                        0 AS init_count,
                        0 AS add_count,
                        1 AS remove_count
                    FROM ""Safe_RemovedOwner""
                ),
                final_owners AS (
                    SELECT
                        ""safeAddress"",
                        ""owner"",
                        SUM(init_count) + SUM(add_count) - SUM(remove_count) AS final_count
                    FROM events
                    GROUP BY 1, 2
                    HAVING SUM(init_count) + SUM(add_count) - SUM(remove_count) > 0
                ),
                
                -- For each (safeAddress, owner) pair, select the event with the highest block/tx/log
                -- i.e., the most recent event that changed that pair.
                last_change AS (
                    SELECT DISTINCT ON (""safeAddress"", ""owner"")
                        ""safeAddress"",
                        ""owner"",
                        ""blockNumber"",
                        ""timestamp"",
                        ""transactionIndex"",
                        ""logIndex""
                    FROM events
                    ORDER BY
                        ""safeAddress"",
                        ""owner"",
                        ""blockNumber"" DESC,
                        ""timestamp"" DESC,
                        ""transactionIndex"" DESC,
                        ""logIndex"" DESC
                )
                SELECT
                    l.""blockNumber"",     
                    l.""timestamp"",       
                    l.""transactionIndex"",
                    l.""logIndex"",        
                    f.""safeAddress"",
                    f.""owner""
                FROM final_owners f
                         JOIN last_change l
                              ON f.""safeAddress"" = l.""safeAddress""
                                  AND f.""owner""       = l.""owner""
                ORDER BY f.""safeAddress"", f.""owner"";
         ")
    };

    public DatabaseSchema()
    {
        AddMappings<ProxyCreation>(
            ns: "Safe",
            table: "ProxyCreation",
            eventSchema: ProxyCreation,
            databaseFieldMap:
            [
                ("proxy", e => e.Proxy),
                ("singleton", e => e.Singleton)
            ]
        );

        AddMappings<SafeSetup>(
            ns: "Safe",
            table: "SafeSetup",
            eventSchema: SafeSetup,
            databaseFieldMap:
            [
                ("batchIndex", e => e.BatchIndex),
                ("safeAddress", e => e.SafeAddress),
                ("initiator", e => e.Initiator),
                ("owner", e => e.Owner),
                ("threshold", e => (BigInteger)e.Threshold),
                ("initializer", e => e.Initializer),
                ("fallbackHandler", e => e.FallbackHandler)
            ]
        );

        AddMappings<AddedOwner>(
            ns: "Safe",
            table: "AddedOwner",
            eventSchema: AddedOwner,
            databaseFieldMap:
            [
                ("safeAddress", e => e.SafeAddress),
                ("owner", e => e.Owner)
            ]
        );

        AddMappings<RemovedOwner>(
            ns: "Safe",
            table: "RemovedOwner",
            eventSchema: RemovedOwner,
            databaseFieldMap:
            [
                ("safeAddress", e => e.SafeAddress),
                ("owner", e => e.Owner)
            ]
        );

        Tables.Add(("V_Safe", "Owners"), V_Safe_Owners);
    }
}
