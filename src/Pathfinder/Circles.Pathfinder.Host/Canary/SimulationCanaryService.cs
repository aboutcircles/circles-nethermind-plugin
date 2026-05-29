using System.Threading.Channels;
using Circles.Common;
using Circles.Common.Dto;
using Prometheus;

namespace Circles.Pathfinder.Host.Canary;

/// <summary>
/// Work item enqueued by FindPathHandler for background simulation.
/// </summary>
internal sealed record CanaryWorkItem(
    string ReqId,
    string Source,
    string Sink,
    long GraphBlock,
    List<TransferPathStep> Transfers,
    IReadOnlyDictionary<string, string>? WrapperToAvatar = null,
    bool WithWrap = false,
    // Subset of WrapperToAvatar keys (lowercased addresses) flagged as CirclesType.InflationaryCircles.
    // The canary's unwrap-prefix resolver uses this to discriminate the `_amount` unit semantic:
    // - DemurrageCircles (NOT in this set): unwrap(_amount) takes demurraged 1155 units; pass BuildUnwrapPrefix's sum directly.
    // - InflationaryCircles (IN this set):  unwrap(_amount) takes inflationary ERC20 units; convert via convertDemurrageToInflationaryValue.
    IReadOnlySet<string>? InflationaryWrappers = null);

/// <summary>
/// Background service that simulates pathfinder results via eth_call against the local
/// Nethermind node. Detects on-chain reverts that the pathfinder missed.
///
/// Architecture: bounded Channel (fire-and-forget, never blocks the response path).
/// If the queue is full, the new work item is dropped (canary is best-effort).
///
/// The implementation is split across multiple partial-class files for maintainability:
/// - SimulationCanaryService.cs                       — fields, ctor, TryEnqueue, metrics, CanaryWorkItem
/// - SimulationCanaryService.Lifecycle.cs             — ExecuteAsync, SimulateAsync (eth_call path)
/// - SimulationCanaryService.UnwrapCalldata.cs        — Demurraged/Resolved records, BuildUnwrapPrefix, EncodeUnwrapCalldata, PromoteAllDemurraged
/// - SimulationCanaryService.BundleSimulation.cs      — SimulateBundleAsync, BundleOutcome/BundleParseResult, ParseSimulateV1Response, ResolveWrapperTokenOwners
/// - SimulationCanaryService.InflationaryUnits.cs     — ComputeInflationDay, EncodeConvertDemurrageCalldata, ParseConvertCallReturnData, ExtractInflationaryAmounts, ApplyInflationaryAmounts, ApplyInflationaryRoundtripBump
/// - SimulationCanaryService.InflationaryResolver.cs  — FetchBlockTimestampAsync, ResolveInflationaryAmountsAsync
/// </summary>
internal sealed partial class SimulationCanaryService : BackgroundService
{
    private readonly Channel<CanaryWorkItem> _channel;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SimulationCanaryService> _log;
    private readonly string _rpcUrl;

    // Metrics. Counters that tests read are `internal` (see InternalsVisibleTo on
    // Circles.Pathfinder.Tests); promoting them is the existing pattern in this repo
    // (cf. GraphUpdateMetrics.DriftEntries usage in CacheSourceDriftResetTests).
    internal static readonly Counter SimulationTotal = Metrics.CreateCounter(
        "circles_canary_simulation_total",
        "Total eth_call/eth_simulateV1 simulations attempted",
        new CounterConfiguration { LabelNames = new[] { "result", "prefix" } });

    private static readonly Counter SimulationRevertTotal = Metrics.CreateCounter(
        "circles_canary_simulation_revert_total",
        "Simulated reverts by category, label, and which call in the bundle reverted",
        new CounterConfiguration { LabelNames = new[] { "category", "label", "stage" } });

    private static readonly Histogram SimulationDuration = Metrics.CreateHistogram(
        "circles_canary_simulation_duration_seconds",
        "eth_call simulation duration",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.005, 2, 10) });

    private static readonly Gauge QueueDepth = Metrics.CreateGauge(
        "circles_canary_simulation_queue_depth",
        "Current number of pending simulation work items");

    private static readonly Counter QueueDropped = Metrics.CreateCounter(
        "circles_canary_simulation_queue_dropped_total",
        "Work items dropped because the simulation queue was full");

    private static readonly Counter SimulationSkipped = Metrics.CreateCounter(
        "circles_canary_simulation_skipped_total",
        "Work items skipped before enqueue, by reason",
        new CounterConfiguration { LabelNames = new[] { "reason" } });

    // Observability for the 1ppt over-ask added in PR #426 (covers the on-chain
    // Math64x64 floor-floor roundtrip loss for InflationaryCircles wrappers).
    // Counter ticks once per inflationary unwrap whose raw resolver amount is
    // large enough to trigger a non-zero bump (I ≥ 1e12 wei). If Hub.sol's
    // conversion math ever changes and the gap exceeds 1 ppt, this counter
    // diverges from `circles_canary_simulation_total{result="success",prefix="unwrap"}`
    // and ERC1155InsufficientBalance reverts return — the divergence is the
    // operator signal that points at the bump as suspect.
    //
    // Deliberately a SEPARATE counter (not a SimulationTotal label): bump-applied
    // events are per-arithmetic-step (multiple per work item possible), while
    // SimulationTotal is per-simulation-outcome (one per work item). Folding them
    // would double-count. Also deliberately UNLABELLED — adding a {wrapper} label
    // would explode cardinality (one series per wrapper address ever seen). Per-
    // wrapper detail is in the DEBUG log on the bump path, not in the metric.
    internal static readonly Counter InflationaryBumpApplied = Metrics.CreateCounter(
        "circles_canary_inflationary_bump_applied_total",
        "Inflationary unwrap amounts where the floor-floor roundtrip over-ask was non-zero");

    public static void RecordSkipped(string reason) => SimulationSkipped.WithLabels(reason).Inc();

    public SimulationCanaryService(
        Settings settings,
        ILogger<SimulationCanaryService> log,
        IHttpClientFactory httpClientFactory)
    {
        _log = log;
        _httpClientFactory = httpClientFactory;
        _rpcUrl = settings.NethermindRpcUrl;

        int queueSize = int.TryParse(
            Environment.GetEnvironmentVariable("CANARY_SIMULATION_QUEUE_SIZE"), out var qs) ? qs : 10;

        // DropWrite: when queue is full, TryWrite returns false for the NEW item.
        // Oldest items are more valuable (closer to the graph block actually served).
        _channel = Channel.CreateBounded<CanaryWorkItem>(new BoundedChannelOptions(queueSize)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });
    }

    /// <summary>
    /// Enqueue a work item for background simulation. Returns false if queue is full (dropped).
    /// </summary>
    public bool TryEnqueue(CanaryWorkItem item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            QueueDepth.Set(_channel.Reader.Count);
            return true;
        }

        QueueDropped.Inc();
        return false;
    }
}
