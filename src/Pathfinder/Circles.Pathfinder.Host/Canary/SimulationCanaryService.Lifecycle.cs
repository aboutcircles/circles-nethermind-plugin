using System.Net.Http.Json;
using System.Text.Json;
using Circles.Common;
using Circles.Pathfinder.Simulation;
using Prometheus;

namespace Circles.Pathfinder.Host.Canary;

/// <summary>
/// BackgroundService loop and per-item simulation orchestration for the canary.
/// Drains the bounded channel, builds the calldata for each work item, and dispatches to
/// either plain eth_call (no wrapper supply) or the unwrap-prefix eth_simulateV1 bundle.
/// </summary>
internal sealed partial class SimulationCanaryService
{
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
                SimulationTotal.WithLabels("error", "none").Inc();
            }
        }
    }

    private async Task SimulateAsync(CanaryWorkItem item, CancellationToken ct)
    {
        // Compute unwrap prefix once — the same list is needed to decide which RPC method to use
        // AND to populate the bundle when withWrap=true. Resolve TokenOwners using the same map
        // so the operateFlowMatrix calldata references avatar addresses, not wrappers.
        IReadOnlyList<DemurragedUnwrapCall> unwrapCalls;
        string calldata;
        try
        {
            unwrapCalls = item.WithWrap
                ? BuildUnwrapPrefix(item.Transfers, item.WrapperToAvatar, item.InflationaryWrappers)
                : Array.Empty<DemurragedUnwrapCall>();
            var transfers = ResolveWrapperTokenOwners(item.Transfers, item.WrapperToAvatar);
            calldata = FlowMatrixEncoder.BuildCalldata(item.Source, item.Sink, transfers);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "[{ReqId}] SimulationCanary: calldata encoding failed from={Source} to={Sink} block={Block} steps={Steps}",
                item.ReqId, item.Source, item.Sink, item.GraphBlock, item.Transfers.Count);
            SimulationTotal.WithLabels("encode_error", "none").Inc();
            return;
        }

        // Empty unwrap list ⇒ no wrapper-form supply in this path. Fall back to plain eth_call
        // even when withWrap=true was requested — the unwrap-prefix code path adds nothing here
        // and eth_call has wider node-version support.
        bool useUnwrapBundle = unwrapCalls.Count > 0;
        string prefixLabel = useUnwrapBundle ? "unwrap" : "none";

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

            if (useUnwrapBundle)
            {
                // Resolver ALWAYS runs in the withWrap path — the type system (DemurragedUnwrapCall
                // → ResolvedUnwrapCall) forces this. For bundles with no InflationaryCircles
                // entries the resolver short-circuits as a pure pass-through (no RPC overhead).
                IReadOnlyList<ResolvedUnwrapCall> resolved =
                    await ResolveInflationaryAmountsAsync(item, unwrapCalls, blockTag, client, ct);

                await SimulateBundleAsync(item, resolved, calldata, blockTag, prefixLabel, client, ct);
                QueueDepth.Set(_channel.Reader.Count);
                return;
            }

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
                SimulationTotal.WithLabels("rpc_http_error", prefixLabel).Inc();
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
                SimulationTotal.WithLabels("rpc_parse_error", prefixLabel).Inc();
                return;
            }

            if (json.ValueKind == JsonValueKind.Undefined || json.ValueKind == JsonValueKind.Null)
            {
                _log.LogWarning(
                    "[{ReqId}] SimulationCanary: empty/null response from eth_call — node may be misconfigured",
                    item.ReqId);
                SimulationTotal.WithLabels("rpc_empty_response", prefixLabel).Inc();
                return;
            }

            if (json.TryGetProperty("error", out var error))
            {
                var revertData = error.TryGetProperty("data", out var d) ? d.GetString() : null;
                var revertMsg = error.TryGetProperty("message", out var m) ? m.GetString() : "unknown";

                var (category, label) = RevertClassifier.Classify(revertData ?? revertMsg);

                SimulationTotal.WithLabels("revert", prefixLabel).Inc();
                SimulationRevertTotal.WithLabels(category, label, "flow_matrix").Inc();

                // Full replay context: everything needed to reproduce (incl. calldata
                // so the canary log alone is sufficient to feed scripts/_resources/run-sim.sh).
                _log.LogError(
                    "[{ReqId}] SimulationCanary: REVERT category={Category} label={Label} " +
                    "from={Source} to={Sink} graphBlock={Block} simBlock={SimBlock} steps={Steps} revert={Revert} calldata={Calldata}",
                    item.ReqId, category, label,
                    item.Source, item.Sink, item.GraphBlock, blockTag,
                    item.Transfers.Count, revertMsg, calldata);

                // #74 cache-balance-drift: ERC1155InsufficientBalance carries the holder's on-chain
                // balance and the amount the pathfinder's path required, both at graphBlock. Decode
                // from the SAME source Classify used (revertData ?? revertMsg) — the selector hex can
                // arrive in the message field on some nodes.
                EmitBalanceDriftIfDetected(item, label, revertData ?? revertMsg);

                if (revertData == null && revertMsg?.Contains("revert", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _log.LogWarning(
                        "[{ReqId}] SimulationCanary: revert has message but no data field — classification may be degraded",
                        item.ReqId);
                }
            }
            else
            {
                SimulationTotal.WithLabels("success", prefixLabel).Inc();
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
            SimulationTotal.WithLabels("rpc_error", prefixLabel).Inc();
        }

        QueueDepth.Set(_channel.Reader.Count);
    }

    // #74 cache-balance-drift emit, shared by both simulation paths (plain eth_call in this file
    // and the eth_simulateV1 bundle in BundleSimulation.cs). When needed ≫ on-chain the graph source
    // fed an inflated balance; surface it the instant it happens — it self-heals on the next full
    // cache refresh, so post-hoc probing misses it. Holder/token go to the LOG only (unbounded
    // cardinality); the metric is bucketed by the needed/on-chain ratio.
    //
    // The whole body is wrapped in its own narrow try/catch so a future decode/emit regression
    // surfaces as a DISTINCT error rather than masquerading as a surrounding "eth_call
    // network/timeout" catch.
    private void EmitBalanceDriftIfDetected(CanaryWorkItem item, string label, string? revertPayload)
    {
        if (label != "insufficient_balance")
            return;

        try
        {
            if (RevertClassifier.TryDecodeInsufficientBalance(revertPayload) is not { } drift)
                return;

            var bucket = RevertClassifier.DriftBucket(drift.Needed, drift.Balance);
            BalanceDriftTotal.WithLabels(bucket).Inc();
            _log.LogError(
                "[{ReqId}] SimulationCanary: CACHE BALANCE DRIFT holder={Holder} token={Token} " +
                "graphBlock={Block} onChainBalance={OnChain} needed={Needed} ratioBucket={Bucket}",
                item.ReqId, drift.Holder, drift.Token,
                item.GraphBlock, drift.Balance, drift.Needed, bucket);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[{ReqId}] SimulationCanary: balance-drift decode/emit failed", item.ReqId);
        }
    }
}
