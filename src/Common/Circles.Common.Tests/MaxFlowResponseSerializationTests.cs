using System.Text.Json;
using Circles.Common.Dto;

namespace Circles.Common.Tests;

[TestFixture]
public sealed class MaxFlowResponseSerializationTests
{
    private static string Serialize(MaxFlowResponse r) =>
        JsonSerializer.Serialize(r);

    [Test]
    public void HealthyResponse_OmitsValidationFields()
    {
        var r = new MaxFlowResponse("0", new List<TransferPathStep>());
        var json = Serialize(r);

        Assert.That(json, Does.Not.Contain("validationErrors"));
        Assert.That(json, Does.Not.Contain("validationViolationRules"));
        Assert.That(json, Does.Not.Contain("validatorException"));
    }

    [Test]
    public void ValidationErrorsNonZero_SerializesCount()
    {
        var r = new MaxFlowResponse("0", new List<TransferPathStep>())
        {
            ValidationErrors = 2,
            ValidationViolationRules = new[] { "ScoreGroupMintLimitsHonored", "ApproveCRCRequired" }
        };
        var json = Serialize(r);

        Assert.That(json, Does.Contain("\"validationErrors\":2"));
        Assert.That(json, Does.Contain("\"validationViolationRules\""));
        Assert.That(json, Does.Contain("ScoreGroupMintLimitsHonored"));
        Assert.That(json, Does.Contain("ApproveCRCRequired"));
    }

    [Test]
    public void ValidatorExceptionTrue_SerializesFlag()
    {
        var r = new MaxFlowResponse("0", new List<TransferPathStep>())
        {
            ValidatorException = true
        };
        var json = Serialize(r);

        Assert.That(json, Does.Contain("\"validatorException\":true"));
    }

    [Test]
    public void NullViolationRules_Omitted_EvenWhenErrorsNonZero()
    {
        // Defensive shape: an "exception" path may set ValidationErrors without populating
        // the rule list. The list field must stay absent rather than serializing as null.
        var r = new MaxFlowResponse("0", new List<TransferPathStep>())
        {
            ValidationErrors = 1,
            ValidationViolationRules = null
        };
        var json = Serialize(r);

        Assert.That(json, Does.Contain("\"validationErrors\":1"));
        Assert.That(json, Does.Not.Contain("validationViolationRules"));
    }

    [Test]
    public void EmptyViolationRulesList_SerializesAsEmptyArray()
    {
        // Pins the load-bearing invariant documented on the property: producers are
        // expected to leave ValidationViolationRules null when zero violations occur.
        // An empty list WILL appear on the wire as []; this test makes that explicit so
        // an upstream change from `null` → `new List<string>()` is caught immediately.
        var r = new MaxFlowResponse("0", new List<TransferPathStep>())
        {
            ValidationViolationRules = new List<string>()
        };
        var json = Serialize(r);

        Assert.That(json, Does.Contain("\"validationViolationRules\":[]"));
    }

    [Test]
    public void ValidatorExceptionOnly_OmitsValidationErrorsZeroField()
    {
        // The "validator threw, errors stayed at default 0" combination produces a wire
        // shape with validatorException:true and no validationErrors field. Pinning this
        // so callers can distinguish "validator ran cleanly" (no fields) from "validator
        // threw" (validatorException:true alone).
        var r = new MaxFlowResponse("0", new List<TransferPathStep>())
        {
            ValidatorException = true
            // ValidationErrors stays default 0 — omitted by WhenWritingDefault
        };
        var json = Serialize(r);

        Assert.That(json, Does.Contain("\"validatorException\":true"));
        Assert.That(json, Does.Not.Contain("validationErrors"));
        Assert.That(json, Does.Not.Contain("validationViolationRules"));
    }

    [Test]
    public void RoundTrip_PreservesValidationFields()
    {
        // Pins both wire-name mapping and the deserialize path so the DTO can be consumed
        // by both producers and consumers (e.g. the integration tests that deserialize
        // MaxFlowResponse out of a JSON-RPC envelope).
        var original = new MaxFlowResponse("0", new List<TransferPathStep>())
        {
            ValidationErrors = 2,
            ValidationViolationRules = new[] { "ScoreGroupMintLimitsHonored", "ApproveCRCRequired" },
            ValidatorException = true
        };
        var json = Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<MaxFlowResponse>(json);

        Assert.That(roundTripped, Is.Not.Null);
        Assert.That(roundTripped!.ValidationErrors, Is.EqualTo(2));
        Assert.That(roundTripped.ValidationViolationRules, Is.EquivalentTo(original.ValidationViolationRules!));
        Assert.That(roundTripped.ValidatorException, Is.True);
    }
}
