using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Validation;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for CapacityGraphContractState — the bridge from the runtime CapacityGraph
/// to IContractState consumed by HubContractValidator. Pins the IsApprovedForAll lookup
/// direction (account=router → operator=avatar) and the C3 fail-closed-when-routers-
/// indexed semantics: empty OperatorApprovals is permissive ONLY in builds with no
/// score-group policy active (ScoreRouterIds empty).
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
    public void IsApprovedForAll_EmptyApprovals_FailOpen_WhenNoScoreRouters()
    {
        // Legacy semantics preserved: when ScoreRouterIds is empty (no score-group
        // policy active OR indexer hasn't seen any GroupInitialized event yet), absence
        // of approval data means "rule inapplicable" → permissive. This keeps non-score-
        // group CRC paths working in builds that don't run a score policy at all.
        var g = new CapacityGraph();
        var state = new CapacityGraphContractState(g);

        Assert.That(state.IsApprovedForAll(ScoreRouter, Alice), Is.True);
    }

    [Test]
    public void IsApprovedForAll_EmptyApprovals_FailClosed_WhenScoreRoutersIndexed()
    {
        // C3: once the cache has seen at least one CrcV2_ScoreGroup.GroupInitialized
        // event (ScoreRouterIds non-empty), an empty OperatorApprovals dict means
        // "indexer behind on approval events", not "nobody approved anything". Must
        // drop the edge — otherwise the pathfinder returns paths that revert at the
        // policy hook on-chain.
        // Argument order matches production: HubContractValidator passes (router, avatar)
        // i.e. account=router, operator=avatar (OperatorApprovals is keyed router→avatars).
        int routerId = AddressIdPool.IdOf(ScoreRouter);
        var g = new CapacityGraph();
        g.ScoreRouterIds.Add(routerId);
        var state = new CapacityGraphContractState(g);

        Assert.That(state.IsApprovedForAll(ScoreRouter, Alice), Is.False,
            "Score routers are known but no approvals indexed — must fail closed.");
    }

    [Test]
    public void IsApprovedForAll_UnknownAccount_FailClosed_WhenScoreRoutersIndexed()
    {
        // C3: second fail-open branch. When the account address isn't in AddressIdPool
        // it cannot possibly have an approval entry; under an active score policy this
        // must also drop the edge rather than wave it through. Uses a fresh address
        // that has not been interned by any earlier test to exercise the TryIdOf-miss
        // path.
        var unknownAccount = "0xf2ee000000000000000000000000000000000099";
        int routerId = AddressIdPool.IdOf(ScoreRouter);
        int aliceId = AddressIdPool.IdOf(Alice);
        var g = new CapacityGraph();
        g.ScoreRouterIds.Add(routerId);
        // Populate approvals so the first guard (empty dict) is skipped — we want to
        // reach the AddressIdPool.TryIdOf(account) miss specifically.
        g.OperatorApprovals[routerId] = new HashSet<int> { aliceId };
        var state = new CapacityGraphContractState(g);

        Assert.That(state.IsApprovedForAll(unknownAccount, ScoreRouter), Is.False,
            "Account not in AddressIdPool under an active score policy must fail closed.");
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
