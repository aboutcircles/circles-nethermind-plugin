using System.Threading;
using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Circles.Pathfinder.Validation;
using Nethermind.Int256;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Fuzz differential tests that scale pathfinder-validator agreement testing to ~250 iterations.
/// Strategy A: Validator vs Pathfinder (no Anvil needed) — purely synthetic graphs.
///
/// Covers:
///   - Small/medium/large graph sizes with varying density and consent
///   - Quantized mode with groups
///   - Consented flow with validation mode
///   - Mutation detection (validator catches corruption)
///   - Quantization + group minting intersection (FlowConservation)
///   - Consent + groups cross-contamination (Rules 4, 6, 9, 10, 11)
/// </summary>
[TestFixture, Parallelizable]
[Category("Fuzz")]
public class FuzzDifferentialTests
{
    private const long DefaultBalance = 1_000_000L;
    private const long HighBalance = 100_000_000L;

    #region Fuzz_SmallGraphs_ValidatorAgreesWithPathfinder

    /// <summary>
    /// Small graphs: 3-8 avatars, 0-2 groups, density 0.1-0.6, consent on/off.
    /// Validates that pathfinder output always passes the HubContractValidator.
    /// </summary>
    [Test, Repeat(50)]
    public void Fuzz_SmallGraphs_ValidatorAgreesWithPathfinder()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 100_000);
        int avatars = rng.Next(3, 9);
        int groups = rng.Next(0, 3);
        double density = rng.NextDouble() * 0.5 + 0.1; // 0.1-0.6
        bool consent = rng.NextDouble() > 0.5;

        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, groups, DefaultBalance, rng,
            trustDensity: density, withRouter: groups > 0, withConsent: consent, consentRate: 0.5);
        var (source, sink) = PropertyBasedTests.PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
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
            return; // Empty graph — no path to validate
        }

        if (result.Transfers == null || result.Transfers.Count == 0)
            return; // No flow found — nothing to validate

        var state = new CapacityGraphContractState(graph);
        var validation = HubContractValidator.Validate(result.Transfers, sourceAddr, sinkAddr, state);

        AssertNoErrors(validation, avatars, groups, density, consent);
    }

    #endregion

    #region Fuzz_MediumGraphs_ValidatorAgreesWithPathfinder

    /// <summary>
    /// Medium graphs: 8-15 avatars, 1-3 groups, density 0.2-0.7, consent on/off.
    /// </summary>
    [Test, Repeat(40)]
    public void Fuzz_MediumGraphs_ValidatorAgreesWithPathfinder()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 200_000);
        int avatars = rng.Next(8, 16);
        int groups = rng.Next(1, 4);
        double density = rng.NextDouble() * 0.5 + 0.2; // 0.2-0.7
        bool consent = rng.NextDouble() > 0.5;

        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, groups, DefaultBalance, rng,
            trustDensity: density, withRouter: groups > 0, withConsent: consent, consentRate: 0.5);
        var (source, sink) = PropertyBasedTests.PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
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
            return;
        }

        if (result.Transfers == null || result.Transfers.Count == 0)
            return;

        var state = new CapacityGraphContractState(graph);
        var validation = HubContractValidator.Validate(result.Transfers, sourceAddr, sinkAddr, state);

        AssertNoErrors(validation, avatars, groups, density, consent);
    }

    #endregion

    #region Fuzz_LargeGraphs_ValidatorAgreesWithPathfinder

    /// <summary>
    /// Large graphs: 15-25 avatars, 2-4 groups, density 0.3-0.8.
    /// Tests scalability of both pathfinder and validator.
    /// </summary>
    [Test, Repeat(20)]
    public void Fuzz_LargeGraphs_ValidatorAgreesWithPathfinder()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 300_000);
        int avatars = rng.Next(15, 26);
        int groups = rng.Next(2, 5);
        double density = rng.NextDouble() * 0.5 + 0.3; // 0.3-0.8
        bool consent = rng.NextDouble() > 0.5;

        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, groups, DefaultBalance, rng,
            trustDensity: density, withRouter: true, withConsent: consent, consentRate: 0.5);
        var (source, sink) = PropertyBasedTests.PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
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
            return;
        }

        if (result.Transfers == null || result.Transfers.Count == 0)
            return;

        var state = new CapacityGraphContractState(graph);
        var validation = HubContractValidator.Validate(result.Transfers, sourceAddr, sinkAddr, state);

        AssertNoErrors(validation, avatars, groups, density, consent);
    }

    #endregion

    #region Fuzz_QuantizedMode_ValidatorAgreesWithPathfinder

    /// <summary>
    /// Quantized mode: 4-12 avatars, 1-3 groups, quantized=true.
    /// Validates that quantized output passes all validator rules.
    /// </summary>
    [Test, Repeat(30)]
    public void Fuzz_QuantizedMode_ValidatorAgreesWithPathfinder()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 400_000);
        int avatars = rng.Next(4, 13);
        int groups = rng.Next(1, 4);
        double density = rng.NextDouble() * 0.4 + 0.3; // 0.3-0.7

        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, groups, HighBalance, rng,
            trustDensity: density, withRouter: groups > 0);
        var (source, sink) = PropertyBasedTests.PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var sourceAddr = AddressIdPool.StringOf(source);
        var sinkAddr = AddressIdPool.StringOf(sink);
        var request = new FlowRequest
        {
            Source = sourceAddr,
            Sink = sinkAddr,
            QuantizedMode = true,
        };
        UInt256 target = CirclesConverter.BlowUpToUInt256(HighBalance);

        MaxFlowResponse result;
        try
        {
            result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("BAD_INPUT"))
        {
            return;
        }

        if (result.Transfers == null || result.Transfers.Count == 0)
            return;

        var state = new CapacityGraphContractState(graph);
        var validation = HubContractValidator.Validate(result.Transfers, sourceAddr, sinkAddr, state);

        var errors = validation.Violations
            .Where(v => v.Severity == "error")
            .Select(v => $"  [{v.Rule}] {v.Message}")
            .ToList();

        Assert.That(errors, Is.Empty,
            $"Quantized mode validator errors!\n" +
            $"Graph: {avatars} avatars, {groups} groups, density={density:F2}\n" +
            $"Errors:\n{string.Join("\n", errors)}");
    }

    #endregion

    #region Fuzz_ConsentedFlow_ValidatorAgreesWithPathfinder

    /// <summary>
    /// Consented flow: 4-10 avatars, 0 groups, consent enabled, consent rate 0.2-0.8.
    /// Uses DisableConsentedFlow=false to exercise full consent validation path.
    /// </summary>
    [Test, Repeat(30)]
    public void Fuzz_ConsentedFlow_ValidatorAgreesWithPathfinder()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 500_000);
        int avatars = rng.Next(4, 11);
        double density = rng.NextDouble() * 0.4 + 0.2; // 0.2-0.6
        double consentRate = rng.NextDouble() * 0.6 + 0.2; // 0.2-0.8

        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, 0, DefaultBalance, rng,
            trustDensity: density, withRouter: false, withConsent: true, consentRate: consentRate);
        var (source, sink) = PropertyBasedTests.PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
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
            $"Consented flow violations (avatars={avatars}, consentRate={consentRate:F2}):\n" +
            string.Join("\n", permissionViolations.Select(v => $"  [{v.Rule}] {v.Message}")));
    }

    #endregion

    #region Fuzz_ConsentGroupRouter_BothModesValid

    // Skip-rate guardrail: track BAD_INPUT skips across iterations to catch a degenerate
    // graph generator that produces unsolvable graphs every time.
    private static int _consentFuzzSkipCount;
    private const int ConsentFuzzMaxSkips = 20; // out of 30 iterations

    /// <summary>
    /// Triple combo: consent + groups + router. Forces all three features active.
    /// Runs BOTH modes (exclusion and validation) against the SAME graph instance.
    ///
    /// Validation mode (the prod target) MUST produce validator-clean output — every
    /// path Hub.sol's isPermittedFlow would reject is filtered before output. Any
    /// validator error here is a real regression that would revert on-chain.
    ///
    /// Exclusion mode (the prod fallback) is checked for FlowConservation only. It is
    /// known to admit consented_source → non_consented_recipient direct transfers
    /// (its rule is "no consented intermediaries", not "no consent violations"), so
    /// IsPermittedFlow violations there are a known limitation, not a regression.
    ///
    /// Building one graph for both modes is required: the consent fix is mode-agnostic
    /// at the graph level; only the Settings differ. Building twice and trying to share
    /// addresses fails because RNG state drifts between the two `BuildSyntheticGraph`
    /// calls — different prefixes → different addresses → source/sink from graph A do
    /// not exist in graph B → ResolveAndGuard returns empty response, silently no-op-ing
    /// the assertion. Caught by code review on 2026-04-28.
    /// </summary>
    [Test, Repeat(30)]
    public void Fuzz_ConsentGroupRouter_BothModesValid()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 700_000);
        int avatars = rng.Next(5, 12);
        int groups = rng.Next(1, 4);
        double density = rng.NextDouble() * 0.4 + 0.2;
        double consentRate = rng.NextDouble() * 0.5 + 0.2;

        // Build the graph ONCE — both modes share it. CapacityGraph is mode-agnostic.
        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, groups, DefaultBalance, rng,
            trustDensity: density, withRouter: true, withConsent: true, consentRate: consentRate);
        var (srcId, snkId) = PropertyBasedTests.PickSourceSink(graph, rng);
        var srcAddr = AddressIdPool.StringOf(srcId);
        var snkAddr = AddressIdPool.StringOf(snkId);
        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        var pfExcl = new V2Pathfinder(settings: new Settings { ExcludeConsentedIntermediaries = true });
        var pfVal = new V2Pathfinder(settings: new Settings { ExcludeConsentedIntermediaries = false });

        // Compute each mode independently so a one-sided BAD_INPUT can't mask a
        // mode-specific regression. The skip budget is only consumed when *both*
        // modes reject the same graph for the same reason (degenerate generator).
        bool exclBadInput = false, valBadInput = false;
        MaxFlowResponse? resultExcl = null, resultVal = null;

        try
        {
            resultExcl = pfExcl.ComputeMaxFlowWithPath(graph, new FlowRequest { Source = srcAddr, Sink = snkAddr }, target);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("BAD_INPUT"))
        {
            exclBadInput = true;
        }

        try
        {
            resultVal = pfVal.ComputeMaxFlowWithPath(graph, new FlowRequest { Source = srcAddr, Sink = snkAddr }, target);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("BAD_INPUT"))
        {
            valBadInput = true;
        }

        if (exclBadInput && valBadInput)
        {
            int skips = Interlocked.Increment(ref _consentFuzzSkipCount);
            Assert.That(skips, Is.LessThanOrEqualTo(ConsentFuzzMaxSkips),
                $"Consent fuzz skipped {skips} iterations with BAD_INPUT — graph generator is producing degenerate graphs");
            return;
        }

        Assert.That(exclBadInput, Is.False,
            "Exclusion mode threw BAD_INPUT while validation mode did not — mode-specific regression");
        Assert.That(valBadInput, Is.False,
            "Validation mode threw BAD_INPUT while exclusion mode did not — mode-specific regression");

        var state = new CapacityGraphContractState(graph);

        // Validation mode (PROD TARGET): must be validator-clean. No exceptions —
        // any violation is a regression that would revert on-chain.
        if (resultVal.Transfers.Count > 0)
        {
            var valVal = HubContractValidator.Validate(resultVal.Transfers, srcAddr, snkAddr, state);
            var errors = valVal.Violations
                .Where(v => v.Severity == "error")
                .Select(v => $"  [{v.Rule}] {v.Message}")
                .ToList();
            Assert.That(errors, Is.Empty,
                $"Validation mode errors (avatars={avatars}, groups={groups}, consent={consentRate:F2}):\n" +
                string.Join("\n", errors));
        }

        // Exclusion mode: only FlowConservation must hold. IsPermittedFlow violations
        // here are a known limitation (consented_source → non_consented_recipient is
        // allowed because the rule is "no consented intermediaries", not full consent
        // validation). Tightening exclusion mode is tracked separately — for now, this
        // weaker assertion guards against new classes of bug (Conservation, Ordering,
        // SelfTransfer, DuplicateEdge, etc.) without producing false positives.
        if (resultExcl.Transfers.Count > 0)
        {
            var valExcl = HubContractValidator.Validate(resultExcl.Transfers, srcAddr, snkAddr, state);
            var conservationErrors = valExcl.Violations
                .Where(v => v.Rule == "FlowConservation" && v.Severity == "error")
                .ToList();
            Assert.That(conservationErrors, Is.Empty,
                $"Exclusion mode FlowConservation errors:\n" +
                string.Join("\n", conservationErrors.Select(v => $"  [{v.Rule}] {v.Message}")));
        }
    }

    #endregion

    #region Fuzz_MutatedPaths_ValidatorDetectsCorruption

    /// <summary>
    /// Generate valid paths, apply 2-3 random mutations, verify at least one is detected.
    /// Uses up to 8 mutation attempts before failing — covers a wide range of mutation strategies.
    /// </summary>
    [Test, Repeat(40)]
    public void Fuzz_MutatedPaths_ValidatorDetectsCorruption()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 600_000);
        int avatars = rng.Next(4, 12);
        int groups = rng.Next(0, 3);
        double density = rng.NextDouble() * 0.4 + 0.2; // 0.2-0.6

        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, groups, DefaultBalance, rng,
            trustDensity: density, withRouter: groups > 0);
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

        // Verify original passes first
        var originalValidation = HubContractValidator.Validate(result.Transfers, sourceAddr, sinkAddr, state);
        if (!originalValidation.IsValid)
            return; // Original already invalid — can't meaningfully test mutation

        // Try up to 8 mutation attempts — each applies 2-3 random mutations
        bool anyDetected = false;
        string lastDescription = "";

        for (int attempt = 0; attempt < 8 && !anyDetected; attempt++)
        {
            var mutationRng = new Random(rng.Next() + attempt);
            int mutationCount = mutationRng.Next(2, 4); // 2-3 mutations

            var mutated = result.Transfers;
            var descriptions = new List<string>();

            for (int m = 0; m < mutationCount; m++)
            {
                var (newMutated, desc) = PathMutationHelper.ApplyRandomMutation(mutated, state, mutationRng);
                mutated = newMutated;
                descriptions.Add(desc);
            }

            lastDescription = string.Join(" + ", descriptions);

            var mutatedValidation = HubContractValidator.Validate(mutated, sourceAddr, sinkAddr, state);
            if (mutatedValidation.Violations.Count > 0)
                anyDetected = true;
        }

        Assert.That(anyDetected, Is.True,
            $"Validator failed to detect any of 8 multi-mutation attempts (last: {lastDescription})\n" +
            $"Graph: {avatars} avatars, {groups} groups, density={density:F2}\n" +
            $"Steps: {result.Transfers.Count}");
    }

    #endregion

    #region Fuzz_QuantizedWithGroups_NoConservationViolation

    /// <summary>
    /// Quantization + group minting intersection: specifically checks FlowConservation.
    /// The backward propagation of quantization adjustments through group minting edges
    /// is a known fragile area — this fuzzes that code path extensively.
    ///
    /// Previously, PropagateQuantizationBackwards could process a group vertex before all
    /// its downstream successors had been adjusted (BFS ordering bug), causing stale
    /// collateral scaling and NettedFlowMismatch. Fixed by adding convergence passes
    /// after the initial BFS — vertices are re-checked until totalIn == totalOut everywhere.
    /// </summary>
    [Test, Repeat(20)]
    public void Fuzz_QuantizedWithGroups_NoConservationViolation()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 700_000);
        int avatars = rng.Next(5, 15);
        int groups = rng.Next(1, 4);
        double density = rng.NextDouble() * 0.4 + 0.3; // 0.3-0.7

        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, groups, HighBalance, rng,
            trustDensity: density, withRouter: true);
        var (source, sink) = PropertyBasedTests.PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var sourceAddr = AddressIdPool.StringOf(source);
        var sinkAddr = AddressIdPool.StringOf(sink);
        var request = new FlowRequest
        {
            Source = sourceAddr,
            Sink = sinkAddr,
            QuantizedMode = true,
        };
        UInt256 target = CirclesConverter.BlowUpToUInt256(HighBalance);

        MaxFlowResponse result;
        try
        {
            result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("BAD_INPUT"))
        {
            return;
        }

        if (result.Transfers == null || result.Transfers.Count == 0)
            return;

        var state = new CapacityGraphContractState(graph);
        var validation = HubContractValidator.Validate(result.Transfers, sourceAddr, sinkAddr, state);

        // Specifically check FlowConservation — the rule most likely to break
        // when quantization adjustments don't propagate correctly through group edges.
        // Known issue: backward propagation bug causes NettedFlowMismatch at group vertices.
        var conservationViolations = validation.Violations
            .Where(v => v.Rule == "FlowConservation" && v.Severity == "error")
            .ToList();

        Assert.That(conservationViolations, Is.Empty,
            $"FlowConservation violated in quantized+groups mode\n" +
            $"Graph: {avatars} avatars, {groups} groups, density={density:F2}\n" +
            $"Violations:\n{string.Join("\n", conservationViolations.Select(v => $"  {v.Message}"))}");

        // Check for NON-conservation errors that would indicate a new bug
        var otherErrors = validation.Violations
            .Where(v => v.Rule != "FlowConservation" && v.Severity == "error")
            .Select(v => $"  [{v.Rule}] {v.Message}")
            .ToList();

        Assert.That(otherErrors, Is.Empty,
            $"Non-conservation errors in quantized+groups mode!\n" +
            $"Graph: {avatars} avatars, {groups} groups, density={density:F2}\n" +
            $"Errors:\n{string.Join("\n", otherErrors)}");

        // Also check no self-loops leaked into the DTO (regression for Bug #3)
        foreach (var step in result.Transfers)
        {
            Assert.That(step.From!.ToLowerInvariant(), Is.Not.EqualTo(step.To!.ToLowerInvariant()),
                $"Self-loop in quantized+groups transfer DTO: {step.From} -> {step.To}");
        }
    }

    #endregion

    #region Fuzz_ConsentWithGroups_NoCrossContamination

    /// <summary>
    /// Consent + groups together: exercises Rules 4 (IsPermittedFlow), 6 (CollateralBeforeMint),
    /// 9 (AvatarRegistration), 10 (GroupRegistration), and 11 (TokenIdValidity) simultaneously.
    /// This is the most complex interaction mode — consent filtering must not break group
    /// minting ordering or introduce unregistered vertices.
    /// </summary>
    [Test, Repeat(20)]
    public void Fuzz_ConsentWithGroups_NoCrossContamination()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 800_000);
        int avatars = rng.Next(5, 12);
        int groups = rng.Next(1, 3);
        double density = rng.NextDouble() * 0.4 + 0.3; // 0.3-0.7
        double consentRate = rng.NextDouble() * 0.4 + 0.2; // 0.2-0.6

        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, groups, DefaultBalance, rng,
            trustDensity: density, withRouter: true, withConsent: true, consentRate: consentRate);
        var (source, sink) = PropertyBasedTests.PickSourceSink(graph, rng);

        // Use validation mode (not intermediary exclusion) to exercise full consent path
        var pathfinder = new V2Pathfinder(settings: new Settings { DisableConsentedFlow = false });
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
            return;
        }

        if (result.Transfers == null || result.Transfers.Count == 0)
            return;

        var state = new CapacityGraphContractState(graph);
        var validation = HubContractValidator.Validate(result.Transfers, sourceAddr, sinkAddr, state);

        // Check ALL error-severity violations — consent+groups must not introduce any rule break
        var errors = validation.Violations
            .Where(v => v.Severity == "error")
            .Select(v => $"  [{v.Rule}] {v.Message}")
            .ToList();

        Assert.That(errors, Is.Empty,
            $"Consent+groups cross-contamination detected!\n" +
            $"Graph: {avatars} avatars, {groups} groups, density={density:F2}, consentRate={consentRate:F2}\n" +
            $"Errors:\n{string.Join("\n", errors)}");
    }

    #endregion

    #region Shared Helpers

    private static void AssertNoErrors(
        ValidationResult validation,
        int avatars,
        int groups,
        double density,
        bool consent)
    {
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
}
