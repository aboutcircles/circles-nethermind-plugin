using System.Reflection;
using Circles.Common;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Host.Data;
using Circles.Pathfinder.Host.State;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Regression tests for the historical score-group loaders.
///
/// Bug: HistoricalLoadGraph + MaterializedLoadGraph both inherited the empty ILoadGraph
/// defaults (=> []) for LoadGroupRouters / LoadScoreRouters / LoadScoreGroupMintLimits /
/// LoadOperatorApprovals. MaterializedLoadGraph is the loader GraphFactory actually consumes
/// for historical (X-Max-Block-Number) requests, so score groups silently degraded to regular
/// groups — no operator gate, unbounded mint edges — diverging from the live graph.
///
/// These tests are DB-free: they verify the materialized wrapper forwards the score data it is
/// given (not []), and that HistoricalLoadGraph overrides the loaders rather than inheriting
/// the empty defaults.
/// </summary>
[TestFixture]
public class HistoricalScoreLoadersTests
{
    private static MaterializedLoadGraph BuildWithScoreData() => new(
        balances: [],
        trust: [],
        groups: ["0x9999999999999999999999999999999999999999"],
        organizations: [],
        groupTrusts: [],
        consentedFlags: [],
        registeredAvatars: [],
        wrapperMappings: [],
        groupRouters: [("0xaaaa000000000000000000000000000000000001", "0xrouter00000000000000000000000000000000a1")],
        scoreRouters: ["0xrouter00000000000000000000000000000000a1"],
        scoreGroupMintLimits: [("0xaaaa000000000000000000000000000000000001",
                                "0xcccc000000000000000000000000000000000001", "12345")],
        operatorApprovals:
        [
            ("0xrouter00000000000000000000000000000000a1", "0xopprov0000000000000000000000000000000001"),
            ("0xotheracct00000000000000000000000000000002", "0xopprov0000000000000000000000000000000003"),
        ]);

    [Test]
    public void Materialized_GroupRouters_AreForwarded_NotEmpty()
    {
        var lg = BuildWithScoreData();
        var routers = lg.LoadGroupRouters().ToList();
        Assert.That(routers, Has.Count.EqualTo(1));
        Assert.That(routers[0].RouterAddress, Is.EqualTo("0xrouter00000000000000000000000000000000a1"));
    }

    [Test]
    public void Materialized_ScoreRouters_AreForwarded_NotEmpty()
    {
        Assert.That(BuildWithScoreData().LoadScoreRouters().ToList(), Has.Count.EqualTo(1));
    }

    [Test]
    public void Materialized_ScoreGroupMintLimits_AreForwarded_NotEmpty()
    {
        var limits = BuildWithScoreData().LoadScoreGroupMintLimits().ToList();
        Assert.That(limits, Has.Count.EqualTo(1));
        Assert.That(limits[0].AvailableLimit, Is.EqualTo("12345"));
    }

    [Test]
    public void Materialized_OperatorApprovals_FilterByRequestedAccounts()
    {
        var lg = BuildWithScoreData();

        // GraphFactory queries approvals for specific router accounts — only those should return.
        var forRouter = lg.LoadOperatorApprovals(["0xrouter00000000000000000000000000000000a1"]).ToList();
        Assert.That(forRouter, Has.Count.EqualTo(1));
        Assert.That(forRouter[0].Operator, Is.EqualTo("0xopprov0000000000000000000000000000000001"));

        // An account with no approvals returns nothing; empty input returns nothing.
        Assert.That(lg.LoadOperatorApprovals(["0xnobody00000000000000000000000000000000ff"]), Is.Empty);
        Assert.That(lg.LoadOperatorApprovals([]), Is.Empty);
    }

    [Test]
    public void HistoricalLoadGraph_Overrides_AllScoreLoaders_NotInheritedDefaults()
    {
        // Guards against re-introducing the silent gap: each score loader must be declared on
        // HistoricalLoadGraph itself, not inherited from the ILoadGraph default interface method.
        var type = typeof(HistoricalLoadGraph);
        foreach (var name in new[]
                 {
                     nameof(ILoadGraph.LoadGroupRouters),
                     nameof(ILoadGraph.LoadScoreRouters),
                     nameof(ILoadGraph.LoadScoreGroupMintLimits),
                     nameof(ILoadGraph.LoadOperatorApprovals),
                     nameof(ILoadGraph.LoadOrganizations),
                     nameof(ILoadGraph.LoadWrapperMappings),
                 })
        {
            var method = type.GetMethod(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.That(method, Is.Not.Null,
                $"HistoricalLoadGraph must declare {name} — inheriting the empty ILoadGraph default "
                + "silently degrades historical score-group pathfinding.");
        }
    }
}
