using Circles.Common.Dto;
using Circles.Pathfinder;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Enforcement semantics for the <c>toTokens</c> delivery constraint in regular
/// (non-quantized) invitation flow (source ≠ sink).
///
/// Regression guard for a real defect: when <c>toTokens</c> is specified but the sink can
/// receive none of the requested tokens, <see cref="GraphFactory"/> narrowed the filter to
/// empty and then treated an empty filter as "no constraint" — silently returning a path that
/// delivers some OTHER (unrequested) token instead of the correct empty result. For a
/// "pay only in this group's token" request to a recipient who can't receive it, that produced
/// a path delivering personal CRC — exactly the kind of path that can revert on-chain.
///
/// The intended semantics distinguish two cases:
///   • no <c>toTokens</c> given            → unconstrained (any token may be delivered)
///   • <c>toTokens</c> given but unsatisfiable → NO path (the constraint cannot be met)
///
/// Both triggers of the empty-filter degrade are covered: a token the sink doesn't trust
/// (this fixture) and a token that doesn't resolve at all (handled by the same guard, since
/// "explicit" is read from the raw request, not from the resolved set).
/// </summary>
[TestFixture, Parallelizable]
public class ToTokensEnforcementTests
{
    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");
    private static readonly UInt256 OneCrc = UInt256.Parse("1000000000000000000");

    // tokenA is deliverable to the sink (sink trusts it, source holds it);
    // tokenB is NOT deliverable (sink does not trust it).
    private static readonly int Source = Node(0x7701);
    private static readonly int Sink = Node(0x7702);
    private static readonly int TokenA = Node(0x7710);
    private static readonly int TokenB = Node(0x7711);

    private static GraphFactory BuildFactory()
    {
        var mock = new MockLoadGraph();
        mock.AddBalance(Source, TokenA, 200_000_000L); // source holds tokenA
        mock.AddTrust(Sink, TokenA);                   // sink trusts tokenA -> deliverable
        mock.AddTrust(Source, Sink);                   // routing trust (mirrors existing toTokens tests)
        // (sink deliberately does NOT trust TokenB)
        return new GraphFactory("0x0000000000000000000000000000000000000000", mock);
    }

    private static int SolveStepCount(GraphFactory factory, FlowRequest request)
    {
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        var response = new V2Pathfinder().ComputeMaxFlowWithPath(capacityGraph, request, OneCrc);
        return response.Transfers?.Count ?? 0;
    }

    [Test]
    public void NonQuantized_NoToTokens_DeliversPath()
    {
        // Control: with no delivery constraint the sink can receive tokenA, so a path must exist.
        // This proves the "no path" result below is caused by the constraint, not a dead graph.
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(Source),
            Sink = AddressIdPool.StringOf(Sink),
            QuantizedMode = false,
        };

        Assert.That(SolveStepCount(BuildFactory(), request), Is.GreaterThan(0),
            "control: with no toTokens, the sink can receive tokenA, so a path must exist");
    }

    [Test]
    public void NonQuantized_UnsatisfiableToTokens_NoPath()
    {
        // toTokens=[tokenB] is unsatisfiable: the sink trusts tokenA but not tokenB, and a score
        // group token (the real-world analogue) is sink-only — there is no way to deliver tokenB.
        // The correct result is NO path, NOT a silent fallback to delivering tokenA.
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(Source),
            Sink = AddressIdPool.StringOf(Sink),
            QuantizedMode = false,
            ToTokens = new List<string> { AddressIdPool.StringOf(TokenB) },
        };

        Assert.That(SolveStepCount(BuildFactory(), request), Is.EqualTo(0),
            "toTokens specified but unsatisfiable (sink cannot receive tokenB) must yield NO path, " +
            "not a path delivering the unrequested tokenA");
    }

    [Test]
    public void NonQuantized_UnresolvableToToken_NoPath()
    {
        // toTokens references a token that does not exist in the graph at all (e.g. a token not yet
        // minted at this block). It resolves to nothing — but the request was explicit, so the result
        // must be NO path, not an unconstrained fallback. This is the second trigger of the same fix:
        // "explicit" is read from the raw request, so it stays true even when nothing resolves.
        const string unresolvable = "0x00000000000000000000000000000000deadbeef"; // never added to the graph
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(Source),
            Sink = AddressIdPool.StringOf(Sink),
            QuantizedMode = false,
            ToTokens = new List<string> { unresolvable },
        };

        Assert.That(SolveStepCount(BuildFactory(), request), Is.EqualTo(0),
            "an explicit toTokens that resolves to nothing (token absent at this block) must yield NO path");
    }

    [Test]
    public void NonQuantized_PartiallySatisfiableToTokens_DeliversTrustedSubset()
    {
        // toTokens = [tokenA (sink trusts -> deliverable), tokenB (sink doesn't trust)].
        // The satisfiable subset survives the narrowing, so the unsatisfiable sentinel must NOT fire:
        // a path delivering tokenA is still found. Guards against the fix over-firing.
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(Source),
            Sink = AddressIdPool.StringOf(Sink),
            QuantizedMode = false,
            ToTokens = new List<string> { AddressIdPool.StringOf(TokenA), AddressIdPool.StringOf(TokenB) },
        };

        Assert.That(SolveStepCount(BuildFactory(), request), Is.GreaterThan(0),
            "a satisfiable subset (sink trusts tokenA) must still yield a path; the sentinel must not fire");
    }
}
