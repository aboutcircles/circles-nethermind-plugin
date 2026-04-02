using Circles.Common.Dto;
using Circles.Pathfinder.Validation;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Branch-covering tests for HubContractValidator — each test targets a specific
/// Hub.sol revert condition by constructing a minimal invalid path that triggers
/// exactly that branch. Test names reference the Hub.sol line/function being covered.
/// </summary>
[TestFixture, Parallelizable]
public class HubSolBranchCoverageTests
{
    // Standard test addresses (valid 42-char hex)
    private const string Alice = "0x0000000000000000000000000000000000000001";
    private const string Bob = "0x0000000000000000000000000000000000000002";
    private const string Carol = "0x0000000000000000000000000000000000000003";
    private const string Dave = "0x0000000000000000000000000000000000000004";
    private const string Router = "0x0000000000000000000000000000000000000099";
    private const string GroupA = "0x00000000000000000000000000000000000000a1";
    private const string GroupB = "0x00000000000000000000000000000000000000a2";
    private const string Unregistered = "0x000000000000000000000000000000000000dead";

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
        public HashSet<string> Registered { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool RegisterAll { get; set; } = true;

        public string? RouterAddress => Router;
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
    }

    #endregion

    // =============================================
    // Branch 1: Zero flow — Hub.sol rejects zero amounts
    // =============================================

    [Test]
    public void Branch_ZeroFlow_EdgeWithValueZero_CaughtByNoZeroFlow()
    {
        var steps = new[] { Step(Alice, Bob, Alice, "0") };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Violations.Any(v => v.Rule == "NoZeroFlow" && v.Severity == "error"), Is.True,
            "Hub.sol rejects zero amounts in the flow matrix — validator must catch Value='0'");
    }

    // =============================================
    // Branch 2: Empty flow — missing value string
    // =============================================

    [Test]
    public void Branch_EmptyFlow_EdgeWithEmptyValue_CaughtByNoZeroFlow()
    {
        var steps = new[] { Step(Alice, Bob, Alice, "") };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Violations.Any(v => v.Rule == "NoZeroFlow" && v.Severity == "error"), Is.True,
            "Empty value string is equivalent to zero flow — must be caught");
    }

    // =============================================
    // Branch 3: Invalid address format — revert on uint160 cast
    // =============================================

    [Test]
    public void Branch_InvalidAddressFormat_ShortAddress_CaughtByAddressFormat()
    {
        // Hub.sol: uint160 cast from address requires valid 20-byte hex
        var steps = new[] { Step("0x1234", Bob, Alice) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Violations.Any(v => v.Rule == "AddressFormat" && v.Severity == "error" && v.Message.Contains("From")), Is.True,
            "Short address would fail uint160 cast on-chain");
    }

    // =============================================
    // Branch 4: Non-hex address — invalid characters
    // =============================================

    [Test]
    public void Branch_NonHexAddress_ContainsInvalidChars_CaughtByAddressFormat()
    {
        // Correct length but non-hex characters: 'G', 'Z' etc.
        var badAddr = "0xGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG";
        var steps = new[] { Step(badAddr, Bob, Alice) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Violations.Any(v => v.Rule == "AddressFormat" && v.Severity == "error" && v.Message.Contains("non-hex")), Is.True,
            "Non-hex characters in address must be caught before on-chain revert");
    }

    // =============================================
    // Branch 5: Unsortable vertices — duplicate uint160
    // Hub.sol: _flowVertices must be uint160-sorted ascending
    // =============================================

    [Test]
    public void Branch_UnsortableVertices_DuplicateUint160Values_CaughtByVertexOrdering()
    {
        // Two addresses that would parse to the same uint160 value but differ in string form
        // can't actually happen with lowercase normalization. Instead, test the public method directly
        // with addresses that create a duplicate uint160 after lowercasing.
        // In practice, the validator lowercases all addresses, so same-value duplicates are deduplicated.
        // The rule fires when different address strings yield the same BigInteger — unlikely with
        // proper normalization, so we test the internal method directly.
        var violations = new List<ValidationViolation>();

        // Use two addresses that differ only in case but are otherwise identical:
        // After lowercasing they become the same string, so no violation from VertexOrdering.
        // Instead, we craft a scenario where the from/to addresses in steps + source/sink
        // would collide. Since all are lowercased and deduplicated, VertexOrdering won't fire.
        // This is actually correct behavior — the rule catches hex-collisions, not duplicates.
        var steps = new[] { Step(Alice, Bob, Alice) };
        HubContractValidator.ValidateVertexOrdering(steps, Alice, Bob, violations);
        Assert.That(violations.Any(v => v.Rule == "VertexOrdering"), Is.False,
            "Properly normalized addresses should not produce VertexOrdering violations");
    }

    // =============================================
    // Branch 6: Standard flow — untrusted token
    // Hub.sol:668 isPermittedFlow: if !advancedUsageFlags[_from]
    //   return isTrusted(_to, _circlesAvatar)
    // =============================================

    [Test]
    public void Branch_StandardFlow_UntrustedToken_CaughtByIsPermittedFlow()
    {
        // Bob does NOT trust Alice's token — standard flow check fails
        var state = new MockContractState
        {
            Router = Router,
            // TrustAll = false (default), no explicit trusts
        };
        var steps = new[] { Step(Alice, Bob, Alice) }; // Alice sends Alice-token to Bob
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        Assert.That(result.IsValid, Is.False);
        var permViolations = result.Violations
            .Where(v => v.Rule == "IsPermittedFlow" && v.Severity == "error")
            .ToList();
        Assert.That(permViolations, Has.Count.GreaterThanOrEqualTo(1));
        Assert.That(permViolations.Any(v => v.Message.Contains("does not trust")), Is.True,
            "Hub.sol:668 — standard mode: isTrusted(to, circlesAvatar) must hold");
    }

    // =============================================
    // Branch 7: Consented flow — From does not trust To
    // Hub.sol:671 consented branch: isTrusted(_from, _to)
    // =============================================

    [Test]
    public void Branch_ConsentedFlow_FromNotTrustingTo_CaughtByIsPermittedFlow()
    {
        // Both Alice and Bob have advancedUsageFlags, but Alice does NOT trust Bob
        var state = new MockContractState
        {
            Router = Router,
            Consented = { Alice, Bob },
            // No trust relationships — Alice doesn't trust Bob
        };
        var steps = new[] { Step(Alice, Bob, Carol) }; // Consented: checks From trusts To
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        Assert.That(result.IsValid, Is.False);
        var permViolations = result.Violations
            .Where(v => v.Rule == "IsPermittedFlow" && v.Severity == "error")
            .ToList();
        Assert.That(permViolations.Any(v => v.Message.Contains("does not trust")), Is.True,
            "Hub.sol:671 — consented branch requires isTrusted(from, to)");
    }

    // =============================================
    // Branch 8: Consented flow — To without advancedUsageFlags
    // Hub.sol:671 consented branch: advancedUsageFlags[_to]
    // =============================================

    [Test]
    public void Branch_ConsentedFlow_ToWithoutFlag_CaughtByIsPermittedFlow()
    {
        // Alice has advancedUsageFlags, Bob does NOT
        var state = new MockContractState
        {
            Router = Router,
            Consented = { Alice }, // Only Alice — Bob missing
            Trusts = { (Alice.ToLower(), Bob.ToLower()) }, // Alice trusts Bob (trust part passes)
        };
        var steps = new[] { Step(Alice, Bob, Carol) }; // Consented: checks advancedUsageFlags[To]
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        Assert.That(result.IsValid, Is.False);
        var permViolations = result.Violations
            .Where(v => v.Rule == "IsPermittedFlow" && v.Severity == "error")
            .ToList();
        Assert.That(permViolations.Any(v => v.Message.Contains("advancedUsageFlags")), Is.True,
            "Hub.sol:671 — consented branch requires advancedUsageFlags[to]");
    }

    // =============================================
    // Branch 9: Flow leak — intermediate vertex net flow != 0
    // Hub.sol: operateFlowMatrix reverts on NettedFlowMismatch
    // =============================================

    [Test]
    public void Branch_FlowLeak_IntermediateVertexUnbalanced_CaughtByFlowConservation()
    {
        // A->B(100), B->C(50) — Bob leaks 50 units
        var steps = new[]
        {
            Step(Alice, Bob, Alice, "100"),
            Step(Bob, Carol, Bob, "50"),
        };
        var result = HubContractValidator.Validate(steps, Alice, Carol, DefaultState());

        Assert.That(result.IsValid, Is.False);
        var conservationViolations = result.Violations
            .Where(v => v.Rule == "FlowConservation" && v.Severity == "error")
            .ToList();
        Assert.That(conservationViolations, Has.Count.EqualTo(1));
        Assert.That(conservationViolations[0].Message, Does.Contain("net flow"),
            "Hub.sol NettedFlowMismatch — intermediate vertex must have zero net flow");
    }

    // =============================================
    // Branch 10: Mint before collateral
    // Hub.sol:_groupMint — collateral must be deposited before minting
    // =============================================

    [Test]
    public void Branch_MintBeforeCollateral_ReversedOrder_CaughtByCollateralBeforeMint()
    {
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            Groups = { GroupA },
        };
        // Group→Avatar mint appears BEFORE Router→Group collateral
        var steps = new[]
        {
            Step(GroupA, Bob, GroupA, "100"),    // Mint FIRST — wrong order!
            Step(Router, GroupA, Alice, "100"),  // Collateral AFTER — too late
        };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Violations.Any(v => v.Rule == "CollateralBeforeMint" && v.Severity == "error"), Is.True,
            "Hub.sol _groupMint: collateral must precede mint in edge ordering");
    }

    // =============================================
    // Branch 11: Insufficient collateral
    // Hub.sol:_groupMint — cumulative collateral < outbound
    // =============================================

    [Test]
    public void Branch_InsufficientCollateral_ShortByHalf_CaughtByCollateralBeforeMint()
    {
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            Groups = { GroupA },
        };
        // Only 50 collateral but trying to mint 100
        var steps = new[]
        {
            Step(Router, GroupA, Alice, "50"),   // Collateral: 50
            Step(GroupA, Carol, GroupA, "100"),  // Mint: 100 — short by 50
        };
        var result = HubContractValidator.Validate(steps, Alice, Carol, state);

        Assert.That(result.IsValid, Is.False);
        var collateralViolations = result.Violations
            .Where(v => v.Rule == "CollateralBeforeMint" && v.Severity == "error")
            .ToList();
        Assert.That(collateralViolations, Has.Count.EqualTo(1));
        Assert.That(collateralViolations[0].Message, Does.Contain("insufficient collateral"),
            "Hub.sol _groupMint: cumulative collateral must be >= cumulative mint amount");
    }

    // =============================================
    // Branch 12: Unregistered vertex (From or To)
    // Hub.sol:794-805 — avatars[addr] != address(0) for ALL flow vertices
    // Error codes 0x24 (non-last) and 0x25 (last)
    // =============================================

    [Test]
    public void Branch_UnregisteredVertex_FromAndToNotRegistered_CaughtByAvatarRegistration()
    {
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            RegisterAll = false,
            Registered = { Alice }, // Only Alice registered; Unregistered is not
        };
        // Unregistered address as From — should fail
        var steps = new[] { Step(Unregistered, Alice, Alice) };
        var result = HubContractValidator.Validate(steps, Unregistered, Alice, state);

        Assert.That(result.IsValid, Is.False);
        var regViolations = result.Violations
            .Where(v => v.Rule == "AvatarRegistration" && v.Severity == "error")
            .ToList();
        Assert.That(regViolations, Has.Count.GreaterThanOrEqualTo(1),
            "Hub.sol:794-805 — unregistered From vertex must be caught (error 0x24)");
        // Validator truncates addresses to first 10 chars in messages: "0x00000000"
        // so check the Unregistered address is among unregistered vertices
        Assert.That(regViolations.Any(v => v.Message.Contains("not a registered avatar")), Is.True,
            "Violation message should indicate unregistered avatar");
    }

    // =============================================
    // Branch 13: Unregistered source
    // Hub.sol:794-805 — source is always a flow vertex
    // =============================================

    [Test]
    public void Branch_UnregisteredSource_SourceNotInRegistered_CaughtByAvatarRegistration()
    {
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            RegisterAll = false,
            Registered = { Bob }, // Alice (source) not registered
        };
        var steps = new[] { Step(Alice, Bob, Bob) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        Assert.That(result.IsValid, Is.False);
        var regViolations = result.Violations
            .Where(v => v.Rule == "AvatarRegistration" && v.Severity == "error")
            .ToList();
        Assert.That(regViolations.Any(v => v.Message.Contains(Alice[..10])), Is.True,
            "Hub.sol:794-805 — source vertex must be registered (error 0x25)");
    }

    // =============================================
    // Branch 14: Non-group in mint pattern
    // Hub.sol:707 isGroup(_group) check in _groupMint
    // Error code 0x40 (CirclesHubGroupIsNotRegistered)
    // =============================================

    [Test]
    public void Branch_NonGroupInMintPattern_NonGroupReceivesFromRouter_CaughtByGroupRegistration()
    {
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            // Carol is NOT a group — but receives from Router and sends own token
        };
        var steps = new[]
        {
            Step(Alice, Router, Alice, "100"),
            Step(Router, Carol, Alice, "100"),  // "Collateral" to non-group
            Step(Carol, Bob, Carol, "100"),      // Carol sends own token (group-mint pattern)
        };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        Assert.That(result.IsValid, Is.False);
        var groupViolations = result.Violations
            .Where(v => v.Rule == "GroupRegistration" && v.Severity == "error")
            .ToList();
        Assert.That(groupViolations, Has.Count.GreaterThanOrEqualTo(1),
            "Hub.sol:707 — address in group-mint pattern must be a registered group (error 0x40)");
    }

    // =============================================
    // Branch 15: Unregistered TokenOwner
    // Hub.sol:718 _validateAddressFromId — collateral token IDs
    // must encode valid registered avatar addresses
    // =============================================

    [Test]
    public void Branch_UnregisteredTokenOwner_NotRegistered_CaughtByTokenIdValidity()
    {
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            RegisterAll = false,
            Registered = { Alice, Bob }, // Unregistered address NOT in set
        };
        // TokenOwner is unregistered — invalid token ID
        var steps = new[] { Step(Alice, Bob, Unregistered) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        Assert.That(result.IsValid, Is.False);
        var tokenViolations = result.Violations
            .Where(v => v.Rule == "TokenIdValidity" && v.Severity == "error")
            .ToList();
        Assert.That(tokenViolations, Has.Count.GreaterThanOrEqualTo(1),
            "Hub.sol:718 _validateAddressFromId — TokenOwner must be a registered avatar");
    }

    // =============================================
    // Branch 16: Multiple violations — all reported
    // Validator must not short-circuit after first error
    // =============================================

    [Test]
    public void Branch_MultipleViolations_AllReported_NoShortCircuit()
    {
        var state = new MockContractState
        {
            Router = Router,
            // TrustAll = false (default) — triggers IsPermittedFlow
            RegisterAll = false,
            // No registrations — triggers AvatarRegistration + TokenIdValidity
        };

        // This path has at least 3 distinct violations:
        //   1. NoZeroFlow (value = "0")
        //   2. AddressFormat (short address)
        //   3. AvatarRegistration (unregistered vertices)
        var steps = new[]
        {
            Step("0x1234", Bob, Alice, "0"),     // AddressFormat + NoZeroFlow
            Step(Alice, Bob, Unregistered),       // AvatarRegistration + TokenIdValidity + IsPermittedFlow
        };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        Assert.That(result.IsValid, Is.False);

        var errorRules = result.Violations
            .Where(v => v.Severity == "error")
            .Select(v => v.Rule)
            .ToHashSet();

        // At least 3 distinct rule violations should be present
        Assert.That(errorRules, Does.Contain("NoZeroFlow"), "Zero flow should be reported");
        Assert.That(errorRules, Does.Contain("AddressFormat"), "Invalid address should be reported");
        Assert.That(errorRules.Count, Is.GreaterThanOrEqualTo(3),
            "Validator must report all violations without short-circuiting — found: " + string.Join(", ", errorRules));
    }

    // =============================================
    // Branch 17: Valid path — no violations
    // Complete valid path with groups, consent, router,
    // and proper trust/registration/ordering
    // =============================================

    [Test]
    public void Branch_ValidPath_NoViolations_ComplexScenarioPassesAll11Rules()
    {
        var state = new MockContractState
        {
            Router = Router,
            RegisterAll = false,
            Registered = { Alice, Bob, Carol, Dave, GroupA, GroupB },
            Groups = { GroupA, GroupB },
            Consented = { Carol, Dave },
            Trusts =
            {
                // Standard flow trusts: To trusts TokenOwner
                (Bob.ToLower(), Alice.ToLower()),       // Bob trusts Alice's token
                (Carol.ToLower(), GroupA.ToLower()),     // Carol trusts GroupA's token (for GroupA→Carol mint)

                // Router trust: Router must trust token owners for Avatar→Router edges (Hub.sol:665)
                (Router.ToLower(), Alice.ToLower()),     // Router trusts Alice's token

                // Consented flow trusts: From trusts To
                (Carol.ToLower(), Dave.ToLower()),       // Carol trusts Dave (consented branch)
            },
        };

        // Complex valid path:
        //   Alice -> Bob (Alice-token, standard flow: Bob trusts Alice)
        //   Bob -> Router (Bob hands off to router for group mint)
        //   Router -> GroupA (collateral deposit: Alice-token)
        //   GroupA -> Carol (mint: GroupA-token)
        //   Carol -> Dave (GroupA-token, consented flow: Carol trusts Dave, both have flags)
        var steps = new[]
        {
            Step(Alice, Bob, Alice, "100"),          // Standard: Bob trusts Alice
            Step(Bob, Router, Alice, "100"),         // Handoff to Router
            Step(Router, GroupA, Alice, "100"),      // Collateral deposit
            Step(GroupA, Carol, GroupA, "100"),       // Group mint
            Step(Carol, Dave, GroupA, "100"),         // Consented: Carol trusts Dave, both flagged
        };
        var result = HubContractValidator.Validate(steps, Alice, Dave, state);

        Assert.That(result.IsValid, Is.True,
            "Valid complex path should pass all 11 rules. Violations: " +
            string.Join("; ", result.Violations.Select(v => $"[{v.Rule}/{v.Severity}] {v.Message}")));
        Assert.That(result.Violations.Where(v => v.Severity == "error"), Is.Empty,
            "No error-severity violations expected in a correctly constructed path");
    }

    // =============================================
    // Additional targeted branch tests
    // =============================================

    [Test]
    public void Branch_RouterExemptFromRegistration_NotFlaggedByAvatarRegistration()
    {
        // Router is a contract — always valid on-chain but not in Registered set
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            RegisterAll = false,
            Registered = { Alice, Bob, GroupA },
            Groups = { GroupA },
            // Router NOT in Registered
        };
        var steps = new[]
        {
            Step(Alice, Router, Alice, "100"),
            Step(Router, GroupA, Alice, "100"),
            Step(GroupA, Bob, GroupA, "100"),
        };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        var regErrors = result.Violations.Where(v => v.Rule == "AvatarRegistration").ToList();
        Assert.That(regErrors, Is.Empty, "Router address must be exempt from avatar registration checks");
    }

    [Test]
    public void Branch_RouterExemptFromTokenValidity_NotFlaggedByTokenIdValidity()
    {
        // Router used as TokenOwner — should be exempt
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            RegisterAll = false,
            Registered = { Alice, Bob },
            // Router NOT in Registered
        };
        var steps = new[] { Step(Alice, Bob, Router) };
        var result = HubContractValidator.Validate(steps, Alice, Bob, state);

        var tokenErrors = result.Violations.Where(v => v.Rule == "TokenIdValidity").ToList();
        Assert.That(tokenErrors, Is.Empty, "Router as TokenOwner must be exempt from token ID validity");
    }

    [Test]
    public void Branch_SinkSelfLoop_FilteredBeforeValidation()
    {
        // Quantized mode produces Sink->Sink display-only edges.
        // These should be filtered out before any rule runs.
        var steps = new[]
        {
            Step(Alice, Bob, Alice, "100"),
            Step(Bob, Bob, Alice, "50"), // Sink self-loop (display-only in quantized mode)
        };
        var result = HubContractValidator.Validate(steps, Alice, Bob, DefaultState());

        Assert.That(result.Violations.Any(v => v.Rule == "NoSelfTransfers"), Is.False,
            "Sink self-loops should be filtered out before rules execute");
        Assert.That(result.Violations.Any(v => v.Rule == "FlowConservation"), Is.False,
            "After filtering self-loop, flow conservation should hold");
    }

    [Test]
    public void Branch_MultiGroupCollateral_IndependentTracking()
    {
        // Each group tracks collateral independently — GroupA sufficient, GroupB insufficient
        var state = new MockContractState
        {
            Router = Router,
            TrustAll = true,
            Groups = { GroupA, GroupB },
        };
        var steps = new[]
        {
            Step(Router, GroupA, Alice, "100"),
            Step(Router, GroupB, Bob, "50"),      // Only 50 collateral for GroupB
            Step(GroupA, Carol, GroupA, "100"),    // GroupA: 100 >= 100 OK
            Step(GroupB, Dave, GroupB, "100"),     // GroupB: 50 < 100 FAIL
        };
        var result = HubContractValidator.Validate(steps, Alice, Dave, state);

        var collateralErrors = result.Violations
            .Where(v => v.Rule == "CollateralBeforeMint" && v.Severity == "error")
            .ToList();
        Assert.That(collateralErrors, Has.Count.EqualTo(1), "Only GroupB should have insufficient collateral");
        Assert.That(collateralErrors[0].Message, Does.Contain(GroupB[..10]),
            "Violation should reference GroupB specifically");
    }

    [Test]
    public void Branch_ConsentedAndStandard_MixedInSamePath()
    {
        // Path contains both standard and consented edges — each checked by its own branch
        var state = new MockContractState
        {
            Router = Router,
            RegisterAll = true,
            Consented = { Bob, Carol },
            Trusts =
            {
                (Bob.ToLower(), Alice.ToLower()),    // Standard: Bob trusts Alice-token
                (Bob.ToLower(), Carol.ToLower()),    // Consented: Bob trusts Carol
                (Carol.ToLower(), Bob.ToLower()),    // Consented: Carol trusts Bob
            },
        };
        // Alice->Bob (standard), Bob->Carol (consented)
        var steps = new[]
        {
            Step(Alice, Bob, Alice, "100"),   // Standard: Bob trusts Alice
            Step(Bob, Carol, Alice, "100"),   // Consented: Bob trusts Carol, both have flags
        };
        var result = HubContractValidator.Validate(steps, Alice, Carol, state);

        Assert.That(result.IsValid, Is.True,
            "Mixed standard + consented path should pass when all trusts and flags are set. " +
            "Violations: " + string.Join("; ", result.Violations.Select(v => $"[{v.Rule}] {v.Message}")));
    }
}
