using Circles.Common.Dto;
using Circles.Pathfinder.Host;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Regression guard for the prod path-audit alert that paged from prod2/prod3 (2026-06-02).
///
/// Captured shape: the frontend ran <c>simulatedBalances</c> what-if previews from an UNREGISTERED
/// source (<c>0x85c158…</c>) to a registered sink. The injected balance let the solver build a
/// 2-step path, which then tripped Rule 9 (AvatarRegistration) — the source is not a registered
/// avatar, so the path would revert on-chain (<c>CirclesAvatarMustBeRegistered</c>). The safety net
/// correctly replaced it with the empty path, but the violation paged like a real pathfinder bug.
///
/// Fix (this PR): <see cref="FindPathHandler.RecordPathAuditViolation"/> tags violations with
/// <c>simulated="true"</c> when the request injected what-if state. The alert matches
/// <c>simulated!="true"</c> so previews no longer page, while real-traffic violations
/// (<c>simulated="false"</c>) still do. Detection of the unregistered source itself is covered by
/// <c>HubContractValidatorTests.Rule9_*</c>; this fixture guards the metric tagging that gates the
/// alert. Block-faithful replay of the exact request lives in the test-env Anvil suite (pinned at
/// block 46497740).
/// </summary>
[TestFixture]
public class PathAuditSimulatedMetricTests
{
    private static double Violations(string rule, string simulated) =>
        FindPathMetrics.PathAuditViolationsTotal.WithLabels(rule, simulated).Value;

    private static double Blocked(string rule, string simulated) =>
        FindPathMetrics.PathAuditBlockedTotal.WithLabels(rule, simulated).Value;

    private static MaxFlowResponse Rejected(params string[] rules) =>
        new("0", new List<TransferPathStep>())
        {
            ValidationErrors = rules.Length,
            ValidationViolationRules = rules,
        };

    [Test]
    public void SimulatedRequest_AvatarRegistration_TaggedTrue_DoesNotTouchAlertingSeries()
    {
        var mfr = Rejected("AvatarRegistration");

        double trueBefore = Violations("AvatarRegistration", "true");
        double falseBefore = Violations("AvatarRegistration", "false");
        double anyTrueBefore = Violations("any", "true");
        double blockedTrueBefore = Blocked("AvatarRegistration", "true");

        FindPathHandler.RecordPathAuditViolation(mfr, simulated: true);

        Assert.That(Violations("AvatarRegistration", "true"), Is.EqualTo(trueBefore + 1),
            "the what-if preview must increment the simulated series");
        Assert.That(Violations("AvatarRegistration", "false"), Is.EqualTo(falseBefore),
            "the alerting series (simulated=\"false\") must NOT move for a preview — this is what stops the page");
        Assert.That(Violations("any", "true"), Is.EqualTo(anyTrueBefore + 1),
            "the aggregate 'any' series carries the simulated tag too");
        Assert.That(Blocked("AvatarRegistration", "true"), Is.EqualTo(blockedTrueBefore + 1),
            "blocked tracks violations 1:1 (the safety net always replaces a violating response)");
    }

    [Test]
    public void RealRequest_Violation_TaggedFalse_StillPages()
    {
        var mfr = Rejected("FlowConservation");

        double falseBefore = Violations("FlowConservation", "false");
        double trueBefore = Violations("FlowConservation", "true");

        FindPathHandler.RecordPathAuditViolation(mfr, simulated: false);

        Assert.That(Violations("FlowConservation", "false"), Is.EqualTo(falseBefore + 1),
            "a real-traffic violation increments the alerting series — a genuine pathfinder bug must still page");
        Assert.That(Violations("FlowConservation", "true"), Is.EqualTo(trueBefore),
            "the simulated series stays put for real traffic");
    }

    [Test]
    public void MultipleRules_EachTaggedAndBlockedOneToOne()
    {
        var mfr = Rejected("AvatarRegistration", "IsPermittedFlow");

        double regBefore = Violations("AvatarRegistration", "true");
        double permBefore = Violations("IsPermittedFlow", "true");
        double regBlockedBefore = Blocked("AvatarRegistration", "true");
        double permBlockedBefore = Blocked("IsPermittedFlow", "true");

        FindPathHandler.RecordPathAuditViolation(mfr, simulated: true);

        Assert.That(Violations("AvatarRegistration", "true"), Is.EqualTo(regBefore + 1));
        Assert.That(Violations("IsPermittedFlow", "true"), Is.EqualTo(permBefore + 1));
        Assert.That(Blocked("AvatarRegistration", "true"), Is.EqualTo(regBlockedBefore + 1));
        Assert.That(Blocked("IsPermittedFlow", "true"), Is.EqualTo(permBlockedBefore + 1));
    }

    [Test]
    public void ValidatorException_NullRules_TagsAnyAndException_NoCrash()
    {
        // The validator-exception path may set ValidatorException without populating the rule list.
        var mfr = new MaxFlowResponse("0", new List<TransferPathStep>())
        {
            ValidatorException = true,
            ValidationViolationRules = null,
        };

        double anyBefore = Violations("any", "false");
        double exBefore = Violations("ValidatorException", "false");

        Assert.DoesNotThrow(() => FindPathHandler.RecordPathAuditViolation(mfr, simulated: false));

        Assert.That(Violations("any", "false"), Is.EqualTo(anyBefore + 1));
        Assert.That(Violations("ValidatorException", "false"), Is.EqualTo(exBefore + 1));
    }

    [Test]
    public void SimulatedRequest_IntegrityRule_StaysFalse_StillPages()
    {
        // The load-bearing guard: a solver-integrity rule (FlowConservation) validates the path the
        // solver emitted and is a real pathfinder bug regardless of injected what-if state. It must
        // NOT be excused even on a simulated request — otherwise a genuine bug riding on a frontend
        // what-if call would be silenced.
        var mfr = Rejected("FlowConservation");

        double falseBefore = Violations("FlowConservation", "false");
        double trueBefore = Violations("FlowConservation", "true");
        double anyFalseBefore = Violations("any", "false");

        FindPathHandler.RecordPathAuditViolation(mfr, simulated: true);

        Assert.That(Violations("FlowConservation", "false"), Is.EqualTo(falseBefore + 1),
            "an integrity-rule violation pages even when the request was simulated");
        Assert.That(Violations("FlowConservation", "true"), Is.EqualTo(trueBefore),
            "integrity rules are never tagged simulated=\"true\"");
        Assert.That(Violations("any", "false"), Is.EqualTo(anyFalseBefore + 1),
            "'any' stays simulated=\"false\" so the integrity bug pages");
    }

    [Test]
    public void SimulatedRequest_MixedExcusableAndIntegrity_AnyStaysFalse()
    {
        // Mixed rejection: AvatarRegistration (excusable) + CollateralBeforeMint (integrity).
        var mfr = Rejected("AvatarRegistration", "CollateralBeforeMint");

        double regTrueBefore = Violations("AvatarRegistration", "true");
        double collFalseBefore = Violations("CollateralBeforeMint", "false");
        double anyFalseBefore = Violations("any", "false");
        double anyTrueBefore = Violations("any", "true");

        FindPathHandler.RecordPathAuditViolation(mfr, simulated: true);

        Assert.That(Violations("AvatarRegistration", "true"), Is.EqualTo(regTrueBefore + 1),
            "the excusable rule is still tagged simulated=\"true\"");
        Assert.That(Violations("CollateralBeforeMint", "false"), Is.EqualTo(collFalseBefore + 1),
            "the integrity rule stays simulated=\"false\"");
        Assert.That(Violations("any", "false"), Is.EqualTo(anyFalseBefore + 1),
            "a mixed rejection keeps 'any' simulated=\"false\" so it still pages");
        Assert.That(Violations("any", "true"), Is.EqualTo(anyTrueBefore),
            "'any' is NOT excused when an integrity rule is present");
    }

    [Test]
    public void SimulatedRequest_ValidatorException_NeverExcused()
    {
        var mfr = new MaxFlowResponse("0", new List<TransferPathStep>())
        {
            ValidatorException = true,
        };

        double exFalseBefore = Violations("ValidatorException", "false");
        double exTrueBefore = Violations("ValidatorException", "true");
        double anyFalseBefore = Violations("any", "false");

        FindPathHandler.RecordPathAuditViolation(mfr, simulated: true);

        Assert.That(Violations("ValidatorException", "false"), Is.EqualTo(exFalseBefore + 1),
            "a validator exception is an audit-layer bug — never excused, always pages");
        Assert.That(Violations("ValidatorException", "true"), Is.EqualTo(exTrueBefore));
        Assert.That(Violations("any", "false"), Is.EqualTo(anyFalseBefore + 1),
            "'any' pages when a validator exception is present, even on a simulated request");
    }
}
