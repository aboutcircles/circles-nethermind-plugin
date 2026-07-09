using System.Numerics;
using Circles.Rpc.Host;
using Cap = Circles.Rpc.Host.ScoreGroupRedeemPlanner.CollateralCap;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// DB-free tests for the redeem allocation logic behind <c>circles_findScoreGroupRedeemPath</c>:
/// the per-collateral MIN clamp, the shared-budget greedy allocation, and the findPath-shaped output
/// (collateral legs only; <c>maxFlow</c> conveys the gCRC amount, no burn leg).
/// </summary>
[TestFixture]
public class ScoreGroupRedeemPlannerTests
{
    private const string Holder = "0x1111111111111111111111111111111111111111";
    private const string Treasury = "0x2222222222222222222222222222222222222222";
    private const string ColA = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ColB = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ColC = "0xcccccccccccccccccccccccccccccccccccccccc";

    private static BigInteger Big(string s) => BigInteger.Parse(s);

    [Test]
    public void SingleCollateral_EntitlementBelowCap_RedeemsEntitlement()
    {
        // entitlement < cap  =>  MIN picks the entitlement
        var r = ScoreGroupRedeemPlanner.Plan(Big("100"), new[] { new Cap(ColA, Big("1000")) }, Holder, Treasury);

        Assert.That(r.MaxFlow, Is.EqualTo("100"));
        Assert.That(r.Transfers, Has.Count.EqualTo(1)); // collateral leg only, no burn leg
        Assert.That(r.Transfers[0].From, Is.EqualTo(Treasury));
        Assert.That(r.Transfers[0].To, Is.EqualTo(Holder));
        Assert.That(r.Transfers[0].TokenOwner, Is.EqualTo(ColA));
        Assert.That(r.Transfers[0].Value, Is.EqualTo("100"));
    }

    [Test]
    public void SingleCollateral_CapBelowEntitlement_ClampsToTreasuryHolding()
    {
        // cap < entitlement  =>  MIN picks the treasury cap (the "whatever is LOWER")
        var r = ScoreGroupRedeemPlanner.Plan(Big("1000"), new[] { new Cap(ColA, Big("250")) }, Holder, Treasury);

        Assert.That(r.MaxFlow, Is.EqualTo("250"));
        Assert.That(r.Transfers, Has.Count.EqualTo(1));
        Assert.That(r.Transfers[0].Value, Is.EqualTo("250"));
    }

    [Test]
    public void MultiCollateral_BudgetIsShared_NotAppliedPerCollateral()
    {
        // The classic over-count guard: entitlement 120 with two caps of 100 each must total 120,
        // NOT 200 (which independent MIN-per-collateral would yield). Greedy-by-largest fills the
        // first to its cap (100) then the remainder (20) from the next.
        var caps = new[] { new Cap(ColA, Big("100")), new Cap(ColB, Big("100")) };
        var r = ScoreGroupRedeemPlanner.Plan(Big("120"), caps, Holder, Treasury);

        Assert.That(r.MaxFlow, Is.EqualTo("120"));
        var legSum = r.Transfers.Aggregate(BigInteger.Zero, (acc, t) => acc + Big(t.Value));
        Assert.That(legSum, Is.EqualTo(Big("120")));
        Assert.That(r.Transfers, Has.Count.EqualTo(2));
        Assert.That(r.Transfers[0].Value, Is.EqualTo("100")); // largest first, filled to cap
        Assert.That(r.Transfers[1].Value, Is.EqualTo("20"));  // remainder
    }

    [Test]
    public void MultiCollateral_GreedyFillsLargestFirst()
    {
        var caps = new[] { new Cap(ColA, Big("30")), new Cap(ColB, Big("500")), new Cap(ColC, Big("70")) };
        var r = ScoreGroupRedeemPlanner.Plan(Big("1000"), caps, Holder, Treasury);

        // entitlement exceeds total treasury (600) => redeem all 600, ordered largest→smallest
        Assert.That(r.MaxFlow, Is.EqualTo("600"));
        Assert.That(r.Transfers, Has.Count.EqualTo(3));
        Assert.That(r.Transfers[0].TokenOwner, Is.EqualTo(ColB));
        Assert.That(r.Transfers[0].Value, Is.EqualTo("500"));
        Assert.That(r.Transfers[1].TokenOwner, Is.EqualTo(ColC));
        Assert.That(r.Transfers[1].Value, Is.EqualTo("70"));
        Assert.That(r.Transfers[2].TokenOwner, Is.EqualTo(ColA));
        Assert.That(r.Transfers[2].Value, Is.EqualTo("30"));
    }

    [Test]
    public void MaxFlow_IsMinOfEntitlementAndTotalTreasury()
    {
        var caps = new[] { new Cap(ColA, Big("40")), new Cap(ColB, Big("60")) }; // total 100
        // entitlement above total => capped at total
        Assert.That(ScoreGroupRedeemPlanner.Plan(Big("999"), caps, Holder, Treasury).MaxFlow, Is.EqualTo("100"));
        // entitlement below total => capped at entitlement
        Assert.That(ScoreGroupRedeemPlanner.Plan(Big("70"), caps, Holder, Treasury).MaxFlow, Is.EqualTo("70"));
    }

    [Test]
    public void ZeroEntitlement_ReturnsEmptyPath()
    {
        var r = ScoreGroupRedeemPlanner.Plan(BigInteger.Zero, new[] { new Cap(ColA, Big("100")) }, Holder, Treasury);
        Assert.That(r.MaxFlow, Is.EqualTo("0"));
        Assert.That(r.Transfers, Is.Empty);
    }

    [Test]
    public void NoTreasuryCollateral_ReturnsEmptyPath()
    {
        // Holder has gCRC but the treasury holds nothing (e.g. all already redeemed) => nothing to give back.
        var r = ScoreGroupRedeemPlanner.Plan(Big("100"), new[] { new Cap(ColA, BigInteger.Zero) }, Holder, Treasury);
        Assert.That(r.MaxFlow, Is.EqualTo("0"));
        Assert.That(r.Transfers, Is.Empty);
    }

    [Test]
    public void NoCaps_ReturnsEmptyPath()
    {
        var r = ScoreGroupRedeemPlanner.Plan(Big("100"), Array.Empty<Cap>(), Holder, Treasury);
        Assert.That(r.MaxFlow, Is.EqualTo("0"));
        Assert.That(r.Transfers, Is.Empty);
    }

    [Test]
    public void MaxFlow_EqualsSumOfCollateralLegs()
    {
        // 1:1 redemption: gCRC redeemed (maxFlow) == total collateral returned across the legs.
        var caps = new[] { new Cap(ColA, Big("123")), new Cap(ColB, Big("456")), new Cap(ColC, Big("789")) };
        var r = ScoreGroupRedeemPlanner.Plan(Big("1000"), caps, Holder, Treasury);

        var collateralOut = r.Transfers.Aggregate(BigInteger.Zero, (acc, t) => acc + Big(t.Value));
        Assert.That(Big(r.MaxFlow), Is.EqualTo(collateralOut));
    }

    [Test]
    public void AllLegs_AreTreasuryToHolder()
    {
        var caps = new[] { new Cap(ColA, Big("10")), new Cap(ColB, Big("20")) };
        var r = ScoreGroupRedeemPlanner.Plan(Big("100"), caps, Holder, Treasury);

        Assert.That(r.Transfers, Is.Not.Empty);
        Assert.That(r.Transfers, Has.All.Matches<Circles.Common.Dto.TransferPathStep>(
            t => t.From == Treasury && t.To == Holder));
    }

    [Test]
    public void LargeValues_NoOverflow()
    {
        // ~1.2e24 wei caps (well beyond Int64) exercise the BigInteger path end-to-end.
        var caps = new[] { new Cap(ColA, Big("1200000000000000000000000")), new Cap(ColB, Big("800000000000000000000000")) };
        var r = ScoreGroupRedeemPlanner.Plan(Big("1500000000000000000000000"), caps, Holder, Treasury);

        Assert.That(r.MaxFlow, Is.EqualTo("1500000000000000000000000"));
        Assert.That(r.Transfers[0].Value, Is.EqualTo("1200000000000000000000000")); // largest cap filled
        Assert.That(r.Transfers[1].Value, Is.EqualTo("300000000000000000000000"));  // remainder
    }

    [Test]
    public void EqualCaps_TieBreakByAddressOrdinal_IsDeterministic()
    {
        // Equal balances: order must be deterministic by collateral address (ordinal), independent of
        // input order. ColA < ColB ordinally, so ColA must come first even when passed last.
        var caps = new[] { new Cap(ColB, Big("100")), new Cap(ColA, Big("100")) };
        var r = ScoreGroupRedeemPlanner.Plan(Big("150"), caps, Holder, Treasury);

        Assert.That(r.Transfers[0].TokenOwner, Is.EqualTo(ColA));
        Assert.That(r.Transfers[0].Value, Is.EqualTo("100"));
        Assert.That(r.Transfers[1].TokenOwner, Is.EqualTo(ColB));
        Assert.That(r.Transfers[1].Value, Is.EqualTo("50"));
    }

    // ── ParseRequest (input validation, DB-free) ──────────────────────────────

    [Test]
    public void ParseRequest_ValidInputs_NormalizesAndParses()
    {
        var req = ScoreGroupRedeemPlanner.ParseRequest("0xABCDEF0123456789abcdef0123456789ABCDEF01", Holder, "1000");
        Assert.That(req.Group, Is.EqualTo("0xabcdef0123456789abcdef0123456789abcdef01")); // lowercased
        Assert.That(req.Holder, Is.EqualTo(Holder));
        Assert.That(req.RequestedAmount, Is.EqualTo((BigInteger?)Big("1000")));
    }

    [Test]
    public void ParseRequest_NoAmount_NullCap()
    {
        Assert.That(ScoreGroupRedeemPlanner.ParseRequest(ColA, Holder, null).RequestedAmount, Is.Null);
        Assert.That(ScoreGroupRedeemPlanner.ParseRequest(ColA, Holder, "  ").RequestedAmount, Is.Null);
    }

    [Test]
    public void ParseRequest_ZeroAmount_ParsesZero()
    {
        Assert.That(ScoreGroupRedeemPlanner.ParseRequest(ColA, Holder, "0").RequestedAmount, Is.EqualTo((BigInteger?)BigInteger.Zero));
    }

    [Test]
    public void ParseRequest_WhitespaceAroundAmount_Trimmed()
    {
        Assert.That(ScoreGroupRedeemPlanner.ParseRequest(ColA, Holder, " 100 ").RequestedAmount, Is.EqualTo((BigInteger?)Big("100")));
    }

    [Test]
    public void ParseRequest_EmptyOrMissingAddresses_Throw()
    {
        Assert.Throws<ArgumentException>(() => ScoreGroupRedeemPlanner.ParseRequest("", Holder, null));
        Assert.Throws<ArgumentException>(() => ScoreGroupRedeemPlanner.ParseRequest(null, Holder, null));
        Assert.Throws<ArgumentException>(() => ScoreGroupRedeemPlanner.ParseRequest(ColA, "", null));
        Assert.Throws<ArgumentException>(() => ScoreGroupRedeemPlanner.ParseRequest(ColA, null, null));
    }

    [Test]
    public void ParseRequest_MalformedAddress_Throws()
    {
        Assert.Throws<ArgumentException>(() => ScoreGroupRedeemPlanner.ParseRequest("0x123", Holder, null));        // too short
        Assert.Throws<ArgumentException>(() => ScoreGroupRedeemPlanner.ParseRequest("notanaddress", Holder, null)); // no 0x / non-hex
        Assert.Throws<ArgumentException>(() => ScoreGroupRedeemPlanner.ParseRequest(
            "0xZZZZ567890123456789012345678901234567890", Holder, null));                                          // non-hex chars
        Assert.Throws<ArgumentException>(() => ScoreGroupRedeemPlanner.ParseRequest(
            "0x12345678901234567890123456789012345678901", Holder, null));                                         // too long (41)
    }

    [Test]
    public void ParseRequest_NegativeOrNonDecimalAmount_Throws()
    {
        Assert.Throws<ArgumentException>(() => ScoreGroupRedeemPlanner.ParseRequest(ColA, Holder, "-1"));   // negative
        Assert.Throws<ArgumentException>(() => ScoreGroupRedeemPlanner.ParseRequest(ColA, Holder, "+1"));   // leading sign
        Assert.Throws<ArgumentException>(() => ScoreGroupRedeemPlanner.ParseRequest(ColA, Holder, "1.5"));  // non-integer
        Assert.Throws<ArgumentException>(() => ScoreGroupRedeemPlanner.ParseRequest(ColA, Holder, "0x10")); // hex
        Assert.Throws<ArgumentException>(() => ScoreGroupRedeemPlanner.ParseRequest(ColA, Holder, "abc"));  // not a number
    }
}
