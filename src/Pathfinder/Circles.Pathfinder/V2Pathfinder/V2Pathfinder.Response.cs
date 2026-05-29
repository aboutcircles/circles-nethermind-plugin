using System.Globalization;
using System.Linq;
using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Edges;
using Microsoft.Extensions.Logging;
using Nethermind.Int256;

namespace Circles.Pathfinder;

public partial class V2Pathfinder
{
    /* ======================================================================
     * Stage 11: Build transfer DTOs from flow edges
     * ====================================================================== */

    private void BuildTransferDtos(PipelineContext ctx)
    {
        if (ctx.WantDebug && ctx.Request.QuantizedMode != true)
            ctx.DebugStages!.Sorted = ConvertFlowEdgesToTransferSteps(ctx.Edges);

        ctx.Transfers = new List<TransferPathStep>();

        foreach (var e in ctx.Edges)
        {
            if (e.Flow <= 0)
                continue;

            // Skip sink self-loop edges (display-only, violate flow conservation)
            if (e.From == ctx.SinkId && e.To == ctx.SinkId)
                continue;

            // Resolve From/To — wrapper addresses can leak into the graph as avatar
            // nodes via trust query UNION 2 and appear as flow intermediaries.
            // Hub.sol rejects wrapper addresses as flow vertices (CirclesAvatarMustBeRegistered).
            int resolvedFrom = ctx.Graph.WrapperToAvatar.TryGetValue(e.From, out int fromAvatarId)
                ? fromAvatarId : e.From;
            int resolvedTo = ctx.Graph.WrapperToAvatar.TryGetValue(e.To, out int toAvatarId)
                ? toAvatarId : e.To;

            if (resolvedFrom != e.From || resolvedTo != e.To)
            {
                _logger?.LogWarning(
                    "Resolved wrapper address in flow vertex: From={OrigFrom}→{ResFrom}, To={OrigTo}→{ResTo}",
                    AddressIdPool.StringOf(e.From), AddressIdPool.StringOf(resolvedFrom),
                    AddressIdPool.StringOf(e.To), AddressIdPool.StringOf(resolvedTo));
            }

            // TokenOwner: keep the original token ID (wrapper address for wrapped tokens).
            // Callers use this to determine if unwrapping is needed before Hub.sol submission.
            // For native CRC tokens, e.Token is already the avatar address.
            ctx.Transfers.Add(new TransferPathStep
            {
                From = AddressIdPool.StringOf(resolvedFrom),
                To = AddressIdPool.StringOf(resolvedTo),
                TokenOwner = AddressIdPool.StringOf(e.Token),
                Value = CirclesConverter
                    .BlowUpToUInt256(e.Flow)
                    .ToString(CultureInfo.InvariantCulture)
            });
        }

        // Debug: compact transfer summary for replay analysis
        if (ctx.Transfers?.Count > 0 && _logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            static string Trunc(string s) => s[..Math.Min(10, s.Length)];
            var summary = string.Join(" | ", ctx.Transfers.Select(t =>
                $"{Trunc(t.From)}→{Trunc(t.To)} token={Trunc(t.TokenOwner)} val={t.Value}"));
            _logger.LogDebug("[{ReqId}] Transfers: {Summary}", ctx.ReqId, summary);
        }

        // Path audit: run HubContractValidator on every response (observe-only, NEVER blocks).
        //
        // Two separate try blocks intentionally:
        //   - The error-gathering block sets ctx.ValidatorException on failure, which
        //     FindPathHandler reads to replace the response with an empty path (the
        //     error path is the only one that gates the response). A failure to even
        //     enumerate error-severity violations means we have no proof the path is
        //     safe → fail-closed by blocking.
        //   - The warning-gathering block is OBSERVE-ONLY: a failure to enumerate
        //     warnings (e.g. NRE on a concurrent _balanceIndex read, an
        //     AddressIdPool key-not-found) must NEVER set ValidatorException,
        //     otherwise a warning-only diagnostic could block legitimate paths.
        //     Log and continue with empty warning telemetry.
        if (ctx.Transfers.Count > 0)
        {
            IReadOnlyList<Validation.ValidationViolation> allViolations =
                Array.Empty<Validation.ValidationViolation>();

            try
            {
                var contractState = new Validation.CapacityGraphContractState(ctx.Graph);
                var validation = Validation.HubContractValidator.Validate(
                    ctx.Transfers, ctx.Request.Source!, ctx.Request.Sink!, contractState);
                allViolations = validation.Violations;

                var errorViolations = allViolations.Where(v => v.Severity == "error").ToList();
                if (errorViolations.Count > 0)
                {
                    var errors = errorViolations.Select(v => $"[{v.Rule}] {v.Message}");
                    _logger.LogError(
                        "[{ReqId}] Path audit: REJECTED from={Source} to={Sink} block={Block} violations: {Violations}",
                        ctx.ReqId, ctx.Request.Source, ctx.Request.Sink, ctx.Graph.Block,
                        string.Join("; ", errors));
                }

                ctx.ValidationErrors = errorViolations.Count;
                // Deduplicate rules so per-rule audit metrics aren't multiply incremented
                // when a single response has several violations of the same rule.
                ctx.ValidationViolationRules = errorViolations
                    .Select(v => v.Rule)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[{ReqId}] Path audit: unexpected exception during error-gathering (observe-only, not blocking response)",
                    ctx.ReqId);
                ctx.ValidatorException = true;
            }

            // Warning-severity violations are observe-only: they log + surface in a
            // separate per-rule metrics bucket, never block the response. Used by
            // diagnostic rules (e.g. HolderBalanceAvailable) that flag pathfinder
            // bugs whose root cause is not yet pinpointed — operator sees the alert
            // upstream, the path still returns so users aren't blocked on uncertain
            // signal. A failure HERE must NOT set ValidatorException — see comment
            // block above.
            try
            {
                var warningViolations = allViolations.Where(v => v.Severity == "warning").ToList();
                if (warningViolations.Count > 0)
                {
                    var warnings = warningViolations.Select(v => $"[{v.Rule}] {v.Message}");
                    _logger.LogWarning(
                        "[{ReqId}] Path audit: {Count} warning(s) from={Source} to={Sink} block={Block}: {Violations}",
                        ctx.ReqId, warningViolations.Count, ctx.Request.Source, ctx.Request.Sink,
                        ctx.Graph.Block, string.Join("; ", warnings));
                }
                ctx.ValidationWarnings = warningViolations.Count;
                ctx.ValidationWarningRules = warningViolations
                    .Select(v => v.Rule)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex)
            {
                // Warning gathering is observe-only: log + continue with empty telemetry.
                // Do NOT set ValidatorException — that gate is reserved for the error path.
                _logger.LogError(ex,
                    "[{ReqId}] Path audit: unexpected exception during warning-gathering — telemetry suppressed for this request, response not blocked",
                    ctx.ReqId);
            }
        }
    }

    /* ======================================================================
     * Stage 12: Calculate maxFlow and assemble response
     * ====================================================================== */

    private MaxFlowResponse BuildResponse(PipelineContext ctx)
    {
        UInt256 maxFlowWei = 0;
        foreach (var t in ctx.Transfers)
        {
            if (AddressIdPool.IdOf(t.To) == ctx.SinkId)
                maxFlowWei += UInt256.Parse(t.Value);
        }

        ctx.TotalStopwatch.Stop();
        _logger.LogInformation(
            "[{ReqId}] Result: from={Source} to={Sink} maxFlow={MaxFlow}, steps={Steps}, block={Block}, totalMs={TotalMs}",
            ctx.ReqId, ctx.Request.Source, ctx.Request.Sink, maxFlowWei,
            ctx.Transfers.Count, ctx.Graph.Block, ctx.TotalStopwatch.ElapsedMilliseconds);

        return new MaxFlowResponse(
            maxFlowWei.ToString(CultureInfo.InvariantCulture),
            ctx.Transfers,
            ctx.DebugStages)
        {
            ReqId = ctx.ReqId,
            ConsentDroppedPaths = ctx.ConsentDroppedPaths,
            ValidationErrors = ctx.ValidationErrors,
            ValidationViolationRules = ctx.ValidationViolationRules,
            ValidatorException = ctx.ValidatorException,
            ValidationWarnings = ctx.ValidationWarnings,
            ValidationWarningRules = ctx.ValidationWarningRules
        };
    }

    /* ------------------------------------------------------------------------
     * DEBUG HELPERS: Convert internal edge representations to TransferPathStep
     * for debug output showing all transformation stages.
     * --------------------------------------------------------------------- */

    /// <summary>
    /// Convert raw paths (List of SimpleEdge lists) to TransferPathStep list.
    /// Flattens all paths and shows edges with token pools visible.
    /// </summary>
    private static List<TransferPathStep> ConvertSimplePathsToTransferSteps(IReadOnlyList<List<SimpleEdge>> paths)
    {
        var result = new List<TransferPathStep>();

        foreach (var path in paths)
        {
            foreach (var edge in path)
            {
                if (edge.Flow <= 0)
                    continue;

                result.Add(new TransferPathStep
                {
                    From = AddressIdPool.StringOf(edge.From),
                    To = AddressIdPool.StringOf(edge.To),
                    TokenOwner = AddressIdPool.StringOf(edge.Token),
                    Value = CirclesConverter
                        .BlowUpToUInt256(edge.Flow)
                        .ToString(CultureInfo.InvariantCulture)
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Convert FlowEdge list to TransferPathStep list.
    /// </summary>
    private static List<TransferPathStep> ConvertFlowEdgesToTransferSteps(IReadOnlyList<FlowEdge> edges)
    {
        var result = new List<TransferPathStep>(edges.Count);

        foreach (var edge in edges)
        {
            if (edge.Flow <= 0)
                continue;

            result.Add(new TransferPathStep
            {
                From = AddressIdPool.StringOf(edge.From),
                To = AddressIdPool.StringOf(edge.To),
                TokenOwner = AddressIdPool.StringOf(edge.Token),
                Value = CirclesConverter
                    .BlowUpToUInt256(edge.Flow)
                    .ToString(CultureInfo.InvariantCulture)
            });
        }

        return result;
    }
}
