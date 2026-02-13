using Circles.Common.Dto;
using Circles.Pathfinder.Validation;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for HubContractValidator — each rule tested in isolation
/// with positive, negative, and edge cases.
/// </summary>
[TestFixture]
public class HubContractValidatorTests
{
    // Standard test addresses (valid 42-char hex)
    private const string Alice = "0x0000000000000000000000000000000000000001";
    private const string Bob = "0x0000000000000000000000000000000000000002";
    private const string Carol = "0x0000000000000000000000000000000000000003";
    private const string Dave = "0x0000000000000000000000000000000000000004";
    private const string Router = "0x0000000000000000000000000000000000000099";
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
        public HashSet<string> Groups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Consented { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(string Truster, string CirclesId)> Trusts { get; set; } = new();
        public bool TrustAll { get; set; }

        public string? RouterAddress => Router;
        public bool IsGroup(string address) => Groups.Contains(address);
        public bool HasAdvancedUsageFlags(string address) => Consented.Contains(address);

        public bool IsTrusted(string truster, string circlesId)
        {
            if (TrustAll) return true;
            return Trusts.Contains((truster.ToLowerInvariant(), circlesId.ToLowerInvariant()));
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
        HubContractValidator.ValidateIsPermittedFlow(steps, state, violations);
        Assert.That(violations, Is.Empty);
    }

    [Test]
    public void Rule4_StandardFlow_NotTrusted_Fails()
    {
        var state = new MockContractState { Router = Router }; // No trusts, TrustAll=false
        var steps = new[] { Step(Alice, Bob, Alice) };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, state, violations);
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
        HubContractValidator.ValidateIsPermittedFlow(steps, state, violations);
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
        HubContractValidator.ValidateIsPermittedFlow(steps, state, violations);
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
        HubContractValidator.ValidateIsPermittedFlow(steps, state, violations);
        Assert.That(violations.Any(v => v.Message.Contains("does not trust")), Is.True);
    }

    [Test]
    public void Rule4_RouterEdge_BypassesCheck()
    {
        var state = new MockContractState { Router = Router }; // No trusts at all
        // Router → Group edge should be skipped entirely
        var steps = new[]
        {
            Step(Router, GroupA, Alice),
            Step(Alice, Router, Alice),
        };
        var violations = new List<ValidationViolation>();
        HubContractValidator.ValidateIsPermittedFlow(steps, state, violations);
        Assert.That(violations, Is.Empty, "Router edges should bypass isPermittedFlow");
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
}
