using System.Text.Json.Serialization;

namespace Circles.Common.Dto;

/// <summary>
/// Debug information showing all transformation stages in the pathfinding pipeline.
/// Each stage shows how edges are transformed as they progress through the system.
/// </summary>
public class DebugPipelineStages
{
    /// <summary>
    /// Stage 1: Raw paths from MaxFlowSolver with token pools (tpool-0x...).
    /// Shows Avatar → TokenPool → Avatar paths before collapsing.
    /// </summary>
    [JsonPropertyName("rawPaths")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TransferPathStep>? RawPaths { get; set; }

    /// <summary>
    /// Stage 2: Token pools collapsed, showing Avatar → Avatar flows.
    /// Intermediate pool nodes removed, flows aggregated.
    /// </summary>
    [JsonPropertyName("collapsed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TransferPathStep>? Collapsed { get; set; }

    /// <summary>
    /// Stage 3: Router inserted for group mints.
    /// Avatar → Group becomes Avatar → Router → Group.
    /// </summary>
    [JsonPropertyName("routerInserted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TransferPathStep>? RouterInserted { get; set; }

    /// <summary>
    /// Stage 4: Final sorted order for contract execution.
    /// Ensures mint dependencies are satisfied (collateral before mints).
    /// </summary>
    [JsonPropertyName("sorted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TransferPathStep>? Sorted { get; set; }
}

/// <summary>
/// Response from path computation. Contains the maximum achievable flow and the transfer steps to execute on-chain.
/// </summary>
public class MaxFlowResponse
{
    /// <summary>
    /// Maximum achievable flow in CRC wei (uint256 as decimal string). This is the actual amount that can be transferred, which may be less than targetFlow.
    /// </summary>
    [JsonPropertyName("maxFlow")]
    public string MaxFlow { get; set; }

    /// <summary>
    /// Ordered list of individual token transfer steps to submit on-chain via Hub.sol operateFlowMatrix().
    /// </summary>
    [JsonPropertyName("transfers")]
    public List<TransferPathStep> Transfers { get; set; }

    /// <summary>
    /// Debug information showing transformation stages (only present if debugShowIntermediateSteps=true).
    /// </summary>
    [JsonPropertyName("debug")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DebugPipelineStages? Debug { get; set; }

    /// <summary>
    /// Number of solver paths dropped due to consent rules (intermediary exclusion or violation).
    /// Not serialized — used internally for metrics.
    /// </summary>
    [JsonIgnore]
    public int ConsentDroppedPaths { get; set; }

    /// <summary>
    /// Pipeline request ID for log correlation across canary layers.
    /// Not serialized — used internally for metrics and simulation canary.
    /// </summary>
    [JsonIgnore]
    public string? ReqId { get; set; }

    /// <summary>
    /// Number of Hub.sol rule violations detected by HubContractValidator. When non-zero,
    /// the pathfinder produced output that would have reverted on-chain and the response's
    /// transfers have been replaced with the empty path by the safety net. Serialized only
    /// when non-zero so callers can self-diagnose path rejections without log access.
    /// </summary>
    [JsonPropertyName("validationErrors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ValidationErrors { get; set; }

    /// <summary>
    /// Hub.sol rule names of each detected violation (duplicates are removed by
    /// HubContractValidator before assignment). Serialized when not null. The producer is
    /// expected to leave this null when there are zero violations rather than assigning
    /// an empty list; an empty list would appear on the wire as `[]`.
    /// </summary>
    [JsonPropertyName("validationViolationRules")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ValidationViolationRules { get; set; }

    /// <summary>
    /// True if HubContractValidator threw an unexpected exception (validator bug, not
    /// pathfinder bug). Serialized only when true.
    /// </summary>
    [JsonPropertyName("validatorException")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ValidatorException { get; set; }

    /// <summary>
    /// Number of warning-severity violations detected by HubContractValidator. Warnings
    /// are observe-only — they NEVER cause the response to be replaced with the empty
    /// path; the transfers are returned as-is. Used by diagnostic rules whose root cause
    /// is still under investigation (the path may or may not actually revert on-chain).
    /// Serialized only when non-zero.
    /// </summary>
    [JsonPropertyName("validationWarnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ValidationWarnings { get; set; }

    /// <summary>
    /// Rule names of each detected warning-severity violation. Serialized when not null.
    /// </summary>
    [JsonPropertyName("validationWarningRules")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ValidationWarningRules { get; set; }

    /// <summary>
    /// Block number of the graph snapshot used for this computation.
    /// Lets callers detect staleness by comparing to the current chain head.
    /// </summary>
    [JsonPropertyName("graphBlock")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long GraphBlock { get; set; }

    public MaxFlowResponse(string maxFlow, List<TransferPathStep> transfers, DebugPipelineStages? debug = null)
    {
        MaxFlow = maxFlow;
        Transfers = transfers;
        Debug = debug;
    }
}
