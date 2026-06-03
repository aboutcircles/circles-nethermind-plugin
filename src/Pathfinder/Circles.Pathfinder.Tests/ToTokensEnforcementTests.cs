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
    // Addresses use a distinctive high lowercase-hex prefix (0xee…) so the four nodes can't
    // collide with low/zero-padded fixtures registered elsewhere in the process-global static
    // AddressIdPool. The low 16 bits stay distinct per node.
    private static int Node(int i) => AddressIdPool.IdOf($"0xee{0:D34}{i:x4}");
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

        // Register all participating avatars so LoadRegisteredAvatars() is non-empty, mirroring
        // production and silencing GraphFactory's "RegisteredAvatarIds is empty" fail-closed LogError.
        mock.AddRegisteredAvatar(AddressIdPool.StringOf(Source));
        mock.AddRegisteredAvatar(AddressIdPool.StringOf(Sink));
        mock.AddRegisteredAvatar(AddressIdPool.StringOf(TokenA));
        mock.AddRegisteredAvatar(AddressIdPool.StringOf(TokenB));

        return new GraphFactory("0x0000000000000000000000000000000000000000", mock);
    }

    private static MaxFlowResponse Solve(GraphFactory factory, FlowRequest request)
    {
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        return new V2Pathfinder().ComputeMaxFlowWithPath(capacityGraph, request, OneCrc);
    }

    private static int SolveStepCount(GraphFactory factory, FlowRequest request)
        => Solve(factory, request).Transfers?.Count ?? 0;

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

        var response = Solve(BuildFactory(), request);
        var transfers = response.Transfers ?? new List<TransferPathStep>();

        Assert.That(transfers.Count, Is.GreaterThan(0),
            "a satisfiable subset (sink trusts tokenA) must still yield a path; the sentinel must not fire");

        // The precise failure mode the fix addresses: delivering the WRONG token. The final transfer
        // into the sink must carry tokenA (the only deliverable requested token), never tokenB.
        var sinkAddr = AddressIdPool.StringOf(Sink);
        var tokenAAddr = AddressIdPool.StringOf(TokenA);
        var tokenBAddr = AddressIdPool.StringOf(TokenB);
        var deliveryToSink = transfers.Where(t => t.To == sinkAddr).ToList();

        Assert.That(deliveryToSink, Is.Not.Empty, "expected at least one transfer delivered to the sink");
        Assert.That(deliveryToSink.All(t => t.TokenOwner == tokenAAddr), Is.True,
            "the delivered token must be tokenA (the trusted subset), not the untrusted tokenB");
        Assert.That(transfers.Any(t => t.TokenOwner == tokenBAddr), Is.False,
            "tokenB (untrusted by the sink) must never appear in the delivered path");
    }

    [Test]
    public void Quantized_UnsatisfiableToTokens_NoPath()
    {
        // Same unsatisfiable request as NonQuantized_UnsatisfiableToTokens_NoPath but with
        // QuantizedMode = true. This pins the ordering of the STEP 2a1 sentinel (unsatisfiable →
        // inject -1) BEFORE STEP 2a2 (quantized auto-discovery of tokens). If 2a2 ran first / over
        // the sentinel it would re-populate toTokensFilter from the source's own balances and the
        // unsatisfiable constraint would be silently lost, delivering tokenA again.
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(Source),
            Sink = AddressIdPool.StringOf(Sink),
            QuantizedMode = true,
            ToTokens = new List<string> { AddressIdPool.StringOf(TokenB) },
        };

        Assert.That(SolveStepCount(BuildFactory(), request), Is.EqualTo(0),
            "quantized + explicit unsatisfiable toTokens must yield NO path; the sentinel (STEP 2a1) " +
            "must run before quantized auto-discovery (STEP 2a2) so it is not overwritten");
    }

    [Test]
    public void SwapMode_SatisfiableToTokens_SentinelDoesNotFire()
    {
        // Swap mode (source == sink) is deliberately EXCLUDED from the STEP 2a1 sentinel (the guard is
        // `!sourceEqualsSink`). Here a self-conversion request asks for tokenA, which the source both
        // holds and trusts, so the swap is satisfiable and a virtual sink must be built.
        //
        // If the sentinel wrongly fired in swap mode it would replace the explicit, satisfiable
        // toTokens with the {-1} sentinel, dropping every real token edge into the virtual sink, which
        // would then be pruned (VirtualSinkAddress == null). Asserting the virtual sink SURVIVES pins
        // that swap behavior is preserved and the sentinel does not leak into source==sink flow.
        var source = Node(0x7701);   // reuse Source: holds + trusts tokenA
        var intermediary = Node(0x7720);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, TokenA, 200_000_000L); // source holds tokenA
        mock.AddBalance(intermediary, TokenA, 200_000_000L);
        mock.AddTrust(source, TokenA);                 // source trusts (wants back) tokenA
        mock.AddTrust(intermediary, source);
        mock.AddTrust(intermediary, TokenA);
        mock.AddRegisteredAvatar(AddressIdPool.StringOf(source));
        mock.AddRegisteredAvatar(AddressIdPool.StringOf(intermediary));
        mock.AddRegisteredAvatar(AddressIdPool.StringOf(TokenA));

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source), // swap mode: source == sink
            QuantizedMode = false,
            ToTokens = new List<string> { AddressIdPool.StringOf(TokenA) },
        };

        // Must not throw, and the virtual sink must be created (sentinel not injected).
        var capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request);
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Not.Null,
            "swap mode with a satisfiable toTokens must build a virtual sink; the STEP 2a1 sentinel " +
            "must not fire for source == sink");
    }

    [Test]
    public void SwapMode_UnsatisfiableToTokens_NoSentinel_NoThrow()
    {
        // The `!sourceEqualsSink` boundary on STEP 2a1, for the UNSATISFIABLE case: source == sink with
        // an explicit toToken the source neither holds nor trusts. In invitation mode this is exactly
        // the case that injects the -1 sentinel; here (swap mode) the sentinel must be skipped.
        //
        // Observable swap behavior must be preserved: CreateCapacityGraph does not throw, no virtual
        // sink is built (the resolved toTokens filter is empty in swap mode, so needsVirtualSink stays
        // false), and the solver yields no path — without the invitation-mode sentinel ever firing.
        var source = Node(0x7701);

        var mock = new MockLoadGraph();
        mock.AddBalance(source, TokenA, 200_000_000L); // source holds tokenA only
        mock.AddTrust(source, TokenA);
        mock.AddRegisteredAvatar(AddressIdPool.StringOf(source));
        mock.AddRegisteredAvatar(AddressIdPool.StringOf(TokenA));

        var factory = new GraphFactory("0x0000000000000000000000000000000000000000", mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustGraph = factory.V2TrustGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(trustGraph);

        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(source), // swap mode: source == sink
            QuantizedMode = false,
            ToTokens = new List<string> { AddressIdPool.StringOf(TokenB) }, // never held/trusted -> unsatisfiable
        };

        CapacityGraph capacityGraph = null!;
        Assert.That(() => capacityGraph = factory.CreateCapacityGraph(balanceGraph, trustLookup, request),
            Throws.Nothing, "swap mode with an unsatisfiable toTokens must not throw");
        Assert.That(capacityGraph.VirtualSinkAddress, Is.Null,
            "swap mode skips STEP 2a narrowing and the sentinel, so an empty resolved filter builds no virtual sink");

        var response = new V2Pathfinder().ComputeMaxFlowWithPath(capacityGraph, request, OneCrc);
        Assert.That(response.Transfers?.Count ?? 0, Is.EqualTo(0),
            "swap mode with an unsatisfiable toTokens yields no path via the source==sink path guard, not the sentinel");
    }
}
