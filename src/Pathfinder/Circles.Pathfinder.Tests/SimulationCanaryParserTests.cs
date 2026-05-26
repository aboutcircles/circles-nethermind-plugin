using System.Text.Json;
using Circles.Pathfinder.Host.Canary;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for SimulationCanaryService.ParseSimulateV1Response — the pure parser
/// that classifies eth_simulateV1 bundle responses into a discriminated outcome.
/// Critical: a truncated response (node returns fewer call results than sent) must
/// never silently land in the success bucket.
/// </summary>
[TestFixture, Parallelizable]
public class SimulationCanaryParserTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    #region Top-level error

    [Test]
    public void TopLevelError_MethodNotFound_ReturnsMethodNotSupported()
    {
        var json = Parse(@"{""jsonrpc"":""2.0"",""id"":1,""error"":{""code"":-32601,""message"":""Method not found""}}");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 2);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.MethodNotSupported));
        Assert.That(result.ErrorCode, Is.EqualTo(-32601));
        Assert.That(result.RevertMessage, Is.EqualTo("Method not found"));
    }

    [Test]
    public void TopLevelError_OtherCode_ReturnsTopLevelError()
    {
        var json = Parse(@"{""jsonrpc"":""2.0"",""id"":1,""error"":{""code"":-32000,""message"":""bad input""}}");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 2);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.TopLevelError));
        Assert.That(result.ErrorCode, Is.EqualTo(-32000));
    }

    [Test]
    public void TopLevelError_NonNumberCode_DoesNotThrow()
    {
        // Defensive: some nodes have sent string codes. TryGetInt32 must not propagate.
        var json = Parse(@"{""jsonrpc"":""2.0"",""id"":1,""error"":{""code"":""32601"",""message"":""bad node""}}");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 2);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.TopLevelError));
        Assert.That(result.ErrorCode, Is.EqualTo(0), "Non-numeric code falls back to 0 (no -32xxx semantics)");
    }

    [Test]
    public void TopLevelError_MissingCode_DoesNotThrow()
    {
        var json = Parse(@"{""jsonrpc"":""2.0"",""id"":1,""error"":{""message"":""mystery""}}");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 1);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.TopLevelError));
        Assert.That(result.ErrorCode, Is.EqualTo(0));
    }

    #endregion

    #region Empty / malformed

    [Test]
    public void NoResultField_ReturnsEmptyResponse()
    {
        var json = Parse(@"{""jsonrpc"":""2.0"",""id"":1}");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 1);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.EmptyResponse));
    }

    [Test]
    public void EmptyResultArray_ReturnsEmptyResponse()
    {
        var json = Parse(@"{""jsonrpc"":""2.0"",""id"":1,""result"":[]}");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 1);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.EmptyResponse));
    }

    [Test]
    public void MissingCallsArray_ReturnsMissingCallsArray()
    {
        var json = Parse(@"{""jsonrpc"":""2.0"",""id"":1,""result"":[{""number"":""0x1""}]}");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 1);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.MissingCallsArray));
    }

    #endregion

    #region Truncated — the deploy-blocker the review caught

    [Test]
    public void TruncatedResponse_ReturnsTruncated_NeverSuccess()
    {
        // Sent 3 calls (2 unwraps + 1 flow matrix), node returned 1 successful entry.
        // Must NOT fold into success.
        var json = Parse(@"{
            ""jsonrpc"":""2.0"",""id"":1,
            ""result"":[{""calls"":[{""status"":""0x1""}]}]
        }");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 3);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.Truncated),
            "Truncated bundle responses must not be classified as success");
    }

    [Test]
    public void TruncatedResponse_OneShort_StillTruncated()
    {
        var json = Parse(@"{
            ""jsonrpc"":""2.0"",""id"":1,
            ""result"":[{""calls"":[{""status"":""0x1""},{""status"":""0x1""}]}]
        }");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 3);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.Truncated));
    }

    [Test]
    public void ExactlyExpectedCount_AllSuccess_ReturnsSuccess()
    {
        var json = Parse(@"{
            ""jsonrpc"":""2.0"",""id"":1,
            ""result"":[{""calls"":[{""status"":""0x1""},{""status"":""0x1""},{""status"":""0x1""}]}]
        }");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 3);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.Success));
    }

    [Test]
    public void ExtraCallsBeyondExpected_AreIgnored_NotTruncated()
    {
        // Node returned MORE entries than we sent (unusual, but should be tolerated —
        // we only inspect indices [0, expectedCalls)).
        var json = Parse(@"{
            ""jsonrpc"":""2.0"",""id"":1,
            ""result"":[{""calls"":[
                {""status"":""0x1""},{""status"":""0x1""},{""status"":""0x1""},{""status"":""0x1""}
            ]}]
        }");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 3);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.Success));
    }

    #endregion

    #region Revert classification

    [Test]
    public void FirstUnwrapReverts_StageIsUnwrap0()
    {
        var json = Parse(@"{
            ""jsonrpc"":""2.0"",""id"":1,
            ""result"":[{""calls"":[
                {""status"":""0x0"",""error"":{""data"":""0x03dee4c5"",""message"":""insufficient""}},
                {""status"":""0x1""},
                {""status"":""0x1""}
            ]}]
        }");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 3);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.Revert));
        Assert.That(result.Stage, Is.EqualTo("unwrap_0"));
        Assert.That(result.RevertData, Is.EqualTo("0x03dee4c5"));
        Assert.That(result.RevertMessage, Is.EqualTo("insufficient"));
    }

    [Test]
    public void SecondUnwrapReverts_StageIsUnwrap1()
    {
        var json = Parse(@"{
            ""jsonrpc"":""2.0"",""id"":1,
            ""result"":[{""calls"":[
                {""status"":""0x1""},
                {""status"":""0x0"",""error"":{""data"":""0xc14c0700""}},
                {""status"":""0x1""}
            ]}]
        }");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 3);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.Revert));
        Assert.That(result.Stage, Is.EqualTo("unwrap_1"));
    }

    [Test]
    public void FlowMatrixReverts_StageIsFlowMatrix()
    {
        var json = Parse(@"{
            ""jsonrpc"":""2.0"",""id"":1,
            ""result"":[{""calls"":[
                {""status"":""0x1""},
                {""status"":""0x0"",""error"":{""data"":""0x5e418dba""}}
            ]}]
        }");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 2);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.Revert));
        Assert.That(result.Stage, Is.EqualTo("flow_matrix"));
    }

    [Test]
    public void StatusMissing_TreatedAsRevert_NotSuccess()
    {
        // Defensive: if the node omits `status` entirely, fail closed.
        var json = Parse(@"{
            ""jsonrpc"":""2.0"",""id"":1,
            ""result"":[{""calls"":[
                {""returnData"":""0x""}
            ]}]
        }");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 1);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.Revert));
        Assert.That(result.Stage, Is.EqualTo("flow_matrix"));
    }

    [Test]
    public void ReturnDataFallback_WhenErrorMissing()
    {
        // Some nodes report revert payload in returnData on the call itself, not in an error field.
        var json = Parse(@"{
            ""jsonrpc"":""2.0"",""id"":1,
            ""result"":[{""calls"":[
                {""status"":""0x0"",""returnData"":""0x03dee4c5deadbeef""}
            ]}]
        }");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 1);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.Revert));
        Assert.That(result.RevertData, Is.EqualTo("0x03dee4c5deadbeef"));
    }

    [Test]
    public void OnlyFirstRevertSurfaces()
    {
        // If multiple calls report revert, we want the FIRST one (closest to the cause).
        var json = Parse(@"{
            ""jsonrpc"":""2.0"",""id"":1,
            ""result"":[{""calls"":[
                {""status"":""0x1""},
                {""status"":""0x0"",""error"":{""data"":""0xAAAA""}},
                {""status"":""0x0"",""error"":{""data"":""0xBBBB""}}
            ]}]
        }");

        var result = SimulationCanaryService.ParseSimulateV1Response(json, expectedCalls: 3);

        Assert.That(result.Outcome, Is.EqualTo(SimulationCanaryService.BundleOutcome.Revert));
        Assert.That(result.Stage, Is.EqualTo("unwrap_1"));
        Assert.That(result.RevertData, Is.EqualTo("0xAAAA"));
    }

    #endregion
}
