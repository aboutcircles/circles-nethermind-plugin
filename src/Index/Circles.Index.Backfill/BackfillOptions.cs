namespace Circles.Index.Backfill;

public class BackfillOptions
{
    /// <summary>
    /// Tables to backfill (e.g., ["CrcV2_FlowEdgesScopeSingleStarted", "CrcV2_FlowEdgesScopeLastEnded"])
    /// </summary>
    public required string[] Tables { get; init; }

    /// <summary>
    /// Block number to start backfill from.
    /// Default: 37534026 (V2 Hub deployment on Gnosis)
    /// </summary>
    public long FromBlock { get; init; } = 37534026;

    /// <summary>
    /// Block number to end backfill at.
    /// Default: null (uses current System_Block max)
    /// </summary>
    public long? ToBlock { get; init; }

    /// <summary>
    /// PostgreSQL connection string
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Nethermind JSON-RPC URL for fetching blocks and receipts
    /// </summary>
    public string RpcUrl { get; init; } = "http://localhost:8545";

    /// <summary>
    /// Number of blocks to process per batch
    /// </summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>
    /// If true, parse blocks but don't write to database
    /// </summary>
    public bool DryRun { get; init; } = false;

    /// <summary>
    /// V2 Hub contract address (default: Gnosis mainnet)
    /// </summary>
    public string? V2HubAddress { get; init; }

    /// <summary>
    /// If true, bypass the safety check that verifies the indexer is not running
    /// </summary>
    public bool Force { get; init; } = false;
}
