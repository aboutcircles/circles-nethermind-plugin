using Circles.Common;
using Circles.Pathfinder.Data;
using Circles.Pathfinder.Host.State;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Smoke tests for <see cref="MaterializedLoadGraph"/> — the in-memory snapshot
/// backing <c>HistoricalGraphCache</c>. These exist because the type is built
/// against the <c>ILoadGraph</c> interface, and a missing override would
/// silently fall back to the interface-default-empty implementation
/// (regressing ScoreGroup support across historical loads).
///
/// Coverage: the four ScoreGroup-aware methods (LoadGroupRouters,
/// LoadScoreRouters, LoadScoreGroupMintLimits, LoadOperatorApprovals) plus the
/// LoadOperatorApprovals lower-casing + empty-input short-circuit semantics.
/// </summary>
[TestFixture, Parallelizable]
public class MaterializedLoadGraphTests
{
    private static MaterializedGraphData StubData(
        IReadOnlyList<(string GroupAddress, string RouterAddress)>? groupRouters = null,
        IReadOnlyList<string>? scoreRouters = null,
        IReadOnlyList<(string GroupAddress, string CollateralToken, string AvailableLimit)>? scoreLimits = null,
        IReadOnlyList<(string Account, string Operator)>? operatorApprovals = null) => new()
    {
        Balances = Array.Empty<(string, int, int, bool, bool)>(),
        Trust = Array.Empty<(string, string, int)>(),
        Groups = Array.Empty<string>(),
        Organizations = Array.Empty<string>(),
        GroupTrusts = Array.Empty<(string, string)>(),
        ConsentedFlags = Array.Empty<(string, bool)>(),
        RegisteredAvatars = Array.Empty<string>(),
        WrapperMappings = Array.Empty<(string, string, CirclesType)>(),
        GroupRouters = groupRouters ?? Array.Empty<(string, string)>(),
        ScoreRouters = scoreRouters ?? Array.Empty<string>(),
        ScoreGroupMintLimits = scoreLimits ?? Array.Empty<(string, string, string)>(),
        OperatorApprovals = operatorApprovals ?? Array.Empty<(string, string)>(),
    };

    [Test]
    public void LoadGroupRouters_RoundTripsData()
    {
        var data = new[]
        {
            ("0xaaaa000000000000000000000000000000000001", "0xbbbb000000000000000000000000000000000001"),
            ("0xaaaa000000000000000000000000000000000002", "0xbbbb000000000000000000000000000000000002"),
        };
        var sut = new MaterializedLoadGraph(StubData(groupRouters: data));

        var result = sut.LoadGroupRouters().ToList();

        Assert.That(result, Is.EquivalentTo(data));
    }

    [Test]
    public void LoadScoreRouters_RoundTripsData()
    {
        var data = new[]
        {
            "0xcccc000000000000000000000000000000000001",
            "0xcccc000000000000000000000000000000000002",
        };
        var sut = new MaterializedLoadGraph(StubData(scoreRouters: data));

        var result = sut.LoadScoreRouters().ToList();

        Assert.That(result, Is.EquivalentTo(data));
    }

    [Test]
    public void LoadScoreGroupMintLimits_RoundTripsData()
    {
        var data = new[]
        {
            ("0xdd00000000000000000000000000000000000001", "0xee00000000000000000000000000000000000001", "1000"),
            ("0xdd00000000000000000000000000000000000002", "0xee00000000000000000000000000000000000002", "2500"),
        };
        var sut = new MaterializedLoadGraph(StubData(scoreLimits: data));

        var result = sut.LoadScoreGroupMintLimits().ToList();

        Assert.That(result, Is.EquivalentTo(data));
    }

    [Test]
    public void LoadOperatorApprovals_FiltersByLowercasedAccountSet()
    {
        const string router = "0xff00000000000000000000000000000000000001";
        const string avatar = "0xff00000000000000000000000000000000000002";
        var data = new[]
        {
            (router, avatar),
            ("0xff00000000000000000000000000000000000099", "0xff000000000000000000000000000000000000aa"),
        };
        var sut = new MaterializedLoadGraph(StubData(operatorApprovals: data));

        // Mixed-case caller — must be lowercased before lookup.
        var result = sut.LoadOperatorApprovals(new[] { router.ToUpperInvariant() }).ToList();

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Account, Is.EqualTo(router));
        Assert.That(result[0].Operator, Is.EqualTo(avatar));
    }

    [Test]
    public void LoadOperatorApprovals_EmptyAccountsShortCircuitsToEmpty()
    {
        var sut = new MaterializedLoadGraph(StubData(
            operatorApprovals: new[] { ("0x01", "0x02") }));

        Assert.That(sut.LoadOperatorApprovals(Array.Empty<string>()), Is.Empty);
        // Whitespace-only input is filtered out before the empty check.
        Assert.That(sut.LoadOperatorApprovals(new[] { "", "  ", "\t" }), Is.Empty);
    }

    [Test]
    public void LoadOperatorApprovals_UnknownAccountReturnsEmpty()
    {
        var data = new[] { ("0xff00000000000000000000000000000000000001", "0xff00000000000000000000000000000000000002") };
        var sut = new MaterializedLoadGraph(StubData(operatorApprovals: data));

        var result = sut.LoadOperatorApprovals(new[] { "0x0000000000000000000000000000000000000123" });

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void LoadOperatorApprovals_ReturnsMaterializedResult()
    {
        // Regression: previous shape returned a deferred LINQ chain — re-enumeration
        // re-ran the filter. The current contract is "materialized list".
        const string router = "0xff00000000000000000000000000000000000001";
        var data = new[] { (router, "0xff00000000000000000000000000000000000002") };
        var sut = new MaterializedLoadGraph(StubData(operatorApprovals: data));

        var first = sut.LoadOperatorApprovals(new[] { router });
        Assert.That(first, Is.AssignableTo<IReadOnlyCollection<(string, string)>>(),
            "LoadOperatorApprovals must return a materialized collection, not a deferred IEnumerable, " +
            "to avoid re-running the filter on every enumeration.");
    }
}
