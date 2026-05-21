using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Circles.Common.Dto;
using Circles.Pathfinder.Simulation;
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
    IReadOnlyDictionary<string, string>? WrapperToAvatar = null);

/// <summary>
/// Background service that simulates pathfinder results via eth_call against the local
/// Nethermind node. Detects on-chain reverts that the pathfinder missed.
///
/// Architecture: bounded Channel (fire-and-forget, never blocks the response path).
/// If the queue is full, the new work item is dropped (canary is best-effort).
/// </summary>
internal sealed class SimulationCanaryService : BackgroundService
{
    private readonly Channel<CanaryWorkItem> _channel;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SimulationCanaryService> _log;
    private readonly string _rpcUrl;

    // Metrics
    private static readonly Counter SimulationTotal = Metrics.CreateCounter(
        "circles_canary_simulation_total",
        "Total eth_call simulations attempted",
        new CounterConfiguration { LabelNames = new[] { "result" } });

    private static readonly Counter SimulationRevertTotal = Metrics.CreateCounter(
        "circles_canary_simulation_revert_total",
        "Simulated reverts by category and label",
        new CounterConfiguration { LabelNames = new[] { "category", "label" } });

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("SimulationCanary started — rpcUrl={RpcUrl}, queue listening", _rpcUrl);

        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            QueueDepth.Set(_channel.Reader.Count);

            try
            {
                await SimulateAsync(item, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "[{ReqId}] SimulationCanary: unexpected error during simulation", item.ReqId);
                SimulationTotal.WithLabels("error").Inc();
            }
        }
    }

    private async Task SimulateAsync(CanaryWorkItem item, CancellationToken ct)
    {
        string calldata;
        try
        {
            // Hub.sol expects avatar addresses as token owners, not wrapper contract addresses.
            // The SDK does this conversion client-side; the canary must do it before simulation.
            var transfers = ResolveWrapperTokenOwners(item.Transfers, item.WrapperToAvatar);
            calldata = FlowMatrixEncoder.BuildCalldata(item.Source, item.Sink, transfers);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[{ReqId}] SimulationCanary: calldata encoding failed from={Source} to={Sink} block={Block} steps={Steps}",
                item.ReqId, item.Source, item.Sink, item.GraphBlock, item.Transfers.Count);
            SimulationTotal.WithLabels("encode_error").Inc();
            return;
        }

        using var timer = SimulationDuration.NewTimer();

        try
        {
            using var client = _httpClientFactory.CreateClient("canary-simulation");
            // Simulate at the exact block the graph was built from — eliminates false
            // positives from chain state drift between graph build and simulation.
            var blockTag = item.GraphBlock > 0 ? $"0x{item.GraphBlock:x}" : "latest";
            if (item.GraphBlock <= 0)
                _log.LogWarning("[{ReqId}] SimulationCanary: GraphBlock={Block} — simulating at 'latest', results may not match served graph",
                    item.ReqId, item.GraphBlock);

            var rpcRequest = new
            {
                jsonrpc = "2.0",
                method = "eth_call",
                @params = new object[]
                {
                    new { from = item.Source, to = FlowMatrixEncoder.CirclesHubAddress, data = calldata },
                    blockTag
                },
                id = 1
            };

            var response = await client.PostAsJsonAsync(_rpcUrl, rpcRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "[{ReqId}] SimulationCanary: eth_call HTTP {StatusCode} from={Source} to={Sink}",
                    item.ReqId, (int)response.StatusCode, item.Source, item.Sink);
                SimulationTotal.WithLabels("rpc_http_error").Inc();
                return;
            }

            JsonElement json;
            try
            {
                json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            }
            catch (JsonException jex)
            {
                _log.LogWarning(jex,
                    "[{ReqId}] SimulationCanary: non-JSON response from={Source} to={Sink}",
                    item.ReqId, item.Source, item.Sink);
                SimulationTotal.WithLabels("rpc_parse_error").Inc();
                return;
            }

            if (json.ValueKind == JsonValueKind.Undefined || json.ValueKind == JsonValueKind.Null)
            {
                _log.LogWarning(
                    "[{ReqId}] SimulationCanary: empty/null response from eth_call — node may be misconfigured",
                    item.ReqId);
                SimulationTotal.WithLabels("rpc_empty_response").Inc();
                return;
            }

            if (json.TryGetProperty("error", out var error))
            {
                var revertData = error.TryGetProperty("data", out var d) ? d.GetString() : null;
                var revertMsg = error.TryGetProperty("message", out var m) ? m.GetString() : "unknown";

                var (category, label) = RevertClassifier.Classify(revertData ?? revertMsg);

                SimulationTotal.WithLabels("revert").Inc();
                SimulationRevertTotal.WithLabels(category, label).Inc();

                // Full replay context: everything needed to reproduce
                _log.LogError(
                    "[{ReqId}] SimulationCanary: REVERT category={Category} label={Label} " +
                    "from={Source} to={Sink} graphBlock={Block} simBlock={SimBlock} steps={Steps} revert={Revert}",
                    item.ReqId, category, label,
                    item.Source, item.Sink, item.GraphBlock, blockTag,
                    item.Transfers.Count, revertMsg);

                if (revertData == null && revertMsg?.Contains("revert", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _log.LogWarning(
                        "[{ReqId}] SimulationCanary: revert has message but no data field — classification may be degraded",
                        item.ReqId);
                }
            }
            else
            {
                SimulationTotal.WithLabels("success").Inc();
            }
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // propagate shutdown
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[{ReqId}] SimulationCanary: eth_call failed (network/timeout) from={Source} to={Sink}",
                item.ReqId, item.Source, item.Sink);
            SimulationTotal.WithLabels("rpc_error").Inc();
        }

        QueueDepth.Set(_channel.Reader.Count);
    }

    /// <summary>
    /// Replaces wrapper contract addresses in TokenOwner fields with the underlying avatar address.
    /// Hub.sol's operateFlowMatrix expects avatar addresses (circlesId), not wrapper contracts.
    /// Returns the original list unchanged if no mapping is provided or no wrappers are found.
    /// </summary>
    private static IReadOnlyList<TransferPathStep> ResolveWrapperTokenOwners(
        List<TransferPathStep> transfers,
        IReadOnlyDictionary<string, string>? wrapperToAvatar)
    {
        if (wrapperToAvatar == null || wrapperToAvatar.Count == 0)
            return transfers;

        var resolved = new List<TransferPathStep>(transfers.Count);
        foreach (var t in transfers)
        {
            var tokenOwner = t.TokenOwner.ToLowerInvariant();
            if (wrapperToAvatar.TryGetValue(tokenOwner, out var avatar))
            {
                resolved.Add(new TransferPathStep
                {
                    From = t.From,
                    To = t.To,
                    TokenOwner = avatar,
                    Value = t.Value
                });
            }
            else
            {
                resolved.Add(t);
            }
        }

        return resolved;
    }
}
