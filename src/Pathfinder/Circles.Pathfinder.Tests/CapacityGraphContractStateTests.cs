using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Validation;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for CapacityGraphContractState — the bridge from the runtime CapacityGraph
/// to IContractState consumed by HubContractValidator. Pins the IsApprovedForAll lookup
/// direction (account=router → operator=avatar) and the behaviors that C3 will later
/// tighten (fail-open on empty approvals is current behavior; documented but not changed
/// here).
/// </summary>
[TestFixture, Parallelizable]
public class CapacityGraphContractStateTests
{
    private static readonly string ScoreRouter = "0xf2ff000000000000000000000000000000000009";
    private static readonly string Alice = "0xf2aa000000000000000000000000000000000002";
    private static readonly string Bob = "0xf2bb000000000000000000000000000000000003";

    [Test]
    public void IsApprovedForAll_PopulatedApprovals_ReturnsTrueForApprovedOperator()
    {
        int routerId = AddressIdPool.IdOf(ScoreRouter);
        int aliceId = AddressIdPool.IdOf(Alice);

        var g = new CapacityGraph();
        g.AddAvatar(aliceId);
        g.OperatorApprovals[routerId] = new HashSet<int> { aliceId };

        var state = new CapacityGraphContractState(g);

        Assert.That(state.IsApprovedForAll(ScoreRouter, Alice), Is.True);
    }

    [Test]
    public void IsApprovedForAll_PopulatedApprovals_ReturnsFalseForKnownButUnapprovedOperator()
    {
        int routerId = AddressIdPool.IdOf(ScoreRouter);
        int aliceId = AddressIdPool.IdOf(Alice);
        int bobId = AddressIdPool.IdOf(Bob);

        var g = new CapacityGraph();
        g.AddAvatar(aliceId);
        g.AddAvatar(bobId);
        g.OperatorApprovals[routerId] = new HashSet<int> { aliceId };

        var state = new CapacityGraphContractState(g);

        Assert.That(state.IsApprovedForAll(ScoreRouter, Bob), Is.False,
            "Bob is known to the graph but not in the router's approval set.");
    }

    [Test]
    public void IsApprovedForAll_EmptyApprovals_ReturnsTrue_CurrentBehavior()
    {
        // Pins the current fail-open default: with no OperatorApprovals at all, the gate
        // is assumed inapplicable (legacy behavior — only score-group policies populate
        // approvals). C3 will tighten this to fail-closed when score policies are
        // configured; this test documents the pre-C3 state so the change is visible.
        var g = new CapacityGraph();
        var state = new CapacityGraphContractState(g);

        Assert.That(state.IsApprovedForAll(ScoreRouter, Alice), Is.True);
    }

    [Test]
    public void IsScoreRouter_AddressInScoreRouterIds_ReturnsTrue()
    {
        int routerId = AddressIdPool.IdOf(ScoreRouter);
        var g = new CapacityGraph();
        g.ScoreRouterIds.Add(routerId);

        var state = new CapacityGraphContractState(g);

        Assert.That(state.IsScoreRouter(ScoreRouter), Is.True);
    }

    [Test]
    public void IsScoreRouter_AddressNotInScoreRouterIds_ReturnsFalse_EvenWithOperatorApprovals()
    {
        // C2: the legacy heuristic returned true when an address had an OperatorApprovals
        // entry — incidental, not source-of-truth. Post-C2, only ScoreRouterIds (loaded
        // from CrcV2_ScoreGroup.GroupInitialized) classifies score routers.
        int routerId = AddressIdPool.IdOf(ScoreRouter);
        int aliceId = AddressIdPool.IdOf(Alice);
        var g = new CapacityGraph();
        g.AddAvatar(aliceId);
        g.OperatorApprovals[routerId] = new HashSet<int> { aliceId };
        // intentionally do NOT add to ScoreRouterIds

        var state = new CapacityGraphContractState(g);

        Assert.That(state.IsScoreRouter(ScoreRouter), Is.False,
            "OperatorApprovals membership alone must no longer classify a router as a score router.");
    }

    [Test]
    public void IsScoreRouter_UnknownAddress_ReturnsFalse()
    {
        var g = new CapacityGraph();
        var state = new CapacityGraphContractState(g);

        Assert.That(state.IsScoreRouter(ScoreRouter), Is.False);
    }
}
