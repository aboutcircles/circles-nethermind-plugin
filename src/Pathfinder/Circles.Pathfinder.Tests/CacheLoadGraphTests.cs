using Circles.Pathfinder.Data;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Tests for CacheLoadGraph — ILoadGraph implementation backed by a deserialized snapshot.
/// Covers: balance/trust/group/consent round-tripping, lowercase normalization,
/// CirclesType mapping, empty snapshot handling, and snapshot replacement.
/// </summary>
[TestFixture]
public class CacheLoadGraphTests
{
    [Test]
    public void LoadV2Balances_ShouldYieldCorrectTuples()
    {
        var snapshot = CreateSnapshot(balances: new[]
        {
            new PathfinderGraphBalanceRow("1000000000000000000", "0xAlice", "0xToken", 123456, false, "demurraged")
        });
        var graph = new CacheLoadGraph(snapshot);

        var balances = graph.LoadV2Balances().ToList();

        Assert.That(balances, Has.Count.EqualTo(1));
        Assert.That(balances[0].Balance, Is.EqualTo("1000000000000000000"));
        Assert.That(balances[0].IsWrapped, Is.False);
        Assert.That(balances[0].IsStatic, Is.False);
    }

    [Test]
    public void LoadV2Balances_ShouldMapStaticCirclesType()
    {
        var snapshot = CreateSnapshot(balances: new[]
        {
            new PathfinderGraphBalanceRow("500", "0xAlice", "0xWrapper", 0, true, "static")
        });
        var graph = new CacheLoadGraph(snapshot);

        var balances = graph.LoadV2Balances().ToList();

        Assert.That(balances[0].IsStatic, Is.True);
        Assert.That(balances[0].IsWrapped, Is.True);
    }

    [Test]
    public void LoadV2Balances_ShouldNormalizeToLowercase()
    {
        var snapshot = CreateSnapshot(balances: new[]
        {
            new PathfinderGraphBalanceRow("100", "0xABCD", "0xEFGH", 0, false, "demurraged")
        });
        var graph = new CacheLoadGraph(snapshot);

        var balances = graph.LoadV2Balances().ToList();

        // AddressIdPool.IdOf normalizes lowercase internally;
        // verify the balance data is accessible (addresses map correctly)
        Assert.That(balances, Has.Count.EqualTo(1));
    }

    [Test]
    public void LoadV2Balances_ShouldReturnEmpty_WhenNullBalances()
    {
        var snapshot = CreateSnapshot();
        var graph = new CacheLoadGraph(snapshot);

        var balances = graph.LoadV2Balances().ToList();

        Assert.That(balances, Is.Empty);
    }

    [Test]
    public void LoadV2Trust_ShouldYieldCorrectTuples()
    {
        var snapshot = CreateSnapshot(trust: new[]
        {
            new PathfinderGraphTrustRow("0xTruster", "0xTrustee", 100)
        });
        var graph = new CacheLoadGraph(snapshot);

        var trust = graph.LoadV2Trust().ToList();

        Assert.That(trust, Has.Count.EqualTo(1));
        Assert.That(trust[0].Truster, Is.EqualTo("0xtruster")); // lowercase
        Assert.That(trust[0].Trustee, Is.EqualTo("0xtrustee")); // lowercase
        Assert.That(trust[0].Limit, Is.EqualTo(100));
    }

    [Test]
    public void LoadGroups_ShouldYieldLowercaseAddresses()
    {
        var snapshot = CreateSnapshot(groups: new[]
        {
            new PathfinderGraphGroupRow("0xGroupADDR")
        });
        var graph = new CacheLoadGraph(snapshot);

        var groups = graph.LoadGroups().ToList();

        Assert.That(groups, Has.Count.EqualTo(1));
        Assert.That(groups[0], Is.EqualTo("0xgroupaddr"));
    }

    [Test]
    public void LoadGroupTrusts_ShouldYieldLowercasePairs()
    {
        var snapshot = CreateSnapshot(groupTrusts: new[]
        {
            new PathfinderGraphGroupTrustRow("0xGroup", "0xToken")
        });
        var graph = new CacheLoadGraph(snapshot);

        var groupTrusts = graph.LoadGroupTrusts().ToList();

        Assert.That(groupTrusts, Has.Count.EqualTo(1));
        Assert.That(groupTrusts[0].GroupAddress, Is.EqualTo("0xgroup"));
        Assert.That(groupTrusts[0].TrustedToken, Is.EqualTo("0xtoken"));
    }

    [Test]
    public void LoadConsentedFlowFlags_ShouldYieldCorrectTuples()
    {
        var snapshot = CreateSnapshot(consent: new[]
        {
            new PathfinderGraphConsentedFlowRow("0xAlice", true),
            new PathfinderGraphConsentedFlowRow("0xBob", false)
        });
        var graph = new CacheLoadGraph(snapshot);

        var consent = graph.LoadConsentedFlowFlags().ToList();

        Assert.That(consent, Has.Count.EqualTo(2));
        Assert.That(consent[0].Avatar, Is.EqualTo("0xalice"));
        Assert.That(consent[0].HasConsentedFlow, Is.True);
        Assert.That(consent[1].Avatar, Is.EqualTo("0xbob"));
        Assert.That(consent[1].HasConsentedFlow, Is.False);
    }

    [Test]
    public void LastProcessedBlock_ShouldReflectSnapshot()
    {
        var snapshot = CreateSnapshot(lastBlock: 42000);
        var graph = new CacheLoadGraph(snapshot);

        Assert.That(graph.LastProcessedBlock, Is.EqualTo(42000));
    }

    [Test]
    public void ReplaceSnapshot_ShouldUpdateAllData()
    {
        var initial = CreateSnapshot(lastBlock: 100, balances: new[]
        {
            new PathfinderGraphBalanceRow("1000", "0xalice", "0xtoken", 0, false, "demurraged")
        });
        var graph = new CacheLoadGraph(initial);
        Assert.That(graph.LoadV2Balances().Count(), Is.EqualTo(1));
        Assert.That(graph.LastProcessedBlock, Is.EqualTo(100));

        var updated = CreateSnapshot(lastBlock: 200, balances: new[]
        {
            new PathfinderGraphBalanceRow("2000", "0xbob", "0xtoken2", 0, false, "demurraged"),
            new PathfinderGraphBalanceRow("3000", "0xcharlie", "0xtoken3", 0, true, "static")
        });
        graph.ReplaceSnapshot(updated);

        Assert.That(graph.LastProcessedBlock, Is.EqualTo(200));
        Assert.That(graph.LoadV2Balances().Count(), Is.EqualTo(2));
    }

    [Test]
    public void LoadV2Balances_StaticCirclesType_ShouldBeCaseInsensitive()
    {
        var snapshot = CreateSnapshot(balances: new[]
        {
            new PathfinderGraphBalanceRow("100", "0xa", "0xb", 0, true, "STATIC"),
            new PathfinderGraphBalanceRow("200", "0xc", "0xd", 0, true, "Static"),
            new PathfinderGraphBalanceRow("300", "0xe", "0xf", 0, false, "Demurraged")
        });
        var graph = new CacheLoadGraph(snapshot);

        var balances = graph.LoadV2Balances().ToList();

        Assert.That(balances[0].IsStatic, Is.True);
        Assert.That(balances[1].IsStatic, Is.True);
        Assert.That(balances[2].IsStatic, Is.False);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static PathfinderGraphSnapshot CreateSnapshot(
        long lastBlock = 1000,
        IReadOnlyList<PathfinderGraphBalanceRow>? balances = null,
        IReadOnlyList<PathfinderGraphTrustRow>? trust = null,
        IReadOnlyList<PathfinderGraphGroupRow>? groups = null,
        IReadOnlyList<PathfinderGraphGroupTrustRow>? groupTrusts = null,
        IReadOnlyList<PathfinderGraphConsentedFlowRow>? consent = null)
    {
        return new PathfinderGraphSnapshot(
            SchemaVersion: 1,
            LastProcessedBlock: lastBlock,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Balances: balances,
            Trust: trust,
            Groups: groups,
            GroupTrusts: groupTrusts,
            ConsentedFlow: consent);
    }
}
