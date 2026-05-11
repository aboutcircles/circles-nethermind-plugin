using Circles.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.ScoreGroup;

public record GroupInitialized(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Group,
    string MerkleTreeManager,
    string PathMintRouter) : IIndexEvent;

public record MerkleRootUpdated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Group,
    byte[] NewMerkleRoot) : IIndexEvent;

public record HistoricalSupply(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    UInt256 Collateral,
    UInt256 Supply,
    UInt256 Day) : IIndexEvent;

public record PersonalMinted(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Group,
    UInt256 Collateral,
    UInt256 Amount,
    UInt256 Score,
    UInt256 MintedAmountOnToday,
    UInt256 Day) : IIndexEvent;

public record RouterMinted(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Group,
    UInt256 Collateral,
    UInt256 Amount,
    UInt256 CurrentAvailableLimit) : IIndexEvent;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema GroupInitialized = WithEmitterColumn(EventSchema.FromSolidity(
        "CrcV2_ScoreGroup",
        "event GroupInitialized(address indexed group,address indexed merkleTreeManager,address pathMintRouter)"));

    public static readonly EventSchema MerkleRootUpdated = WithEmitterColumn(EventSchema.FromSolidity(
        "CrcV2_ScoreGroup",
        "event MerkleRootUpdated(address indexed group,bytes32 newMerkleRoot)"));

    public static readonly EventSchema HistoricalSupply = WithEmitterColumn(EventSchema.FromSolidity(
        "CrcV2_ScoreGroup",
        "event HistoricalSupply(uint256 indexed collateral,uint256 supply,uint256 day)"));

    public static readonly EventSchema PersonalMinted = WithEmitterColumn(EventSchema.FromSolidity(
        "CrcV2_ScoreGroup",
        "event PersonalMinted(address indexed group,uint256 indexed collateral,uint256 amount,uint256 score,uint256 mintedAmountOnToday,uint256 day)"));

    public static readonly EventSchema RouterMinted = WithEmitterColumn(EventSchema.FromSolidity(
        "CrcV2_ScoreGroup",
        "event RouterMinted(address indexed group,uint256 indexed collateral,uint256 amount,uint256 currentAvailableLimit)"));

    private static EventSchema WithEmitterColumn(EventSchema schema)
    {
        schema.Columns.Insert(5, new EventFieldSchema("emitter", ValueTypes.Address, true));
        return schema;
    }

    public DatabaseSchema()
    {
        AddMappings<GroupInitialized>(
            ns: "CrcV2_ScoreGroup",
            table: "GroupInitialized",
            eventSchema: GroupInitialized,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("group", e => e.Group),
                ("merkleTreeManager", e => e.MerkleTreeManager),
                ("pathMintRouter", e => e.PathMintRouter)
            ]);

        AddMappings<MerkleRootUpdated>(
            ns: "CrcV2_ScoreGroup",
            table: "MerkleRootUpdated",
            eventSchema: MerkleRootUpdated,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("group", e => e.Group),
                ("newMerkleRoot", e => e.NewMerkleRoot)
            ]);

        AddMappings<HistoricalSupply>(
            ns: "CrcV2_ScoreGroup",
            table: "HistoricalSupply",
            eventSchema: HistoricalSupply,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("collateral", e => e.Collateral),
                ("supply", e => e.Supply),
                ("day", e => e.Day)
            ]);

        AddMappings<PersonalMinted>(
            ns: "CrcV2_ScoreGroup",
            table: "PersonalMinted",
            eventSchema: PersonalMinted,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("group", e => e.Group),
                ("collateral", e => e.Collateral),
                ("amount", e => e.Amount),
                ("score", e => e.Score),
                ("mintedAmountOnToday", e => e.MintedAmountOnToday),
                ("day", e => e.Day)
            ]);

        AddMappings<RouterMinted>(
            ns: "CrcV2_ScoreGroup",
            table: "RouterMinted",
            eventSchema: RouterMinted,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("group", e => e.Group),
                ("collateral", e => e.Collateral),
                ("amount", e => e.Amount),
                ("currentAvailableLimit", e => e.CurrentAvailableLimit)
            ]);
    }
}
