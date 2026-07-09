using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Helpers;
using Nethermind.Int256;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Causality tests for the pathfinder "what-if" overlay inputs (simulatedBalances /
/// simulatedTrusts). Existing coverage (<see cref="SimulatedBalanceTests"/>,
/// GraphFactoryTests) proves the overlay produces a path, but never asserts the
/// counterfactual — that the SAME graph yields NO path once the overlay is removed.
/// Without that negative control a "path found" result cannot distinguish
/// "the overlay created the path" from "the path existed anyway".
///
/// Each test runs the identical base graph twice: once without the overlay (expect no
/// path) and once with it (expect a path with positive flow), making the overlay
/// verifiably load-bearing. In every base graph BOTH endpoints already exist as real
/// nodes (via unrelated balance/trust) so the no-path result is caused specifically by
/// the missing liquidity/trust the overlay supplies — not by an absent source/sink node.
/// All offline (MockLoadGraph) → runs in CI without TEST_ENV_URL.
/// </summary>
[TestFixture, Parallelizable]
[Category("Unit")]
public class SimulatedOverlayCausalityTests
{
    private const string RouterAddress = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";

    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");
    private static string Addr(int i) => AddressIdPool.StringOf(Node(i));

    // 1 CRC in wei — a modest target every positive case comfortably satisfies.
    private static readonly UInt256 OneCrc = UInt256.Parse("1000000000000000000");

    [Test]
    public void SimulatedBalance_IsLoadBearing_NoBalanceNoPath_WithOverlayPath()
    {
        // sink trusts the token, so the ONLY thing missing for a path is source-side
        // liquidity. Supplying it via simulatedBalances must create the path.
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);

        MockLoadGraph BaseGraph()
        {
            var mock = new MockLoadGraph();
            mock.AddTrust(sink, source); // source present as a node; still holds nothing
            mock.AddTrust(sink, token); // sink present and trusts the token
            return mock;
        }

        var baseRequest = new FlowRequest { Source = Addr(1), Sink = Addr(2) };
        AssertNoPath(BaseGraph(), baseRequest,
            "Source has no balance and no overlay — a path must not exist");

        var overlayRequest = new FlowRequest
        {
            Source = Addr(1),
            Sink = Addr(2),
            SimulatedBalances = new List<SimulatedBalance>
            {
                new() { Holder = Addr(1), Token = Addr(10), Amount = "100000000000000000000" } // 100 CRC
            }
        };
        AssertHasPath(BaseGraph(), overlayRequest,
            "simulatedBalances must create a path that did not exist without it");
    }

    [Test]
    public void SimulatedTrust_IsLoadBearing_NoTrustNoPath_WithOverlayPath()
    {
        // source holds a real balance; the ONLY thing missing is the sink trusting that
        // token. Supplying it via simulatedTrusts must create the path.
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);
        var unrelated = Node(99);

        MockLoadGraph BaseGraph()
        {
            var mock = new MockLoadGraph();
            mock.AddBalance(source, token, 100_000_000); // 100 CRC of token(10)
            mock.AddTrust(sink, unrelated); // sink is a node, but does NOT trust token(10)
            return mock;
        }

        var baseRequest = new FlowRequest { Source = Addr(1), Sink = Addr(2) };
        AssertNoPath(BaseGraph(), baseRequest,
            "Sink does not trust the source token and there is no overlay — a path must not exist");

        var overlayRequest = new FlowRequest
        {
            Source = Addr(1),
            Sink = Addr(2),
            SimulatedTrusts = new List<SimulatedTrust>
            {
                new() { Truster = Addr(2), Trustee = Addr(10) }
            }
        };
        AssertHasPath(BaseGraph(), overlayRequest,
            "simulatedTrusts must create a path that did not exist without it");
    }

    [Test]
    public void SimulatedBalanceAndTrust_Combined_IsLoadBearing()
    {
        // Both endpoints exist on-chain via unrelated filler balance/trust, so the
        // negative control isolates the missing liquidity+trust for `token` rather than
        // missing graph nodes. Only both overlays together can create a path.
        var source = Node(1);
        var sink = Node(2);
        var token = Node(10);
        var fillerHeld = Node(98); // source holds this, but sink does not trust it
        var fillerTrusted = Node(99); // sink trusts this, but nobody holds it

        MockLoadGraph BaseGraph()
        {
            var mock = new MockLoadGraph();
            mock.AddBalance(source, fillerHeld, 100_000_000); // source present, no usable route
            mock.AddTrust(sink, fillerTrusted); // sink present, trusts nothing that is held
            return mock;
        }

        var baseRequest = new FlowRequest { Source = Addr(1), Sink = Addr(2) };
        AssertNoPath(BaseGraph(), baseRequest,
            "Endpoints exist but neither liquidity nor trust for the token — no path without overlay");

        var overlayRequest = new FlowRequest
        {
            Source = Addr(1),
            Sink = Addr(2),
            SimulatedBalances = new List<SimulatedBalance>
            {
                new() { Holder = Addr(1), Token = Addr(10), Amount = "100000000000000000000" }
            },
            SimulatedTrusts = new List<SimulatedTrust>
            {
                new() { Truster = Addr(2), Trustee = Addr(10) }
            }
        };
        AssertHasPath(BaseGraph(), overlayRequest,
            "Combined simulatedBalances + simulatedTrusts must create a path");
    }

    // ---- helpers -----------------------------------------------------------

    private static (BalanceGraph bg, Dictionary<int, HashSet<int>> lookup) Build(MockLoadGraph mock)
    {
        var factory = new GraphFactory(RouterAddress, mock);
        var balanceGraph = factory.V2BalanceGraph();
        var trustLookup = GraphFactory.BuildTrustLookup(factory.V2TrustGraph());
        return (balanceGraph, trustLookup);
    }

    private static void AssertHasPath(MockLoadGraph mock, FlowRequest request, string because)
    {
        var factory = new GraphFactory(RouterAddress, mock);
        var (bg, lookup) = Build(mock);
        var capacityGraph = factory.CreateCapacityGraph(bg, lookup, request);
        var result = new V2Pathfinder().ComputeMaxFlowWithPath(capacityGraph, request, OneCrc);

        Assert.That(result.Transfers, Is.Not.Null.And.Not.Empty, because);
        Assert.That(result.MaxFlow, Is.Not.Null.And.Not.Empty, "MaxFlow must be populated");
        Assert.That(UInt256.Parse(result.MaxFlow!) > UInt256.Zero, Is.True, because);
    }

    private static void AssertNoPath(MockLoadGraph mock, FlowRequest request, string because)
    {
        // Both endpoints exist in every base graph, so no-path manifests as a zero-flow
        // response from the solver (V2Pathfinder.ResolveAndGuard / MaxFlowSolver), NOT as
        // an exception. Any exception here is a genuine failure and must surface, so this
        // deliberately does not swallow one.
        var factory = new GraphFactory(RouterAddress, mock);
        var (bg, lookup) = Build(mock);
        var capacityGraph = factory.CreateCapacityGraph(bg, lookup, request);
        var result = new V2Pathfinder().ComputeMaxFlowWithPath(capacityGraph, request, OneCrc);

        var noTransfers = result.Transfers is null || result.Transfers.Count == 0;
        var zeroFlow = string.IsNullOrEmpty(result.MaxFlow) || UInt256.Parse(result.MaxFlow!) == UInt256.Zero;
        Assert.That(noTransfers, Is.True, because);
        Assert.That(zeroFlow, Is.True, because);
    }
}
