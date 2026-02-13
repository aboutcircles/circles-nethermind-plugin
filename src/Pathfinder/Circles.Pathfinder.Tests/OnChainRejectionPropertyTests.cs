using System.Globalization;
using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Circles.Pathfinder.Validation;
using Nethermind.Int256;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Property-based tests that validate pathfinder output against HubContractValidator.
/// If the pathfinder produces output that the validator rejects, it's a real bug.
/// If a mutated path passes the validator, the validator has a blind spot.
/// </summary>
[TestFixture]
public class OnChainRejectionPropertyTests
{
    private const long DefaultBalance = 1_000_000L;

    #region P1: Random graphs — pathfinder output always passes validator

    /// <summary>
    /// For any random graph, the pathfinder's output should always pass validation.
    /// A failure here means the pathfinder is producing paths the contract would reject.
    /// </summary>
    [Test]
    [Repeat(30)]
    public void P1_RandomGraph_PathfinderOutput_AlwaysPassesValidator()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 10000);
        int avatars = rng.Next(3, 15);
        int groups = rng.Next(0, 3);
        double density = rng.NextDouble() * 0.6 + 0.1;
        bool consent = rng.NextDouble() > 0.5;

        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, groups, DefaultBalance, rng,
            trustDensity: density, withRouter: groups > 0, withConsent: consent, consentRate: 0.5);
        var (source, sink) = PropertyBasedTests.PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var sourceAddr = AddressIdPool.StringOf(source);
        var sinkAddr = AddressIdPool.StringOf(sink);
        var request = new FlowRequest { Source = sourceAddr, Sink = sinkAddr };
        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        MaxFlowResponse result;
        try
        {
            result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("BAD_INPUT"))
        {
            return; // Empty graph is valid — no path to validate
        }

        if (result.Transfers == null || result.Transfers.Count == 0)
            return; // No flow found — nothing to validate

        var state = new CapacityGraphContractState(graph);
        var validation = HubContractValidator.Validate(result.Transfers, sourceAddr, sinkAddr, state);

        if (!validation.IsValid)
        {
            var errors = validation.Violations
                .Where(v => v.Severity == "error")
                .Select(v => $"  [{v.Rule}] {v.Message}")
                .ToList();

            if (errors.Count > 0)
            {
                Assert.Fail(
                    $"Pathfinder output REJECTED by validator!\n" +
                    $"Graph: {avatars} avatars, {groups} groups, density={density:F2}, consent={consent}\n" +
                    $"Errors:\n{string.Join("\n", errors)}");
            }
        }
    }

    #endregion

    #region P2: Consented flow — never produces invalid edges

    /// <summary>
    /// Graphs with consented flow should produce paths that pass isPermittedFlow validation.
    /// </summary>
    [Test]
    [Repeat(20)]
    public void P2_ConsentedFlow_NeverProducesInvalidEdges()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 20000);
        int avatars = rng.Next(4, 12);
        double consentRate = rng.NextDouble() * 0.4 + 0.3; // 30-70%

        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, 0, DefaultBalance, rng,
            trustDensity: 0.4, withRouter: false, withConsent: true, consentRate: consentRate);
        var (source, sink) = PropertyBasedTests.PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var sourceAddr = AddressIdPool.StringOf(source);
        var sinkAddr = AddressIdPool.StringOf(sink);
        var request = new FlowRequest { Source = sourceAddr, Sink = sinkAddr };
        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        MaxFlowResponse result;
        try
        {
            result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (result.Transfers == null || result.Transfers.Count == 0)
            return;

        var state = new CapacityGraphContractState(graph);
        var validation = HubContractValidator.Validate(result.Transfers, sourceAddr, sinkAddr, state);

        var permissionViolations = validation.Violations
            .Where(v => (v.Rule == "IsPermittedFlow" || v.Rule == "FlowConservation") && v.Severity == "error")
            .ToList();

        Assert.That(permissionViolations, Is.Empty,
            $"Violations with consentRate={consentRate:F2}: " +
            string.Join("; ", permissionViolations.Select(v => $"[{v.Rule}] {v.Message}")));
    }

    #endregion

    #region P3: Group minting — always collateral before mint

    /// <summary>
    /// Graphs with groups should always have collateral deposited before minting.
    /// </summary>
    [Test]
    [Repeat(20)]
    public void P3_GroupMinting_AlwaysCollateralBeforeMint()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 30000);
        int avatars = rng.Next(4, 10);
        int groups = rng.Next(1, 4);

        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, groups, DefaultBalance, rng,
            trustDensity: 0.5, withRouter: true);
        var (source, sink) = PropertyBasedTests.PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var sourceAddr = AddressIdPool.StringOf(source);
        var sinkAddr = AddressIdPool.StringOf(sink);
        var request = new FlowRequest { Source = sourceAddr, Sink = sinkAddr };
        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        MaxFlowResponse result;
        try
        {
            result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (result.Transfers == null || result.Transfers.Count == 0)
            return;

        var state = new CapacityGraphContractState(graph);
        var validation = HubContractValidator.Validate(result.Transfers, sourceAddr, sinkAddr, state);

        var mintViolations = validation.Violations
            .Where(v => v.Rule == "CollateralBeforeMint" && v.Severity == "error")
            .ToList();

        Assert.That(mintViolations, Is.Empty,
            $"CollateralBeforeMint violations with {groups} groups: " +
            string.Join("; ", mintViolations.Select(v => v.Message)));
    }

    #endregion

    #region P4: Mutated paths — validator detects violations

    /// <summary>
    /// Generate valid paths, mutate them, and verify the validator catches the mutation.
    /// This tests that the validator isn't a no-op.
    /// </summary>
    [Test]
    [Repeat(20)]
    public void P4_MutatedPaths_ValidatorDetectsViolations()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 40000);
        int avatars = rng.Next(4, 10);
        int groups = rng.Next(0, 2);

        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, groups, DefaultBalance, rng,
            trustDensity: 0.5, withRouter: groups > 0);
        var (source, sink) = PropertyBasedTests.PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var sourceAddr = AddressIdPool.StringOf(source);
        var sinkAddr = AddressIdPool.StringOf(sink);
        var request = new FlowRequest { Source = sourceAddr, Sink = sinkAddr };
        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        MaxFlowResponse result;
        try
        {
            result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);
        }
        catch (InvalidOperationException)
        {
            return; // No valid path to mutate
        }

        if (result.Transfers == null || result.Transfers.Count == 0)
            return;

        var state = new CapacityGraphContractState(graph);

        // Verify original passes
        var originalValidation = HubContractValidator.Validate(result.Transfers, sourceAddr, sinkAddr, state);
        if (!originalValidation.IsValid)
            return; // Original already invalid (e.g., consent filtering) — can't test mutation

        // Try multiple mutation strategies — some mutations are no-ops for certain graph topologies
        // (e.g., swapping edges in a flat 2-edge path with no groups is still valid).
        // We need at least ONE mutation out of several to be detected.
        bool anyDetected = false;
        string lastDescription = "";

        for (int attempt = 0; attempt < 6 && !anyDetected; attempt++)
        {
            var mutationRng = new Random(rng.Next() + attempt);
            var (mutated, description) = PathMutationHelper.ApplyRandomMutation(result.Transfers, state, mutationRng);
            lastDescription = description;

            var mutatedValidation = HubContractValidator.Validate(mutated, sourceAddr, sinkAddr, state);
            if (mutatedValidation.Violations.Count > 0)
                anyDetected = true;
        }

        Assert.That(anyDetected, Is.True,
            $"Validator failed to detect any of 6 mutation attempts (last: {lastDescription})\n" +
            $"Graph: {avatars} avatars, {groups} groups\n" +
            $"Steps: {result.Transfers.Count}");
    }

    #endregion
}
