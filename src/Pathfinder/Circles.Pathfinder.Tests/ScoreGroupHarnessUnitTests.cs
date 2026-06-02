using Circles.Pathfinder.Tests.Helpers;
using Circles.Pathfinder.Tests.Scenarios;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Offline unit tests for the ScoreGroup harness mechanics (calldata mutation + custom-error
/// decoding). These run everywhere — no TEST_ENV_URL, no DB, no Anvil — and lock the core logic
/// that the block-pinned ScoreGroup matrix depends on.
/// </summary>
[TestFixture]
public class ScoreGroupHarnessUnitTests
{
    private const string Selector = "12345678";
    private const string FillerA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FillerB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Amount1000 = "00000000000000000000000000000000000000000000000000000000000003e8";

    private static string Calldata(string body) => "0x" + Selector + body;

    // ---------------------------------------------------------------------
    // EnrichRevertReason — decoding real ScoreGroup policy selectors
    // ---------------------------------------------------------------------

    [TestCase("0x659c8c43", "AmountExceedsCollateralLimit")]
    [TestCase("0x1fd236d6", "CollateralLimitReached")]
    [TestCase("0x15561365", "InvalidScore")]
    [TestCase("0xc809c613", "NoSnapshot")]
    [TestCase("0x23edbe17", "NotAtomicMint")]
    [TestCase("0x2c6b2d9b", "AmountExceedsScoreLimit")]
    [TestCase("0xa0f984e2", "InvalidCollateralForPersonalIssuanceMint")]
    public void EnrichRevertReason_decodes_known_selector(string selector, string expectedName)
    {
        var raw = $"execution reverted, data: \"{selector}\"";
        var enriched = AnvilExecutionHelper.EnrichRevertReason(raw);

        Assert.That(enriched, Does.Contain(expectedName));
        Assert.That(enriched, Does.Contain(selector));
        Assert.That(enriched, Does.StartWith(raw), "raw message must be preserved");
    }

    [Test]
    public void EnrichRevertReason_leaves_unknown_selector_untouched()
    {
        const string raw = "execution reverted: 0xdeadbeef";
        Assert.That(AnvilExecutionHelper.EnrichRevertReason(raw), Is.EqualTo(raw));
    }

    [Test]
    public void EnrichRevertReason_handles_empty()
    {
        Assert.That(AnvilExecutionHelper.EnrichRevertReason(null), Is.EqualTo(string.Empty));
        Assert.That(AnvilExecutionHelper.EnrichRevertReason(""), Is.EqualTo(string.Empty));
    }

    [Test]
    public void KnownErrorSelectors_are_the_audited_set()
    {
        // Guards against accidental edits to the decoder table.
        Assert.That(AnvilExecutionHelper.KnownErrorSelectors["0x659c8c43"], Is.EqualTo("AmountExceedsCollateralLimit"));
        Assert.That(AnvilExecutionHelper.KnownErrorSelectors["0x1fd236d6"], Is.EqualTo("CollateralLimitReached"));
        Assert.That(AnvilExecutionHelper.KnownErrorSelectors, Has.Count.GreaterThanOrEqualTo(13));
    }

    // ---------------------------------------------------------------------
    // ApplyMutation — increment (amount bump → exceeds-limit branches)
    // ---------------------------------------------------------------------

    [Test]
    public void ApplyMutation_increment_bumps_located_amount_word()
    {
        var data = Calldata(FillerA + Amount1000 + FillerB);
        var m = new ScenarioMutation { Field = "amount", Op = "increment", Value = "1", Locator = "0x" + Amount1000 };

        var mutated = AnvilExecutionHelper.ApplyMutation(data, m);

        const string amount1001 = "00000000000000000000000000000000000000000000000000000000000003e9";
        Assert.That(mutated, Does.Contain(amount1001), "amount should be incremented to 1001");
        Assert.That(mutated, Does.Not.Contain(Amount1000), "original amount word should be gone");
        Assert.That(mutated, Does.StartWith("0x" + Selector), "selector must be preserved");
        Assert.That(mutated, Does.Contain(FillerA).And.Contain(FillerB), "other words untouched");
    }

    [Test]
    public void ApplyMutation_increment_accepts_short_locator()
    {
        var data = Calldata(FillerA + Amount1000);
        var m = new ScenarioMutation { Field = "amount", Op = "increment", Value = "2", Locator = "0x3e8" };

        var mutated = AnvilExecutionHelper.ApplyMutation(data, m);

        Assert.That(mutated, Does.Contain("00000000000000000000000000000000000000000000000000000000000003ea"));
    }

    [Test]
    public void ApplyMutation_increment_without_locator_throws()
    {
        var m = new ScenarioMutation { Field = "amount", Op = "increment", Value = "1" };
        Assert.Throws<InvalidOperationException>(() => AnvilExecutionHelper.ApplyMutation(Calldata(Amount1000), m));
    }

    // ---------------------------------------------------------------------
    // ApplyMutation — corrupt (proof corruption → InvalidScore branch)
    // ---------------------------------------------------------------------

    [Test]
    public void ApplyMutation_corrupt_with_locator_flips_first_byte()
    {
        var data = Calldata(FillerA + Amount1000);
        var m = new ScenarioMutation { Field = "proof", Op = "corrupt", Locator = "0x" + Amount1000 };

        var mutated = AnvilExecutionHelper.ApplyMutation(data, m);

        // 0x00 ^ 0xff = 0xff on the leading byte; the rest of the word is unchanged.
        Assert.That(mutated, Does.Not.Contain(Amount1000), "the located word must change");
        Assert.That(mutated, Does.Contain("ff000000000000000000000000000000000000000000000000000000000003e8"));
    }

    [Test]
    public void ApplyMutation_corrupt_without_locator_flips_last_word()
    {
        var tail = "0000000000000000000000000000000000000000000000000000000000000005";
        var data = Calldata(FillerA + tail);
        var m = new ScenarioMutation { Field = "proof", Op = "corrupt" };

        var mutated = AnvilExecutionHelper.ApplyMutation(data, m);

        Assert.That(mutated, Does.EndWith("ff00000000000000000000000000000000000000000000000000000000000005"));
        Assert.That(mutated, Does.Contain(FillerA), "earlier words untouched");
    }

    // ---------------------------------------------------------------------
    // ApplyMutation — safety (ambiguous / missing locators must throw, never silently mis-patch)
    // ---------------------------------------------------------------------

    [Test]
    public void ApplyMutation_throws_on_ambiguous_locator()
    {
        var data = Calldata(Amount1000 + FillerA + Amount1000); // amount word appears twice
        var m = new ScenarioMutation { Field = "amount", Op = "increment", Value = "1", Locator = "0x" + Amount1000 };

        var ex = Assert.Throws<InvalidOperationException>(() => AnvilExecutionHelper.ApplyMutation(data, m));
        Assert.That(ex!.Message, Does.Contain("ambiguous"));
    }

    [Test]
    public void ApplyMutation_throws_on_missing_locator()
    {
        var data = Calldata(FillerA);
        var m = new ScenarioMutation { Field = "amount", Op = "increment", Value = "1", Locator = "0x" + Amount1000 };

        var ex = Assert.Throws<InvalidOperationException>(() => AnvilExecutionHelper.ApplyMutation(data, m));
        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    [Test]
    public void ApplyMutation_zero_clears_located_word()
    {
        var data = Calldata(FillerA + Amount1000);
        var m = new ScenarioMutation { Field = "amount", Op = "zero", Locator = "0x" + Amount1000 };

        var mutated = AnvilExecutionHelper.ApplyMutation(data, m);

        Assert.That(mutated, Does.Not.Contain(Amount1000));
        Assert.That(mutated, Does.Contain(FillerA));
    }
}
