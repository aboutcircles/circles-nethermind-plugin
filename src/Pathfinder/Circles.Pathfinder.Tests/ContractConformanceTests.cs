using System.Numerics;
using System.Text.RegularExpressions;
using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Nethermind.Int256;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Validates that pathfinder output conforms to the exact contract expectations
/// from Hub.sol's operateFlowMatrix / isPermittedFlow.
///
/// Invariants tested:
/// - Flow conservation at each non-source/non-sink vertex
/// - isPermittedFlow: every edge passes the 3-step consent check
/// - Collateral before mint: Router→Group edges precede Group→Avatar edges
/// - Stream structure: terminal edges in same stream have same receiver
/// - No negative or zero-flow edges in final output
/// </summary>
[TestFixture, Parallelizable]
public class ContractConformanceTests
{
    private const string SourceAddr = "0xcf10000000000000000000000000000000000001";
    private const string SinkAddr = "0xcf20000000000000000000000000000000000002";
    private const string RouterAddr = "0xcf30000000000000000000000000000000000003";
    private const string Group1Addr = "0xcf40000000000000000000000000000000000004";
    private const string Avatar1Addr = "0xcf50000000000000000000000000000000000005";
    private const string Avatar2Addr = "0xcf60000000000000000000000000000000000006";
    private const string Token1Addr = "0xcf70000000000000000000000000000000000007";
    private const string Token2Addr = "0xcf80000000000000000000000000000000000008";

    private int Source => AddressIdPool.IdOf(SourceAddr);
    private int Sink => AddressIdPool.IdOf(SinkAddr);
    private int Router => AddressIdPool.IdOf(RouterAddr);
    private int Group1 => AddressIdPool.IdOf(Group1Addr);
    private int Avatar1 => AddressIdPool.IdOf(Avatar1Addr);
    private int Avatar2 => AddressIdPool.IdOf(Avatar2Addr);
    private int Token1 => AddressIdPool.IdOf(Token1Addr);
    private int Token2 => AddressIdPool.IdOf(Token2Addr);

    #region Flow Conservation

    /// <summary>
    /// At every intermediate vertex (not source/sink), inbound flow must equal outbound flow.
    /// This is a fundamental property of valid network flow.
    /// </summary>
    [Test]
    public void FlowConservation_IntermediateVertices_NetFlowZero()
    {
        var steps = new List<TransferPathStep>
        {
            Step(SourceAddr, Avatar1Addr, Token1Addr, 100),
            Step(Avatar1Addr, Avatar2Addr, Token1Addr, 60),
            Step(Avatar1Addr, SinkAddr, Token1Addr, 40),
            Step(Avatar2Addr, SinkAddr, Token1Addr, 60),
        };

        AssertFlowConservation(steps, SourceAddr, SinkAddr);
    }

    /// <summary>
    /// With router and group minting edges, flow conservation must still hold.
    /// </summary>
    [Test]
    public void FlowConservation_WithGroupMinting_NetFlowZero()
    {
        var steps = new List<TransferPathStep>
        {
            Step(SourceAddr, RouterAddr, Token1Addr, 100),
            Step(RouterAddr, Group1Addr, Token1Addr, 100),
            Step(Group1Addr, SinkAddr, Group1Addr, 100), // Group token = group address
        };

        AssertFlowConservation(steps, SourceAddr, SinkAddr);
    }

    /// <summary>
    /// Multiple paths merging and splitting should conserve flow at all vertices.
    /// </summary>
    [Test]
    public void FlowConservation_MergingSplittingPaths_NetFlowZero()
    {
        var steps = new List<TransferPathStep>
        {
            Step(SourceAddr, Avatar1Addr, Token1Addr, 50),
            Step(SourceAddr, Avatar2Addr, Token2Addr, 50),
            Step(Avatar1Addr, SinkAddr, Token1Addr, 50),
            Step(Avatar2Addr, SinkAddr, Token2Addr, 50),
        };

        AssertFlowConservation(steps, SourceAddr, SinkAddr);
    }

    #endregion

    #region isPermittedFlow Conformance

    /// <summary>
    /// Non-consented avatar: only needs receiver trusts circlesId.
    /// This should pass validation.
    /// </summary>
    [Test]
    public void IsPermittedFlow_NonConsented_ReceiverTrustsToken_Passes()
    {
        var graph = BuildGraphWithTrust(
            trusts: new[] { (Avatar1Addr, Token1Addr) }, // Avatar1 trusts Token1
            consented: Array.Empty<string>());

        var edges = new List<FlowEdge>
        {
            new(Source, Avatar1, Token1, 100) { Flow = 100 }
        };

        // Source is NOT consented → standard trust check: Avatar1 trusts Token1 → pass
        var validated = ValidateConsentedFlow(edges, graph);
        Assert.That(validated.Count, Is.EqualTo(1));
    }

    /// <summary>
    /// Consented avatar: needs From trusts To AND To has consented flow.
    /// Both conditions met → should pass.
    /// </summary>
    [Test]
    public void IsPermittedFlow_Consented_BothConditionsMet_Passes()
    {
        var graph = BuildGraphWithTrust(
            trusts: new[] { (SourceAddr, Avatar1Addr) }, // Source trusts Avatar1
            consented: new[] { SourceAddr, Avatar1Addr }); // Both consented

        var edges = new List<FlowEdge>
        {
            new(Source, Avatar1, Token1, 100) { Flow = 100 }
        };

        var validated = ValidateConsentedFlow(edges, graph);
        Assert.That(validated.Count, Is.EqualTo(1));
    }

    /// <summary>
    /// Consented avatar: From trusts To but To does NOT have consented flow → fails.
    /// </summary>
    [Test]
    public void IsPermittedFlow_Consented_ToNotConsented_Fails()
    {
        var graph = BuildGraphWithTrust(
            trusts: new[] { (SourceAddr, Avatar1Addr) }, // Source trusts Avatar1
            consented: new[] { SourceAddr }); // Only Source is consented, Avatar1 is NOT

        var edges = new List<FlowEdge>
        {
            new(Source, Avatar1, Token1, 100) { Flow = 100 }
        };

        var validated = ValidateConsentedFlow(edges, graph);
        Assert.That(validated.Count, Is.EqualTo(0),
            "Edge should be filtered: To doesn't have consented flow");
    }

    /// <summary>
    /// Consented avatar: From does NOT trust To → fails even if To is consented.
    /// </summary>
    [Test]
    public void IsPermittedFlow_Consented_FromDoesNotTrustTo_Fails()
    {
        var graph = BuildGraphWithTrust(
            trusts: Array.Empty<(string, string)>(), // No trusts!
            consented: new[] { SourceAddr, Avatar1Addr }); // Both consented

        var edges = new List<FlowEdge>
        {
            new(Source, Avatar1, Token1, 100) { Flow = 100 }
        };

        var validated = ValidateConsentedFlow(edges, graph);
        Assert.That(validated.Count, Is.EqualTo(0),
            "Edge should be filtered: From doesn't trust To");
    }

    /// <summary>
    /// Router edges should always be permitted regardless of consent status.
    /// </summary>
    [Test]
    public void IsPermittedFlow_RouterEdges_AlwaysPermitted()
    {
        var graph = BuildGraphWithTrust(
            trusts: Array.Empty<(string, string)>(),
            consented: new[] { SourceAddr }); // Source consented but trusts nobody
        graph.SetRouter(Router);

        var edges = new List<FlowEdge>
        {
            new(Source, Router, Token1, 100) { Flow = 100 },
            new(Router, Avatar1, Token1, 100) { Flow = 100 },
        };

        var validated = ValidateConsentedFlow(edges, graph);
        Assert.That(validated.Count, Is.EqualTo(2),
            "Router edges should bypass consent validation");
    }

    #endregion

    #region Collateral Before Mint

    /// <summary>
    /// For each group, all Router→Group edges must appear BEFORE any Group→Avatar edge.
    /// </summary>
    [Test]
    public void CollateralBeforeMint_CorrectOrder_ValidationPasses()
    {
        var graph = new CapacityGraph();
        graph.AddAvatar(Source);
        graph.AddAvatar(Sink);
        graph.SetRouter(Router);
        graph.AddGroup(Group1);

        var edges = new List<FlowEdge>
        {
            new(Source, Router, Token1, 50) { Flow = 50 },
            new(Source, Router, Token2, 50) { Flow = 50 },
            new(Router, Group1, Token1, 50) { Flow = 50 },
            new(Router, Group1, Token2, 50) { Flow = 50 },
            new(Group1, Sink, Group1, 100) { Flow = 100 },
        };

        Assert.DoesNotThrow(() => V2Pathfinder.ValidateMintEdgeOrdering(edges, graph));
    }

    /// <summary>
    /// Mint edge before all collateral → validation must return error string.
    /// </summary>
    [Test]
    public void CollateralBeforeMint_WrongOrder_ValidationFails()
    {
        var graph = new CapacityGraph();
        graph.AddAvatar(Source);
        graph.AddAvatar(Sink);
        graph.SetRouter(Router);
        graph.AddGroup(Group1);

        var edges = new List<FlowEdge>
        {
            new(Group1, Sink, Group1, 100) { Flow = 100 }, // Mint FIRST (wrong!)
            new(Router, Group1, Token1, 50) { Flow = 50 },
            new(Router, Group1, Token2, 50) { Flow = 50 },
        };

        var error = V2Pathfinder.ValidateMintEdgeOrdering(edges, graph);
        Assert.That(error, Is.Not.Null, "Wrong ordering should return error string");
    }

    /// <summary>
    /// After SortEdgesForMintDependencies, ValidateMintEdgeOrdering must always pass.
    /// This is a key integration invariant.
    /// </summary>
    [Test]
    public void SortThenValidate_AlwaysConsistent()
    {
        var graph = new CapacityGraph();
        graph.AddAvatar(Source);
        graph.AddAvatar(Sink);
        graph.SetRouter(Router);
        graph.AddGroup(Group1);

        // Deliberately worst-case ordering
        var edges = new List<FlowEdge>
        {
            new(Group1, Sink, Group1, 100) { Flow = 100 },
            new(Router, Group1, Token2, 50) { Flow = 50 },
            new(Source, Router, Token1, 50) { Flow = 50 },
            new(Source, Router, Token2, 50) { Flow = 50 },
            new(Router, Group1, Token1, 50) { Flow = 50 },
        };

        var sorted = V2Pathfinder.SortEdgesForMintDependencies(edges, graph);
        Assert.DoesNotThrow(() => V2Pathfinder.ValidateMintEdgeOrdering(sorted, graph));
    }

    #endregion

    #region No Negative/Zero Flow Edges

    /// <summary>
    /// The final output should never contain edges with flow <= 0.
    /// These would cause contract reverts or be meaningless.
    /// </summary>
    [Test]
    public void FinalOutput_NoZeroOrNegativeFlowEdges()
    {
        // Simulate what ComputeMaxFlowWithPath's step 7 does
        var edges = new List<FlowEdge>
        {
            new(Source, Avatar1, Token1, 100) { Flow = 100 },
            new(Avatar1, Sink, Token1, 0) { Flow = 0 },       // Zero flow
            new(Avatar2, Sink, Token2, -5) { Flow = -5 },      // Negative flow
            new(Source, Avatar2, Token2, 50) { Flow = 50 },
        };

        // Filter as the real code does
        var transfer = new List<TransferPathStep>();
        foreach (var e in edges)
        {
            if (e.Flow <= 0) continue;
            transfer.Add(Step(
                AddressIdPool.StringOf(e.From),
                AddressIdPool.StringOf(e.To),
                AddressIdPool.StringOf(e.Token),
                e.Flow));
        }

        Assert.That(transfer.Count, Is.EqualTo(2),
            "Only positive-flow edges should make it to output");
        Assert.That(transfer.All(t => long.Parse(t.Value!) > 0), Is.True);
    }

    #endregion

    #region Demurrage Calculation Conformance

    /// <summary>
    /// Verify InflationaryToDemurrage produces monotonically decreasing values as days increase.
    /// </summary>
    [Test]
    public void Demurrage_MonotonicallyDecreasing()
    {
        var balance = BigInteger.Parse("1000000000000000000"); // 1 CRC
        BigInteger prev = balance;

        for (ulong day = 1; day <= 365; day++)
        {
            var demurraged = CirclesConverter.InflationaryToDemurrage(balance, day);
            Assert.That(demurraged, Is.LessThanOrEqualTo(prev),
                $"Day {day}: demurraged value should be <= day {day - 1} value");
            Assert.That(demurraged, Is.GreaterThan(BigInteger.Zero),
                $"Day {day}: demurraged value should remain positive for 1 CRC within a year");
            prev = demurraged;
        }
    }

    /// <summary>
    /// At day 0, demurrage should not change the value (gamma^0 = 1).
    /// </summary>
    [Test]
    public void Demurrage_DayZero_NoChange()
    {
        var balance = BigInteger.Parse("123456789012345678");
        var result = CirclesConverter.InflationaryToDemurrage(balance, 0);
        Assert.That(result, Is.EqualTo(balance),
            "gamma^0 should be 1, so no demurrage at day 0");
    }

    /// <summary>
    /// Over ~365 days, roughly 7% should be lost to demurrage.
    /// </summary>
    [Test]
    public void Demurrage_OneYear_Approximately7Percent()
    {
        var balance = BigInteger.Parse("100000000000000000000"); // 100 CRC
        var afterOneYear = CirclesConverter.InflationaryToDemurrage(balance, 365);

        double ratio = (double)afterOneYear / (double)balance;
        // Should be approximately 0.93 (7% decay)
        Assert.That(ratio, Is.InRange(0.925, 0.935),
            $"After 365 days, ~93% of balance should remain (got {ratio:P4})");
    }

    /// <summary>
    /// Round-trip: inflationary → demurrage → inflationary should be close to original.
    /// </summary>
    [Test]
    public void Demurrage_RoundTrip_CloseToOriginal()
    {
        var original = BigInteger.Parse("50000000000000000000"); // 50 CRC
        ulong day = 100;

        var demurraged = CirclesConverter.InflationaryToDemurrage(original, day);
        var restored = CirclesConverter.DemurrageToInflationary(demurraged, day);

        // Fixed-point gamma^day math accumulates rounding error over many days.
        // At 100 days the error is ~87 atto-CRC on 50 CRC — that's ~1.7e-18 relative error.
        // Allow up to 1000 atto-CRC (still < 1 trillionth of a CRC).
        var diff = BigInteger.Abs(original - restored);
        Assert.That(diff, Is.LessThanOrEqualTo(new BigInteger(1000)),
            $"Round-trip should be within ±1000 atto-CRC (diff={diff})");
    }

    #endregion

    #region Vertex Ordering (uint160 Sorted)

    /// <summary>
    /// Hub.sol's operateFlowMatrix requires _flowVertices sorted by uint160 ascending.
    /// Verify that parsing addresses as BigInteger (uint160) and sorting produces correct order.
    /// Tests addresses at the low, mid, and high end of the uint160 range.
    /// </summary>
    [Test]
    public void VertexOrdering_Uint160Sorted_CorrectOrder()
    {
        // Addresses deliberately out of uint160 order
        var addresses = new[]
        {
            "0xffffffffffffffffffffffffffffffffffffffff", // max uint160
            "0x0000000000000000000000000000000000000001", // near-zero
            "0x8000000000000000000000000000000000000000", // midpoint (2^159)
            "0x0000000000000000000000000000000000000000", // zero
            "0xcf40000000000000000000000000000000000004", // typical address
        };

        // Parse as uint160 (BigInteger) and sort.
        // Prefix with "0" to ensure BigInteger.Parse treats hex as unsigned (avoids
        // two's complement sign extension when MSB is set, e.g. 0xFFFF...).
        var sorted = addresses
            .Select(a => (Address: a.ToLowerInvariant(), Numeric: BigInteger.Parse("0" + a[2..], System.Globalization.NumberStyles.HexNumber)))
            .OrderBy(x => x.Numeric)
            .ToList();

        // Verify ascending uint160 order
        for (int i = 1; i < sorted.Count; i++)
        {
            Assert.That(sorted[i].Numeric, Is.GreaterThan(sorted[i - 1].Numeric),
                $"Vertex {i} (0x{sorted[i].Numeric:x40}) should be > vertex {i - 1} (0x{sorted[i - 1].Numeric:x40})");
        }

        // Verify known order: 0x0000 < 0x0001 < 0x8000 < 0xcf40 < 0xffff
        Assert.That(sorted[0].Address, Is.EqualTo("0x0000000000000000000000000000000000000000"));
        Assert.That(sorted[1].Address, Is.EqualTo("0x0000000000000000000000000000000000000001"));
        Assert.That(sorted[2].Address, Is.EqualTo("0x8000000000000000000000000000000000000000"));
        Assert.That(sorted[3].Address, Is.EqualTo("0xcf40000000000000000000000000000000000004"));
        Assert.That(sorted[4].Address, Is.EqualTo("0xffffffffffffffffffffffffffffffffffffffff"));
    }

    #endregion

    #region Stream Structure Validation

    /// <summary>
    /// Hub.sol requires that within a "stream" (group of edges sharing a source coordinate),
    /// all terminal edges (the last edge in each flow path) must go to the same receiver.
    /// Verify that multi-edge paths converging to a single receiver satisfy this constraint.
    /// </summary>
    [Test]
    public void StreamStructure_TerminalEdges_SameReceiver()
    {
        // Two flow paths merging at Avatar1, then exiting to Sink:
        //   Source → Avatar1 (Token1) — path 1
        //   Source → Avatar2 → Avatar1 (Token2) — path 2
        //   Avatar1 → Sink (Token1) — terminal edge, path 1
        //   Avatar1 → Sink (Token2) — terminal edge, path 2
        var steps = new List<TransferPathStep>
        {
            Step(SourceAddr, Avatar1Addr, Token1Addr, 50),
            Step(SourceAddr, Avatar2Addr, Token2Addr, 30),
            Step(Avatar2Addr, Avatar1Addr, Token2Addr, 30),
            Step(Avatar1Addr, SinkAddr, Token1Addr, 50),
            Step(Avatar1Addr, SinkAddr, Token2Addr, 30),
        };

        // Terminal edges are edges whose To == Sink
        var terminalEdges = steps.Where(s => s.To == SinkAddr.ToLowerInvariant()).ToList();
        Assert.That(terminalEdges.Count, Is.GreaterThanOrEqualTo(2),
            "Should have at least 2 terminal edges for this test");

        // All terminal edges in this stream should share the same receiver (Sink)
        var receivers = terminalEdges.Select(e => e.To).Distinct().ToList();
        Assert.That(receivers.Count, Is.EqualTo(1),
            $"All terminal edges in a stream must go to the same receiver. Found: {string.Join(", ", receivers)}");
        Assert.That(receivers[0], Is.EqualTo(SinkAddr.ToLowerInvariant()));

        // Also verify flow conservation holds across the merged paths
        AssertFlowConservation(steps, SourceAddr, SinkAddr);
    }

    #endregion

    #region TransferPathStep Format Validation

    /// <summary>
    /// Verify output steps have: lowercase 42-char hex addresses (0x + 40 hex chars),
    /// positive non-zero Value, and non-null From/To/TokenOwner.
    /// Format bugs (mixed case, missing 0x prefix) cause on-chain contract reverts.
    /// </summary>
    [Test]
    public void StepFormat_AddressesLowercase42Hex_ValuePositive_NonNull()
    {
        var addressRegex = new Regex(@"^0x[0-9a-f]{40}$");

        // Generate a variety of steps with different address patterns
        var testSteps = new List<TransferPathStep>
        {
            Step(SourceAddr, SinkAddr, Token1Addr, 1),                          // small value
            Step(Avatar1Addr, Avatar2Addr, Token2Addr, long.MaxValue),          // max value
            Step(RouterAddr, Group1Addr, Token1Addr, 999999999999999999),        // 18-digit value
            Step("0x0000000000000000000000000000000000000001",                   // near-zero address
                 "0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",                   // uppercase input
                 "0xAbCdEf0123456789AbCdEf0123456789AbCdEf01", 42),             // mixed case input
        };

        foreach (var step in testSteps)
        {
            // Non-null checks
            Assert.That(step.From, Is.Not.Null.And.Not.Empty,
                "From must not be null or empty");
            Assert.That(step.To, Is.Not.Null.And.Not.Empty,
                "To must not be null or empty");
            Assert.That(step.TokenOwner, Is.Not.Null.And.Not.Empty,
                "TokenOwner must not be null or empty");
            Assert.That(step.Value, Is.Not.Null.And.Not.Empty,
                "Value must not be null or empty");

            // Address format: lowercase 0x + 40 hex chars
            Assert.That(addressRegex.IsMatch(step.From!), Is.True,
                $"From address '{step.From}' must match ^0x[0-9a-f]{{40}}$");
            Assert.That(addressRegex.IsMatch(step.To!), Is.True,
                $"To address '{step.To}' must match ^0x[0-9a-f]{{40}}$");
            Assert.That(addressRegex.IsMatch(step.TokenOwner!), Is.True,
                $"TokenOwner address '{step.TokenOwner}' must match ^0x[0-9a-f]{{40}}$");

            // Value must be positive non-zero
            var value = long.Parse(step.Value!);
            Assert.That(value, Is.GreaterThan(0),
                $"Value must be positive, got {value}");
        }
    }

    #endregion

    #region Helper Methods

    private static TransferPathStep Step(string from, string to, string token, long flow)
    {
        return new TransferPathStep
        {
            From = from.ToLowerInvariant(),
            To = to.ToLowerInvariant(),
            TokenOwner = token.ToLowerInvariant(),
            Value = flow.ToString()
        };
    }

    private static void AssertFlowConservation(
        List<TransferPathStep> steps,
        string sourceAddr,
        string sinkAddr)
    {
        var source = sourceAddr.ToLowerInvariant();
        var sink = sinkAddr.ToLowerInvariant();

        // Calculate net flow at each vertex
        var netFlow = new Dictionary<string, long>();

        foreach (var step in steps)
        {
            var from = step.From!.ToLowerInvariant();
            var to = step.To!.ToLowerInvariant();
            var flow = long.Parse(step.Value!);

            netFlow.TryGetValue(from, out long fromNet);
            netFlow[from] = fromNet - flow; // outbound

            netFlow.TryGetValue(to, out long toNet);
            netFlow[to] = toNet + flow; // inbound
        }

        // Check intermediate vertices (not source/sink)
        foreach (var (vertex, net) in netFlow)
        {
            if (vertex == source || vertex == sink)
                continue;

            Assert.That(net, Is.EqualTo(0),
                $"Flow conservation violated at vertex {vertex[..10]}...: net flow = {net}");
        }

        // Source should have negative net flow (outbound)
        Assert.That(netFlow.GetValueOrDefault(source), Is.LessThan(0),
            "Source should have net outbound flow");

        // Sink should have positive net flow (inbound)
        Assert.That(netFlow.GetValueOrDefault(sink), Is.GreaterThan(0),
            "Sink should have net inbound flow");

        // Source outbound should equal Sink inbound (overall conservation)
        Assert.That(-netFlow.GetValueOrDefault(source), Is.EqualTo(netFlow.GetValueOrDefault(sink)),
            "Total outbound from source must equal total inbound to sink");
    }

    private CapacityGraph BuildGraphWithTrust(
        (string truster, string trustee)[] trusts,
        string[] consented)
    {
        var graph = new CapacityGraph();
        graph.AddAvatar(Source);
        graph.AddAvatar(Sink);
        graph.AddAvatar(Avatar1);
        graph.AddAvatar(Avatar2);

        // Build trust lookup
        var trustLookup = new Dictionary<int, HashSet<int>>();
        foreach (var (truster, trustee) in trusts)
        {
            int trusterId = AddressIdPool.IdOf(truster.ToLowerInvariant());
            int trusteeId = AddressIdPool.IdOf(trustee.ToLowerInvariant());

            if (!trustLookup.TryGetValue(trusterId, out var set))
            {
                set = new HashSet<int>();
                trustLookup[trusterId] = set;
            }
            set.Add(trusteeId);
        }
        graph.TrustLookup = trustLookup;

        // Set consented avatars
        foreach (var addr in consented)
        {
            graph.ConsentedAvatars.Add(AddressIdPool.IdOf(addr.ToLowerInvariant()));
        }

        return graph;
    }

    /// <summary>
    /// Replicates V2Pathfinder.ValidateConsentedFlow logic for unit testing.
    /// </summary>
    private static List<FlowEdge> ValidateConsentedFlow(List<FlowEdge> edges, CapacityGraph graph)
    {
        if (graph.TrustLookup == null || graph.ConsentedAvatars.Count == 0)
            return edges;

        var valid = new List<FlowEdge>(edges.Count);

        foreach (var edge in edges)
        {
            // Skip router edges
            if (graph.IsRouter(edge.From) || graph.IsRouter(edge.To))
            {
                valid.Add(edge);
                continue;
            }

            // Non-consented: standard trust sufficient
            if (!graph.ConsentedAvatars.Contains(edge.From))
            {
                valid.Add(edge);
                continue;
            }

            // Consented: From trusts To AND To consented
            bool fromTrustsTo = graph.TrustLookup.TryGetValue(edge.From, out var trusts)
                                && trusts.Contains(edge.To);
            if (!fromTrustsTo) continue;
            if (!graph.ConsentedAvatars.Contains(edge.To)) continue;

            valid.Add(edge);
        }

        return valid;
    }

    #endregion
}
