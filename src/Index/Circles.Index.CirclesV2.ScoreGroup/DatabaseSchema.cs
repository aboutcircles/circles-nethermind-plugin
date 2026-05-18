using System.Numerics;
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
    byte[] NewMerkleRoot,
    byte[] PreviousRoot,
    UInt256 UpdateBlockNumber) : IIndexEvent;

public record HistoricalSupply(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Group,
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

// Emitted by the ScoreGroup contract itself in its constructor. `Emitter` is
// the ScoreGroup contract address; we don't carry a separate `group` column
// because the emitter IS the group identity here.
public record ScoreGroupInitialized(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string MetadataManager,
    string MintRouter,
    string Treasury,
    string StableErc20,
    string DemurrageErc20) : IIndexEvent;

// Emitted by the ScoreGroup contract when a human avatar opts out of (or back
// into) the group's scoring. `Emitter` is the ScoreGroup contract address.
public record OptOutStatusChanged(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Member,
    bool OptedOut) : IIndexEvent;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema GroupInitialized = WithEmitterColumn(EventSchema.FromSolidity(
        "CrcV2_ScoreGroup",
        "event GroupInitialized(address indexed group,address indexed merkleTreeManager,address pathMintRouter)"));

    public static readonly EventSchema MerkleRootUpdated = WithEmitterColumn(EventSchema.FromSolidity(
        "CrcV2_ScoreGroup",
        "event MerkleRootUpdated(address indexed group,bytes32 newMerkleRoot,bytes32 previousRoot,uint256 updateBlockNumber)"));

    public static readonly EventSchema HistoricalSupply = WithEmitterColumn(EventSchema.FromSolidity(
        "CrcV2_ScoreGroup",
        "event HistoricalSupply(address indexed group,uint256 indexed collateral,uint256 supply,uint256 day)"));

    public static readonly EventSchema PersonalMinted = WithEmitterColumn(EventSchema.FromSolidity(
        "CrcV2_ScoreGroup",
        "event PersonalMinted(address indexed group,uint256 indexed collateral,uint256 amount,uint256 score,uint256 mintedAmountOnToday,uint256 day)"));

    public static readonly EventSchema RouterMinted = WithEmitterColumn(EventSchema.FromSolidity(
        "CrcV2_ScoreGroup",
        "event RouterMinted(address indexed group,uint256 indexed collateral,uint256 amount,uint256 currentAvailableLimit)"));

    public static readonly EventSchema ScoreGroupInitialized = WithEmitterColumn(EventSchema.FromSolidity(
        "CrcV2_ScoreGroup",
        "event ScoreGroupInitialized(address indexed metadataManager,address indexed mintRouter,address indexed treasury,address stableERC20,address demurrageERC20)"));

    public static readonly EventSchema OptOutStatusChanged = WithEmitterColumn(EventSchema.FromSolidity(
        "CrcV2_ScoreGroup",
        "event OptOutStatusChanged(address indexed member,bool optedOut)"));

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
                ("newMerkleRoot", e => e.NewMerkleRoot),
                ("previousRoot", e => e.PreviousRoot),
                ("updateBlockNumber", e => (BigInteger)e.UpdateBlockNumber)
            ]);

        AddMappings<HistoricalSupply>(
            ns: "CrcV2_ScoreGroup",
            table: "HistoricalSupply",
            eventSchema: HistoricalSupply,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("group", e => e.Group),
                ("collateral", e => (BigInteger)e.Collateral),
                ("supply", e => (BigInteger)e.Supply),
                ("day", e => (BigInteger)e.Day)
            ]);

        AddMappings<PersonalMinted>(
            ns: "CrcV2_ScoreGroup",
            table: "PersonalMinted",
            eventSchema: PersonalMinted,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("group", e => e.Group),
                ("collateral", e => (BigInteger)e.Collateral),
                ("amount", e => (BigInteger)e.Amount),
                ("score", e => (BigInteger)e.Score),
                ("mintedAmountOnToday", e => (BigInteger)e.MintedAmountOnToday),
                ("day", e => (BigInteger)e.Day)
            ]);

        AddMappings<RouterMinted>(
            ns: "CrcV2_ScoreGroup",
            table: "RouterMinted",
            eventSchema: RouterMinted,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("group", e => e.Group),
                ("collateral", e => (BigInteger)e.Collateral),
                ("amount", e => (BigInteger)e.Amount),
                ("currentAvailableLimit", e => (BigInteger)e.CurrentAvailableLimit)
            ]);

        AddMappings<ScoreGroupInitialized>(
            ns: "CrcV2_ScoreGroup",
            table: "ScoreGroupInitialized",
            eventSchema: ScoreGroupInitialized,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("metadataManager", e => e.MetadataManager),
                ("mintRouter", e => e.MintRouter),
                ("treasury", e => e.Treasury),
                ("stableERC20", e => e.StableErc20),
                ("demurrageERC20", e => e.DemurrageErc20)
            ]);

        AddMappings<OptOutStatusChanged>(
            ns: "CrcV2_ScoreGroup",
            table: "OptOutStatusChanged",
            eventSchema: OptOutStatusChanged,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("member", e => e.Member),
                ("optedOut", e => e.OptedOut)
            ]);
    }
}
