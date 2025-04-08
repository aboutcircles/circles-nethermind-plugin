using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Tests.Fixtures;
using System.Text.Json;
using System.Reflection;
using Nethermind.Int256;

namespace Circles.Pathfinder.Tests.Utils;

/// <summary>
/// Utility for generating pathfinder tests from real-world examples
/// </summary>
public static class PathTestGenerator
{
    private static readonly string TestDataPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty,
        "TestData");

    /// <summary>
    /// Data structure for a complete path verification test
    /// </summary>
    public class PathVerificationTest
    {
        public required string Name { get; set; }
        public required FlowRequest FlowRequest { get; set; }
        public required MaxFlowResponse ExpectedResponse { get; set; }
        public DateTime Created { get; set; }
    }

    /// <summary>
    /// Creates a test case from your example JSON RPC request and response
    /// </summary>
    public static void CreateFromJsonRpcExample(string name, string jsonRpcRequest, string jsonRpcResponse)
    {
        // Extract the FlowRequest and MaxFlowResponse
        var flowRequest = JsonRpcTestExtractor.ExtractFlowRequestFromJsonRpc(jsonRpcRequest);
        var maxFlowResponse = JsonRpcTestExtractor.ExtractMaxFlowResponseFromJsonRpc(jsonRpcResponse);

        // Create test structure
        var testCase = new PathVerificationTest
        {
            Name = name,
            FlowRequest = flowRequest,
            ExpectedResponse = maxFlowResponse,
            Created = DateTime.Now
        };

        // Save the test case
        SaveTestCase(testCase);
    }

    /// <summary>
    /// Create a test case from the given inputs
    /// </summary>
    public static void CreateFromInputs(
        string name,
        string source,
        string sink,
        string targetFlow,
        List<string>? fromTokens = null,
        List<string>? toTokens = null,
        bool? withWrap = false,
        List<TransferPathStep>? expectedTransfers = null,
        string? expectedMaxFlow = null)
    {
        var flowRequest = new FlowRequest
        {
            Source = source,
            Sink = sink,
            TargetFlow = targetFlow,
            FromTokens = fromTokens,
            ToTokens = toTokens,
            WithWrap = withWrap
        };

        MaxFlowResponse expectedResponse;
        if (expectedTransfers != null && expectedMaxFlow != null)
        {
            expectedResponse = new MaxFlowResponse(expectedMaxFlow, expectedTransfers);
        }
        else
        {
            // Run the pathfinder to get the actual response
            var fixture = new PathfinderTestFixture();
            var pathfinder = new V2Pathfinder(fixture.GraphFactory);
            expectedResponse = pathfinder.ComputeMaxFlowWithData(
                fixture.BalanceGraph,
                fixture.TrustGraph,
                flowRequest,
                UInt256.Parse(targetFlow));

            // Save the network configuration that produced this result
            SaveNetworkForTestCase(name, fixture);
        }

        var testCase = new PathVerificationTest
        {
            Name = name,
            FlowRequest = flowRequest,
            ExpectedResponse = expectedResponse,
            Created = DateTime.Now
        };

        SaveTestCase(testCase);
    }

    /// <summary>
    /// Save a test case to file
    /// </summary>
    private static void SaveTestCase(PathVerificationTest test)
    {
        Directory.CreateDirectory(TestDataPath);
        var safeName = test.Name.Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
        var filename = $"path_test_{safeName}.json";
        var filePath = Path.Combine(TestDataPath, filename);

        var json = JsonSerializer.Serialize(test, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);

        Console.WriteLine($"Test case saved to: {filePath}");
    }

    /// <summary>
    /// Save network data that produced the test result
    /// </summary>
    private static void SaveNetworkForTestCase(string testName, PathfinderTestFixture fixture)
    {
        var safeName = testName.Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
        fixture.SaveCurrentNetwork(
            $"balances_{safeName}.csv",
            $"trust_{safeName}.csv");
    }
}