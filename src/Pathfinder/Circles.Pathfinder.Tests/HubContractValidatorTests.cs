using Circles.Common.Dto;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Validation;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for HubContractValidator — each rule tested in isolation
/// with positive, negative, and edge cases.
/// </summary>
[TestFixture, Parallelizable]
public class HubContractValidatorTests
{
    // Standard test addresses (valid 42-char hex)
    private const string Alice = "0x0000000000000000000000000000000000000001";
    private const string Bob = "0x0000000000000000000000000000000000000002";
    private const string Carol = "0x0000000000000000000000000000000000000003";
    private const string Dave = "0x0000000000000000000000000000000000000004";
    private const string Router = "0x0000000000000000000000000000000000000099";
    private const string ScoreRouter = "0x0000000000000000000000000000000000000098";
    private const string GroupA = "0x00000000000000000000000000000000000000a1";
    private const string GroupB = "0x00000000000000000000000000000000000000a2";

    #region Helpers

    private static TransferPathStep Step(string from, string to, string tokenOwner, string value = "1000000000000000000")
        => new() { From = from, To = to, TokenOwner = tokenOwner, Value = value };

    private static MockContractState DefaultState() => new()
    {
        Router = Router,
        TrustAll = true, // Default: everything trusted (tests that need distrust override)
    };

    /// <summary>Minimal mock implementing IContractState for isolated rule testing.</summary>
    private class MockContractState : IContractState
    {
        public string? Router { get; set; }
        public HashSet<string> Routers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ScoreRouters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Groups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Consented { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(string Truster, string CirclesId)> Trusts { get; set; } = new();
        public bool TrustAll { get; set; }
        public HashSet<string> Registered { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool RegisterAll { get; set; } = true; // Default: all considered registered

        // Keys are lowercased (group, collateral) tuples. Null entries simulate "not cached"
        // for Rule 12's fail-closed branch — distinct from "absent key" which returns null too,
        // both mapping to GetScoreGroupMintLimit() == null at the interface level.
        public Dictionary<(string Group, string Collateral), long> ScoreGroupMintLimits { get; set; }
            = new();

        // (account, operator) tuples lowercased. Membership grants approval — but only when
        // StrictApprovals is true. Default behavior is permissive (returns true regardless),
        // matching the IContractState default so existing tests don't have to seed anything.
        public HashSet<(string Account, string Operator)> Approvals { get; set; } = new();
        public bool StrictApprovals { get; set; }

        public string? RouterAddress => Router;
        public bool IsRouter(string address) =>
            (Router != null && string.Equals(Router, address, StringComparison.OrdinalIgnoreCase))
            || Routers.Contains(address)
            || ScoreRouters.Contains(address);
        public bool IsScoreRouter(string address) => ScoreRouters.Contains(address);
        public bool IsGroup(string address) => Groups.Contains(address);
        public bool HasAdvancedUsageFlags(string address) => Consented.Contains(address);
        public bool IsRegistered(string address) => RegisterAll || Registered.Contains(address);
        public Dictionary<string, string> Wrappers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsWrapperToken(string address) => Wrappers.ContainsKey(address);
        public string? ResolveWrapperToAvatar(string a) => Wrappers.TryGetValue(a, out var v) ? v : null;

        public bool IsTrusted(string truster, string circlesId)
        {
            if (TrustAll) return true;
            return Trusts.Contains((truster.ToLowerInvariant(), circlesId.ToLowerInvariant()));
        }

        public long? GetScoreGroupMintLimit(string group, string collateral)
        {
            return ScoreGroupMintLimits.TryGetValue(
                (group.ToLowerInvariant(), collateral.ToLowerInvariant()), out var limit)
                ? limit
                : null;
        }

        public bool IsApprovedForAll(string account, string @operator)
        {
            if (!StrictApprovals) return true;
            return Approvals.Contains((account.ToLowerInvariant(), @operator.ToLowerInvariant()));
        }
    }

    #endregion

    // ═══════════════════════════════════════════
    // Rule 1: NoZeroFlow
    // ═══════════════════════════════════════════

    [Test]
    public void Rule1_ValidFlow_Passes()
    {
        var steps = new[] { Step(Alice, Bob, Alice, "1000") };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());
        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public void Rule1_ZeroFlow_FailsWithError()
    {
        var steps = new[] { Step(Alice, Bob, Alice, "0") };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());
        Assert.That(result.Violations.Any(v => v.Rule == "NoZeroFlow" && v.Severity == "error"), Is.True);
        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void Rule1_EmptyValue_FailsWithError()
    {
        var steps = new[] { Step(Alice, Bob, Alice, "") };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());
        Assert.That(result.Violations.Any(v => v.Rule == "NoZeroFlow"), Is.True);
    }

    // ═══════════════════════════════════════════
    // Rule 2: AddressFormat
    // ═══════════════════════════════════════════

    [Test]
    public void Rule2_ValidAddresses_Passes()
    {
        var steps = new[] { Step(Alice, Bob, Alice) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());
        Assert.That(result.Violations.Any(v => v.Rule == "AddressFormat"), Is.False);
    }

    [Test]
    public void Rule2_ShortAddress_FailsWithError()
    {
        var steps = new[] { Step("0x1234", Bob, Alice) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());
        Assert.That(result.Violations.Any(v => v.Rule == "AddressFormat" && v.Message.Contains("From")), Is.True);
    }

    [Test]
    public void Rule2_NullAddress_FailsWithError()
    {
        var steps = new[] { Step("", Bob, Alice) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());
        Assert.That(result.Violations.Any(v => v.Rule == "AddressFormat"), Is.True);
    }

    [Test]
    public void Rule2_MissingPrefix_FailsWithError()
    {
        var badAddr = "000000000000000000000000000000000000000001";
        var steps = new[] { Step(badAddr, Bob, Alice) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());
        Assert.That(result.Violations.Any(v => v.Rule == "AddressFormat"), Is.True);
    }

    // ═══════════════════════════════════════════
    // Rule 3: VertexOrdering
    // ═══════════════════════════════════════════

    [Test]
    public void Rule3_ValidAddresses_CanBeSorted()
    {
        var steps = new[] { Step(Alice, Bob, Carol) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());
        Assert.That(result.Violations.Any(v => v.Rule == "VertexOrdering"), Is.False);
    }

    [Test]
    public void Rule3_UnparsableHex_DelegatedToRule2()
    {
        // Non-hex characters are caught by Rule 2 (AddressFormat), not Rule 3 (VertexOrdering).
        // VertexOrdering skips addresses that can't be parsed.
        var violations = new List<ValidationViolation>();
        var steps = new[] { Step("0xZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ", Bob, Alice) };
        HubContractValidator.ValidateVertexOrdering(steps, Alice, Bob, violations);
        Assert.That(violations.Any(v => v.Rule == "VertexOrdering"), Is.False,
            "Non-hex addresses should be caught by AddressFormat, not VertexOrdering");
    }

    [Test]
    public void Rule2_NonHexChars_CaughtByAddressFormat()
    {
        // Verify the full validator catches non-hex characters via Rule 2
        var steps = new[] { Step("0xZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ", Bob, Alice) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());
        Assert.That(result.Violations.Any(v => v.Rule == "AddressFormat" && v.Message.Contains("non-hex")), Is.True);
    }

    // ═══════════════════════════════════════════
    // Rule 4: IsPermittedFlow
    // ═══════════════════════════════════════════

    [Test]
    public void Rule4_StandardFlow_Trusted_Passes()
    {
        var state = new MockContractState
        {
            Router = Router,
            Trusts = { (Bob.ToLower(), Alice.ToLower()) } // Bob trusts Alice's token
        };
        var steps = new[] { Step(Alice, Bob, Alice) }; // Alice sends Alice-token to Bob
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, source: "", state, violations);
        Assert.That(violations, Is.Empty);
    }

    [Test]
    public void Rule4_StandardFlow_NotTrusted_Fails()
    {
        var state = new MockContractState { Router = Router }; // No trusts, TrustAll=false
        var steps = new[] { Step(Alice, Bob, Alice) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, source: "", state, violations);
        Assert.That(violations.Count, Is.EqualTo(1));
        Assert.That(violations[0].Rule, Is.EqualTo("IsPermittedFlow"));
    }

    [Test]
    public void Rule4_ConsentedFlow_BothConsentedAndTrusted_Passes()
    {
        var state = new MockContractState
        {
            Router = Router,
            Consented = { Alice, Bob },
            Trusts = { (Alice.ToLower(), Bob.ToLower()) } // Alice trusts Bob (isTrusted(_from, _to))
        };
        var steps = new[] { Step(Alice, Bob, Carol) }; // Consented: checks From trusts To
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, source: "", state, violations);
        Assert.That(violations, Is.Empty);
    }

    [Test]
    public void Rule4_ConsentedFlow_FromConsentedButToNot_Fails()
    {
        var state = new MockContractState
        {
            Router = Router,
            Consented = { Alice }, // Only Alice has consent, Bob doesn't
            Trusts = { (Alice.ToLower(), Bob.ToLower()) }
        };
        var steps = new[] { Step(Alice, Bob, Carol) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, source: "", state, violations);
        Assert.That(violations.Any(v => v.Message.Contains("advancedUsageFlags")), Is.True);
    }

    [Test]
    public void Rule4_ConsentedFlow_FromDoesNotTrustTo_Fails()
    {
        var state = new MockContractState
        {
            Router = Router,
            Consented = { Alice, Bob },
            // No trust relationship
        };
        var steps = new[] { Step(Alice, Bob, Carol) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, source: "", state, violations);
        Assert.That(violations.Any(v => v.Message.Contains("does not trust")), Is.True);
    }

    [Test]
    public void Rule4_RouterToGroup_BypassesCheck()
    {
        var state = new MockContractState
        {
            Router = Router,
            Groups = { GroupA },
        };
        var steps = new[] { Step(Router, GroupA, Alice) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, source: "", state, violations);
        Assert.That(violations, Is.Empty, "Router→Group should bypass isPermittedFlow (internal group mint)");
    }

    [Test]
    public void Rule4_AvatarToRouter_Trusted_Passes()
    {
        var state = new MockContractState
        {
            Router = Router,
            Trusts = { (Router, Alice) }, // Router trusts Alice's token
        };
        var steps = new[] { Step(Alice, Router, Alice) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, source: "", state, violations);
        Assert.That(violations, Is.Empty, "Avatar→Router should pass when Router trusts tokenOwner");
    }

    [Test]
    public void Rule4_AvatarToRouter_Untrusted_Fails()
    {
        var state = new MockContractState
        {
            Router = Router,
            // No trusts — Router does NOT trust Alice's token
        };
        var steps = new[] { Step(Alice, Router, Alice) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, source: "", state, violations);
        Assert.That(violations, Has.Count.EqualTo(1));
        Assert.That(violations[0].Message, Does.Contain("Avatar→Router"));
        Assert.That(violations[0].Message, Does.Contain("does not trust token owner"));
    }

    [Test]
    public void Rule4_RouterToNonGroup_FallsThrough_StandardValidation()
    {
        var state = new MockContractState
        {
            Router = Router,
            // Alice is NOT a group — Router→Alice falls through to standard isPermittedFlow
            // Standard: isTrusted(to=Alice, tokenOwner=Bob)
            Trusts = { (Alice.ToLowerInvariant(), Bob.ToLowerInvariant()) },
        };
        var steps = new[] { Step(Router, Alice, Bob) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, source: "", state, violations);
        Assert.That(violations, Is.Empty, "Router→NonGroup should use standard validation and pass when trusted");
    }

    [Test]
    public void Rule4_RouterToNonGroup_Untrusted_Fails()
    {
        var state = new MockContractState
        {
            Router = Router,
            // Alice is NOT a group, Alice does NOT trust Bob's token
        };
        var steps = new[] { Step(Router, Alice, Bob) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, source: "", state, violations);
        Assert.That(violations, Has.Count.EqualTo(1), "Router→NonGroup with no trust should fail standard validation");
    }

    // ═══════════════════════════════════════════
    // Rule: ApproveCRCRequired (Avatar→ScoreRouter requires ERC-1155 operator approval)
    // ═══════════════════════════════════════════

    [Test]
    public void ApproveCRCRequired_ScoreRouterEdge_WithApproval_DoesNotEmit()
    {
        var state = new MockContractState
        {
            Router = Router,
            ScoreRouters = { ScoreRouter },
            Trusts = { (ScoreRouter.ToLowerInvariant(), Alice.ToLowerInvariant()) },
            Approvals = { (ScoreRouter.ToLowerInvariant(), Alice.ToLowerInvariant()) },
        };
        var steps = new[] { Step(Alice, ScoreRouter, Alice) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, source: Alice, state, violations);
        Assert.That(violations.Any(v => v.Rule == "ApproveCRCRequired"), Is.False,
            "ApproveCRCRequired must not fire when ScoreRouter has approved source");
    }

    [Test]
    public void ApproveCRCRequired_ScoreRouterEdge_WithoutApproval_Emits()
    {
        var state = new MockContractState
        {
            Router = Router,
            ScoreRouters = { ScoreRouter },
            Trusts = { (ScoreRouter.ToLowerInvariant(), Alice.ToLowerInvariant()) },
            StrictApprovals = true,
        };
        var steps = new[] { Step(Alice, ScoreRouter, Alice) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, source: Alice, state, violations);
        var violation = violations.SingleOrDefault(v => v.Rule == "ApproveCRCRequired");
        Assert.That(violation, Is.Not.Null, "ApproveCRCRequired must fire when ScoreRouter has no approval for source");
        Assert.That(violation!.Message, Does.Contain("setApprovalForCRC"),
            "Message should guide the SDK to call setApprovalForCRC");
    }

    [Test]
    public void ApproveCRCRequired_NonScoreRouterEdge_NoEmission_EvenWithoutApproval()
    {
        var state = new MockContractState
        {
            Router = Router,
            // Router is the standard router but NOT a score router.
            Trusts = { (Router, Alice.ToLowerInvariant()) },
            StrictApprovals = true, // Alice not in Approvals → unapproved, but Router is not a score router.
        };
        var steps = new[] { Step(Alice, Router, Alice) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, source: Alice, state, violations);
        Assert.That(violations.Any(v => v.Rule == "ApproveCRCRequired"), Is.False,
            "ApproveCRCRequired is scoped to score-router edges; standard routers must not trip it");
    }

    // ═══════════════════════════════════════════
    // Rule 5: FlowConservation
    // ═══════════════════════════════════════════

    [Test]
    public void Rule5_ConservedFlow_Passes()
    {
        // A→B(100), B→C(100) — B is intermediate with net=0
        var steps = new[]
        {
            Step(Alice, Bob, Alice, "100"),
            Step(Bob, Carol, Bob, "100"),
        };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateFlowConservation(steps, Alice, Carol, violations);
        Assert.That(violations, Is.Empty);
    }

    [Test]
    public void Rule5_UnconservedFlow_Fails()
    {
        // A→B(100), B→C(50) — B leaks 50
        var steps = new[]
        {
            Step(Alice, Bob, Alice, "100"),
            Step(Bob, Carol, Bob, "50"),
        };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateFlowConservation(steps, Alice, Carol, violations);
        Assert.That(violations.Count, Is.EqualTo(1));
        Assert.That(violations[0].Rule, Is.EqualTo("FlowConservation"));
    }

    [Test]
    public void Rule5_SourceAndSinkExcluded()
    {
        // Single edge: A→B(100) — source and sink aren't checked
        var steps = new[] { Step(Alice, Bob, Alice, "100") };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateFlowConservation(steps, Alice, Bob, violations);
        Assert.That(violations, Is.Empty);
    }

    [Test]
    public void Rule5_RouterAsPassthrough_Conserved()
    {
        // A→Router(100), Router→Group(100) — Router net=0
        var steps = new[]
        {
            Step(Alice, Router, Alice, "100"),
            Step(Router, GroupA, Alice, "100"),
        };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateFlowConservation(steps, Alice, GroupA, violations);
        Assert.That(violations, Is.Empty);
    }

    // ═══════════════════════════════════════════
    // Rule 6: CollateralBeforeMint
    // ═══════════════════════════════════════════

    [Test]
    public void Rule6_CorrectOrder_CollateralThenMint_Passes()
    {
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            Groups = { GroupA },
        };
        var steps = new[]
        {
            Step(Alice, Router, Alice, "100"),  // Collateral handoff
            Step(Router, GroupA, Alice, "100"),  // Collateral deposit
            Step(GroupA, Bob, GroupA, "100"),     // Mint
        };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateCollateralBeforeMint(steps, state, violations);
        Assert.That(violations, Is.Empty);
    }

    [Test]
    public void Rule6_ReversedOrder_MintBeforeCollateral_Fails()
    {
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            Groups = { GroupA },
        };
        var steps = new[]
        {
            Step(GroupA, Bob, GroupA, "100"),     // Mint FIRST — wrong!
            Step(Router, GroupA, Alice, "100"),  // Collateral AFTER — too late
        };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateCollateralBeforeMint(steps, state, violations);
        // Should have two violations: insufficient collateral at mint + collateral after outbound
        Assert.That(violations.Any(v => v.Rule == "CollateralBeforeMint"), Is.True);
    }

    [Test]
    public void Rule6_ExactBalance_Passes()
    {
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            Groups = { GroupA },
        };
        var steps = new[]
        {
            Step(Router, GroupA, Alice, "50"),
            Step(Router, GroupA, Bob, "50"),
            Step(GroupA, Carol, GroupA, "100"), // Exactly matches 50+50
        };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateCollateralBeforeMint(steps, state, violations);
        Assert.That(violations, Is.Empty);
    }

    [Test]
    public void Rule6_InsufficientCollateral_Fails()
    {
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            Groups = { GroupA },
        };
        var steps = new[]
        {
            Step(Router, GroupA, Alice, "50"),    // Only 50 collateral
            Step(GroupA, Carol, GroupA, "100"),   // Needs 100 — short by 50
        };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateCollateralBeforeMint(steps, state, violations);
        Assert.That(violations.Count, Is.EqualTo(1));
        Assert.That(violations[0].Message, Does.Contain("insufficient collateral"));
    }

    [Test]
    public void Rule6_NoRouter_Skips()
    {
        var state = new MockContractState { Groups = { GroupA } }; // No router
        var steps = new[] { Step(GroupA, Bob, GroupA, "100") };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateCollateralBeforeMint(steps, state, violations);
        Assert.That(violations, Is.Empty, "No router means no group minting validation");
    }

    [Test]
    public void Rule6_MultipleGroups_IndependentTracking()
    {
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            Groups = { GroupA, GroupB },
        };
        var steps = new[]
        {
            Step(Router, GroupA, Alice, "100"),
            Step(Router, GroupB, Bob, "200"),
            Step(GroupA, Carol, GroupA, "100"),   // GroupA: 100 in, 100 out ✓
            Step(GroupB, Dave, GroupB, "200"),   // GroupB: 200 in, 200 out ✓
        };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateCollateralBeforeMint(steps, state, violations);
        Assert.That(violations, Is.Empty);
    }

    [Test]
    public void Rule6_GroupSpecificScoreRouterCountsAsCollateral()
    {
        var state = new MockContractState
        {
            Router = Router,
            Routers = { ScoreRouter },
            TrustAll = true,
            Groups = { GroupA },
        };
        var steps = new[]
        {
            Step(Alice, ScoreRouter, Alice, "100"),
            Step(ScoreRouter, GroupA, Alice, "100"),
            Step(GroupA, Bob, GroupA, "100"),
        };

        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateCollateralBeforeMint(steps, state, violations);

        Assert.That(violations, Is.Empty,
            "A group-specific score router must satisfy the same collateral-before-mint rule as the base router.");
    }

    // ═══════════════════════════════════════════
    // Rule 7: NoDuplicateEdges
    // ═══════════════════════════════════════════

    [Test]
    public void Rule7_UniqueEdges_Passes()
    {
        var steps = new[]
        {
            Step(Alice, Bob, Alice, "100"),
            Step(Alice, Carol, Alice, "200"),
        };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateNoDuplicateEdges(steps, violations);
        Assert.That(violations, Is.Empty);
    }

    [Test]
    public void Rule7_DuplicateEdge_WarningNotError()
    {
        var steps = new[]
        {
            Step(Alice, Bob, Alice, "100"),
            Step(Alice, Bob, Alice, "200"), // Same (from, to, token) — duplicate
        };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateNoDuplicateEdges(steps, violations);
        Assert.That(violations.Count, Is.EqualTo(1));
        Assert.That(violations[0].Severity, Is.EqualTo("warning"));
    }

    [Test]
    public void Rule7_SameFromTo_DifferentToken_NotDuplicate()
    {
        var steps = new[]
        {
            Step(Alice, Bob, Alice, "100"),
            Step(Alice, Bob, Carol, "200"), // Different token owner
        };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateNoDuplicateEdges(steps, violations);
        Assert.That(violations, Is.Empty);
    }

    // ═══════════════════════════════════════════
    // Rule 8: NoSelfTransfers
    // ═══════════════════════════════════════════

    [Test]
    public void Rule8_NormalTransfer_Passes()
    {
        var steps = new[] { Step(Alice, Bob, Alice) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateNoSelfTransfers(steps, Bob, violations);
        Assert.That(violations, Is.Empty);
    }

    [Test]
    public void Rule8_SelfTransfer_WarningNotError()
    {
        var steps = new[] { Step(Alice, Alice, Alice) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateNoSelfTransfers(steps, Bob, violations);
        Assert.That(violations.Count, Is.EqualTo(1));
        Assert.That(violations[0].Severity, Is.EqualTo("warning"));
    }

    [Test]
    public void Rule8_SinkSelfLoop_FilteredBeforeValidation()
    {
        // Sink→Sink self-loops are filtered out by Validate() before rules run
        var steps = new[]
        {
            Step(Alice, Bob, Alice),
            Step(Bob, Bob, Alice), // Sink self-loop (display-only)
        };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());
        // The self-loop at Bob (sink) should be filtered out, so no warning
        Assert.That(result.Violations.Any(v => v.Rule == "NoSelfTransfers"), Is.False);
    }

    // ═══════════════════════════════════════════
    // Edge Cases
    // ═══════════════════════════════════════════

    [Test]
    public void EmptyPath_IsValid()
    {
        var result = HubContractValidator.Validate(
            Array.Empty<TransferPathStep>(), Alice, Bob, DefaultState());
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Violations, Is.Empty);
    }

    [Test]
    public void SingleEdge_AllRulesPass()
    {
        var steps = new[] { Step(Alice, Bob, Alice) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());
        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public void WarningOnly_StillValid()
    {
        // Duplicate edges produce warnings but not errors
        var steps = new[]
        {
            Step(Alice, Bob, Alice, "50"),
            Step(Alice, Bob, Alice, "50"),
        };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());
        // Warnings exist but IsValid should still be true (only errors make it invalid)
        Assert.That(result.Violations.Any(v => v.Severity == "warning"), Is.True);
        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public void MultipleViolations_AllReported()
    {
        // Edge with zero flow AND invalid address — should catch both
        var steps = new[] { Step("0x1234", Bob, Alice, "0") };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());
        var errorRules = result.Violations.Where(v => v.Severity == "error").Select(v => v.Rule).ToHashSet();
        Assert.That(errorRules, Does.Contain("NoZeroFlow"));
        Assert.That(errorRules, Does.Contain("AddressFormat"));
    }

    // ═══════════════════════════════════════════
    // Property: Quantized + Groups → All 8 Rules Pass
    // ═══════════════════════════════════════════

    /// <summary>
    /// Regression: random quantized graphs with groups validated against all 8 Hub.sol rules.
    /// Covers the intersection of Bug #3 (self-loop), collateral ordering, and flow conservation.
    /// If any quantization or group minting logic regresses, this catches it via the validator.
    /// </summary>
    [Test]
    [Repeat(15)]
    public void PropertyBased_QuantizedGraphWithGroups_PassesHubContractValidator()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 2000);
        int avatars = rng.Next(4, 8);
        int groups = rng.Next(1, 3);

        var graph = PropertyBasedTests.BuildSyntheticGraph(avatars, groups,
            100_000_000L, rng, trustDensity: 0.5, withRouter: true);
        var (source, sink) = PropertyBasedTests.PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var sourceAddr = AddressIdPool.StringOf(source);
        var sinkAddr = AddressIdPool.StringOf(sink);
        var request = new Circles.Common.Dto.FlowRequest
        {
            Source = sourceAddr,
            Sink = sinkAddr,
            QuantizedMode = true,
        };
        var target = Circles.Common.CirclesConverter.BlowUpToUInt256(100_000_000L);

        Circles.Common.Dto.MaxFlowResponse result;
        try
        {
            result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("BAD_INPUT"))
        {
            return; // Empty graph — valid
        }

        if (result.Transfers.Count == 0)
            return;

        var state = new CapacityGraphContractState(graph);
        var validation = HubContractValidator.Validate(result.Transfers, sourceAddr, sinkAddr, state);

        var errors = validation.Violations
            .Where(v => v.Severity == "error")
            .Select(v => $"  [{v.Rule}] {v.Message}")
            .ToList();

        Assert.That(errors, Is.Empty,
            $"Quantized+groups graph ({avatars} avatars, {groups} groups) validator errors:\n{string.Join("\n", errors)}");
    }

    // ═══════════════════════════════════════════
    // Rule 9: AvatarRegistration
    // ═══════════════════════════════════════════

    [Test]
    public void Rule9_AllRegistered_Passes()
    {
        var state = DefaultState();
        state.RegisterAll = false;
        state.Registered.Add(Alice);
        state.Registered.Add(Bob);
        state.Registered.Add(Carol);

        var steps = new[] { Step(Alice, Bob, Alice), Step(Bob, Carol, Bob) };
        var result = HubContractValidator.Validate(steps, Alice, Carol, state);

        var regErrors = result.Violations.Where(v => v.Rule == "AvatarRegistration").ToList();
        Assert.That(regErrors, Is.Empty);
    }

    [Test]
    public void Rule9_UnregisteredFrom_FailsWithError()
    {
        var state = DefaultState();
        state.RegisterAll = false;
        state.Registered.Add(Alice);
        state.Registered.Add(Bob); // Carol not registered

        var steps = new[] { Step(Alice, Bob, Alice), Step(Carol, Bob, Carol) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        var regErrors = result.Violations.Where(v => v.Rule == "AvatarRegistration" && v.Severity == "error").ToList();
        Assert.That(regErrors, Has.Count.GreaterThanOrEqualTo(1));
        Assert.That(regErrors.Any(v => v.Message.Contains(Carol[..10])), Is.True);
    }

    [Test]
    public void Rule9_UnregisteredTo_FailsWithError()
    {
        var state = DefaultState();
        state.RegisterAll = false;
        state.Registered.Add(Alice);
        // Bob not registered

        var steps = new[] { Step(Alice, Bob, Alice) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        var regErrors = result.Violations.Where(v => v.Rule == "AvatarRegistration" && v.Severity == "error").ToList();
        Assert.That(regErrors, Has.Count.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Rule9_UnregisteredSourceSink_FailsWithError()
    {
        var state = DefaultState();
        state.RegisterAll = false;
        state.Registered.Add(Bob); // Alice and Carol not registered

        var steps = new[] { Step(Alice, Bob, Alice), Step(Bob, Carol, Bob) };
        var result = HubContractValidator.Validate(steps, Alice, Carol, state);

        var regErrors = result.Violations.Where(v => v.Rule == "AvatarRegistration" && v.Severity == "error").ToList();
        Assert.That(regErrors, Has.Count.GreaterThanOrEqualTo(2), "Both source and sink should be flagged");
    }

    [Test]
    public void Rule9_RouterExemptFromRegistrationCheck()
    {
        var state = DefaultState();
        state.RegisterAll = false;
        state.Registered.Add(Alice);
        state.Registered.Add(Bob);
        state.Registered.Add(GroupA);
        state.Groups.Add(GroupA);
        // Router NOT in Registered — but should be exempt

        var steps = new[]
        {
            Step(Alice, Router, Alice),
            Step(Router, GroupA, Alice),
            Step(GroupA, Bob, GroupA),
        };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        var regErrors = result.Violations.Where(v => v.Rule == "AvatarRegistration").ToList();
        Assert.That(regErrors, Is.Empty, "Router should be exempt from registration check");
    }

    // ═══════════════════════════════════════════
    // Rule 10: GroupRegistration
    // ═══════════════════════════════════════════

    [Test]
    public void Rule10_ValidGroupMint_Passes()
    {
        var state = DefaultState();
        state.Groups.Add(GroupA);

        var steps = new[]
        {
            Step(Alice, Router, Alice),
            Step(Router, GroupA, Alice),  // collateral
            Step(GroupA, Bob, GroupA),     // mint
        };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        var groupErrors = result.Violations.Where(v => v.Rule == "GroupRegistration").ToList();
        Assert.That(groupErrors, Is.Empty);
    }

    [Test]
    public void Rule10_NonGroupInMintPattern_FailsWithError()
    {
        var state = DefaultState();
        // Carol is NOT a group, but appears in group-mint pattern
        state.RegisterAll = true;

        var steps = new[]
        {
            Step(Alice, Router, Alice),
            Step(Router, Carol, Alice),   // "collateral" to non-group
            Step(Carol, Bob, Carol),      // Carol sends own token (like a group mint)
        };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        var groupErrors = result.Violations.Where(v => v.Rule == "GroupRegistration" && v.Severity == "error").ToList();
        Assert.That(groupErrors, Has.Count.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Rule10_NormalTransfer_NotFlagged()
    {
        var state = DefaultState();

        // Simple A→B transfer — no router, no group pattern
        var steps = new[] { Step(Alice, Bob, Alice) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        var groupErrors = result.Violations.Where(v => v.Rule == "GroupRegistration").ToList();
        Assert.That(groupErrors, Is.Empty);
    }

    [Test]
    public void Rule10_NoRouter_Skips()
    {
        var state = new MockContractState { Router = null, TrustAll = true };

        var steps = new[] { Step(Alice, Bob, Alice) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        var groupErrors = result.Violations.Where(v => v.Rule == "GroupRegistration").ToList();
        Assert.That(groupErrors, Is.Empty, "No router means no group minting possible");
    }

    // ═══════════════════════════════════════════
    // Rule 11: TokenIdValidity
    // ═══════════════════════════════════════════

    [Test]
    public void Rule11_RegisteredTokenOwner_Passes()
    {
        var state = DefaultState();
        state.RegisterAll = false;
        state.Registered.Add(Alice);
        state.Registered.Add(Bob);

        var steps = new[] { Step(Alice, Bob, Alice) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        var tokenErrors = result.Violations.Where(v => v.Rule == "TokenIdValidity").ToList();
        Assert.That(tokenErrors, Is.Empty);
    }

    [Test]
    public void Rule11_UnregisteredTokenOwner_FailsWithError()
    {
        var state = DefaultState();
        state.RegisterAll = false;
        state.Registered.Add(Alice);
        state.Registered.Add(Bob);
        // Carol not registered — used as TokenOwner

        var steps = new[] { Step(Alice, Bob, Carol) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        var tokenErrors = result.Violations.Where(v => v.Rule == "TokenIdValidity" && v.Severity == "error").ToList();
        Assert.That(tokenErrors, Has.Count.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Rule11_GroupAsTokenOwner_Passes()
    {
        var state = DefaultState();
        state.RegisterAll = false;
        state.Registered.Add(Alice);
        state.Registered.Add(Bob);
        state.Registered.Add(GroupA);
        state.Groups.Add(GroupA);

        var steps = new[] { Step(Alice, Bob, GroupA) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        var tokenErrors = result.Violations.Where(v => v.Rule == "TokenIdValidity").ToList();
        Assert.That(tokenErrors, Is.Empty, "Groups are registered — valid token owner");
    }

    [Test]
    public void Rule11_RouterAsTokenOwner_Exempt()
    {
        var state = DefaultState();
        state.RegisterAll = false;
        state.Registered.Add(Alice);
        state.Registered.Add(Bob);
        // Router NOT in Registered but should be exempt

        var steps = new[] { Step(Alice, Bob, Router) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        var tokenErrors = result.Violations.Where(v => v.Rule == "TokenIdValidity").ToList();
        Assert.That(tokenErrors, Is.Empty, "Router should be exempt from token validity check");
    }

    // ═══════════════════════════════════════════
    // CapacityGraphContractState.IsTrusted — GroupTrustedTokens fallback
    // ═══════════════════════════════════════════

    [Test]
    public void IsTrusted_GroupInGroupTrustedTokensOnly_ReturnsTrue()
    {
        // Groups are excluded from TrustLookup (trustQuery.sql WHERE group IS NULL).
        // IsTrusted must fall back to GroupTrustedTokens for group trusters.
        var graph = new CapacityGraph();
        var groupId = AddressIdPool.IdOf("0xee00000000000000000000000000000000group1");
        var tokenId = AddressIdPool.IdOf("0xee00000000000000000000000000000000token1");
        var untrustedId = AddressIdPool.IdOf("0xee00000000000000000000000000000000token2");

        graph.AddGroup(groupId);
        graph.GroupTrustedTokens[groupId] = new HashSet<int> { tokenId };
        // TrustLookup has NO entry for the group (matches production)
        graph.TrustLookup = new Dictionary<int, HashSet<int>>();

        var state = new CapacityGraphContractState(graph);

        Assert.That(state.IsTrusted(
            AddressIdPool.StringOf(groupId),
            AddressIdPool.StringOf(tokenId)), Is.True,
            "Group trusting token via GroupTrustedTokens should return true");

        Assert.That(state.IsTrusted(
            AddressIdPool.StringOf(groupId),
            AddressIdPool.StringOf(untrustedId)), Is.False,
            "Group not trusting token should return false");
    }

    // ═══════════════════════════════════════════
    // Rule 12: ScoreGroupMintLimitsHonored
    // Mirrors OffchainScoreBasedMintPolicy.beforeMintPolicy's router branch:
    // cumulative router→group deposits per (group, collateral) ≤ availableLimit.
    //
    // Units convention (mirrors production):
    //   TransferPathStep.Value: wei (10^18 per CRC, per V2Pathfinder.cs:454)
    //   ScoreGroupMintLimits cached value: 6-decimal CRC (10^6 per CRC,
    //     per GraphFactory.cs:739 / CirclesConverter.TruncateToInt64)
    // The validator inflates the cached limit to wei via BlowUpToBigInteger.
    // Tests use whole-CRC values where possible: 1 CRC = 1_000_000 cached,
    // 1 CRC = "1000000000000000000" as wei Value string.
    // ═══════════════════════════════════════════

    private const string Wei_0_4_CRC = "400000000000000000";   // 0.4 CRC in wei
    private const string Wei_0_5_CRC = "500000000000000000";   // 0.5 CRC in wei
    private const string Wei_1_0_CRC = "1000000000000000000";  // 1 CRC in wei
    private const long Cached_0_5_CRC = 500_000L;              // 0.5 CRC at 6dp
    private const long Cached_0_7_CRC = 700_000L;              // 0.7 CRC at 6dp
    private const long Cached_1_0_CRC = 1_000_000L;            // 1 CRC at 6dp

    [Test]
    public void Rule12_SingleIntermediaryWithinLimit_Passes()
    {
        var state = new MockContractState
        {
            Router = Router,
            ScoreRouters = { ScoreRouter },
            TrustAll = true,
            Groups = { GroupA },
            ScoreGroupMintLimits = { [(GroupA.ToLowerInvariant(), Alice.ToLowerInvariant())] = Cached_1_0_CRC },
        };
        var steps = new[]
        {
            Step(Alice, ScoreRouter, Alice, Wei_0_5_CRC),
            Step(ScoreRouter, GroupA, Alice, Wei_0_5_CRC),
            Step(GroupA, Bob, GroupA, Wei_0_5_CRC),
        };

        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateScoreGroupMintLimits(steps, state, violations);

        Assert.That(violations, Is.Empty,
            "0.5 CRC cumulative Alice-collateral ≤ 1 CRC availableLimit must pass.");
    }

    [Test]
    public void Rule12_TwoIntermediariesSameCollateralOverLimit_FailsWithError()
    {
        // Same-token sharing case: both Bob and Carol push Alice-collateral
        // through the score router into GroupA. They share the (GroupA, Alice)
        // cap, and their cumulative deposit exceeds it. The violation must
        // point at the SECOND deposit edge — the one that crosses the cap.
        var state = new MockContractState
        {
            Router = Router,
            ScoreRouters = { ScoreRouter },
            TrustAll = true,
            Groups = { GroupA },
            ScoreGroupMintLimits = { [(GroupA.ToLowerInvariant(), Alice.ToLowerInvariant())] = Cached_0_7_CRC },
        };
        // Two 0.4 CRC deposits → 0.8 CRC cumulative > 0.7 CRC cap
        var steps = new[]
        {
            Step(Bob, ScoreRouter, Alice, Wei_0_4_CRC),       // edge 0
            Step(ScoreRouter, GroupA, Alice, Wei_0_4_CRC),    // edge 1 — first router→group, within cap
            Step(Carol, ScoreRouter, Alice, Wei_0_4_CRC),     // edge 2
            Step(ScoreRouter, GroupA, Alice, Wei_0_4_CRC),    // edge 3 — second router→group, pushes over
            Step(GroupA, Dave, GroupA, "800000000000000000"), // edge 4 — 0.8 CRC mint
        };

        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateScoreGroupMintLimits(steps, state, violations);

        var rule12 = violations.Where(v => v.Rule == "ScoreGroupMintLimitsHonored").ToList();
        Assert.That(rule12.Count, Is.EqualTo(1),
            "Exactly one violation per (group, collateral) bucket.");
        Assert.That(rule12[0].Severity, Is.EqualTo("error"));
        Assert.That(rule12[0].EdgeIndex, Is.EqualTo(3),
            "Violation must point at the edge that pushes cumulative past the cap, not the bucket's first contributor.");
        Assert.That(rule12[0].Message,
            Does.Contain("800000000000000000")        // cumulative in wei
            .And.Contain("700000000000000000"));      // limit inflated to wei
    }

    [Test]
    public void Rule12_TwoIntermediariesIndependentCollateralsWithinLimits_Passes()
    {
        // Per-intermediary case: each intermediary contributes its own personal
        // CRC token as collateral. (GroupA, Bob) and (GroupA, Carol) are
        // independent rows on-chain, each with its own cap.
        var state = new MockContractState
        {
            Router = Router,
            ScoreRouters = { ScoreRouter },
            TrustAll = true,
            Groups = { GroupA },
            ScoreGroupMintLimits =
            {
                [(GroupA.ToLowerInvariant(), Bob.ToLowerInvariant())] = Cached_0_5_CRC,
                [(GroupA.ToLowerInvariant(), Carol.ToLowerInvariant())] = Cached_0_5_CRC,
            },
        };
        var steps = new[]
        {
            Step(Bob, ScoreRouter, Bob, Wei_0_4_CRC),
            Step(ScoreRouter, GroupA, Bob, Wei_0_4_CRC),
            Step(Carol, ScoreRouter, Carol, Wei_0_4_CRC),
            Step(ScoreRouter, GroupA, Carol, Wei_0_4_CRC),
            Step(GroupA, Dave, GroupA, "800000000000000000"),
        };

        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateScoreGroupMintLimits(steps, state, violations);

        Assert.That(violations, Is.Empty,
            "Independent (group, intermediary-token) rows each at 0.4 of 0.5 CRC cap must pass.");
    }

    [Test]
    public void Rule12_NoCachedLimitForScoreRouterEdge_FailsClosed()
    {
        // State-snapshot drift: a score-router→group edge exists but no
        // availableLimit row was emitted. Refuse the path (chain would revert).
        var state = new MockContractState
        {
            Router = Router,
            ScoreRouters = { ScoreRouter },
            TrustAll = true,
            Groups = { GroupA },
            // ScoreGroupMintLimits intentionally empty
        };
        var steps = new[]
        {
            Step(Alice, ScoreRouter, Alice, Wei_0_5_CRC),
            Step(ScoreRouter, GroupA, Alice, Wei_0_5_CRC),
            Step(GroupA, Bob, GroupA, Wei_0_5_CRC),
        };

        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateScoreGroupMintLimits(steps, state, violations);

        var rule12 = violations.Where(v => v.Rule == "ScoreGroupMintLimitsHonored").ToList();
        Assert.That(rule12.Count, Is.EqualTo(1));
        Assert.That(rule12[0].Severity, Is.EqualTo("error"));
        Assert.That(rule12[0].Message, Does.Contain("no availableLimit").Or.Contain("state-snapshot drift"));
    }

    [Test]
    public void Rule12_NoScoreRouterEdges_NoOp()
    {
        // Base-group flows (regular router, no score router on path) must
        // not be touched by Rule 12, regardless of whether any limits are
        // cached. The validator scopes by IsScoreRouter().
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            Groups = { GroupA },
        };
        var steps = new[]
        {
            Step(Alice, Router, Alice, Wei_0_5_CRC),
            Step(Router, GroupA, Alice, Wei_0_5_CRC),
            Step(GroupA, Bob, GroupA, Wei_0_5_CRC),
        };

        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateScoreGroupMintLimits(steps, state, violations);

        Assert.That(violations, Is.Empty,
            "Non-score routers must not trigger Rule 12.");
    }

    [Test]
    public void Rule12_ExactLimitBoundary_Passes()
    {
        // Cumulative == limit (exact-edge boundary) — strict-greater compare,
        // so this must pass. Regression: ensures we never silently switch
        // to >= which would reject paths that the contract would accept.
        var state = new MockContractState
        {
            Router = Router,
            ScoreRouters = { ScoreRouter },
            TrustAll = true,
            Groups = { GroupA },
            ScoreGroupMintLimits = { [(GroupA.ToLowerInvariant(), Alice.ToLowerInvariant())] = Cached_1_0_CRC },
        };
        var steps = new[]
        {
            Step(Alice, ScoreRouter, Alice, Wei_1_0_CRC),
            Step(ScoreRouter, GroupA, Alice, Wei_1_0_CRC), // exactly == 1 CRC limit
            Step(GroupA, Bob, GroupA, Wei_1_0_CRC),
        };

        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateScoreGroupMintLimits(steps, state, violations);

        Assert.That(violations, Is.Empty, "Cumulative == limit must pass (strict-greater compare).");
    }

    [Test]
    public void Rule12_UnparseableValueOnScoreRouterEdge_FailsWithError()
    {
        // Silent-failure guard: BigInteger.TryParse failures on score-router
        // edges must surface as a Rule 12 violation, not slip past via
        // "continue". Rule 1 doesn't catch non-empty junk strings.
        var state = new MockContractState
        {
            Router = Router,
            ScoreRouters = { ScoreRouter },
            TrustAll = true,
            Groups = { GroupA },
            ScoreGroupMintLimits = { [(GroupA.ToLowerInvariant(), Alice.ToLowerInvariant())] = Cached_1_0_CRC },
        };
        var steps = new[]
        {
            Step(Alice, ScoreRouter, Alice, "not-a-number"),
            Step(ScoreRouter, GroupA, Alice, "0xdeadbeef"),
            Step(GroupA, Bob, GroupA, Wei_0_5_CRC),
        };

        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateScoreGroupMintLimits(steps, state, violations);

        var rule12 = violations.Where(v => v.Rule == "ScoreGroupMintLimitsHonored").ToList();
        Assert.That(rule12.Any(v => v.Message.Contains("unparseable")), Is.True,
            "Unparseable Value on a score-router→group edge must produce a Rule 12 error.");
    }

    [Test]
    public void Rule12_WrapperTokenOwnerKeyedAsRaw_MatchesGraphFactory()
    {
        // Regression: Rule 12 must NOT resolve wrapper→underlying. The
        // ScoreGroupMintLimits cache is keyed by raw token id (matching
        // GraphFactory.cs:999 + GraphFactory.cs:1032). If a wrapper ever
        // appeared as TokenOwner on a score-router→group hop, the cache
        // lookup must use the wrapper address directly so it matches what
        // GraphFactory stored. Resolving here would create a spurious
        // "no availableLimit cached" fail-close.
        const string WrapperAddr = "0x0000000000000000000000000000000000000088";
        var state = new MockContractState
        {
            Router = Router,
            ScoreRouters = { ScoreRouter },
            TrustAll = true,
            Groups = { GroupA },
            Wrappers = { [WrapperAddr] = Alice }, // wrapper→underlying mapping exists…
            ScoreGroupMintLimits =
            {
                // …but the cache is keyed under the WRAPPER address, not Alice.
                [(GroupA.ToLowerInvariant(), WrapperAddr.ToLowerInvariant())] = Cached_1_0_CRC,
            },
        };
        var steps = new[]
        {
            Step(Alice, ScoreRouter, WrapperAddr, Wei_0_5_CRC),
            Step(ScoreRouter, GroupA, WrapperAddr, Wei_0_5_CRC),
            Step(GroupA, Bob, GroupA, Wei_0_5_CRC),
        };

        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateScoreGroupMintLimits(steps, state, violations);

        Assert.That(violations, Is.Empty,
            "Wrapper-keyed lookup must succeed without wrapper resolution; " +
            "if Rule 12 resolved to Alice, the lookup would miss and fail-close.");
    }
}
