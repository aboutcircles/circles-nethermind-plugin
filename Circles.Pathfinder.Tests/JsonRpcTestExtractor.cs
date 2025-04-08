using Circles.Pathfinder.DTOs;
using System.Text.Json;
using System.Reflection;

namespace Circles.Pathfinder.Tests.Utils;

/// <summary>
/// Utility class for extracting test cases from JSON RPC request/response pairs.
/// </summary>
public static class JsonRpcTestExtractor
{
    private static readonly string TestDataPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty,
        "TestData");

    /// <summary>
    /// Data structure for a JSON RPC request
    /// </summary>
    public class JsonRpcRequest
    {
        public required string Jsonrpc { get; set; }
        public int Id { get; set; }
        public required string Method { get; set; }
        public required object[] Params { get; set; }
    }

    /// <summary>
    /// Data structure for a JSON RPC response
    /// </summary>
    public class JsonRpcResponse
    {
        public required string Jsonrpc { get; set; }
        public int Id { get; set; }
        public required MaxFlowResponse Result { get; set; }
    }

    /// <summary>
    /// Data structure for a complete JSON RPC test case
    /// </summary>
    public class JsonRpcTestCase
    {
        public required string Name { get; set; }
        public required JsonRpcRequest Request { get; set; }
        public required JsonRpcResponse Response { get; set; }
    }

    /// <summary>
    /// Extracts a FlowRequest from a JSON RPC request string
    /// </summary>
    public static FlowRequest ExtractFlowRequestFromJsonRpc(string jsonRpcRequest)
    {
        var request = JsonSerializer.Deserialize<JsonRpcRequest>(jsonRpcRequest);
        if (request == null || request.Params == null || request.Params.Length == 0)
        {
            throw new ArgumentException("Invalid JSON RPC request format");
        }

        // The first parameter should be the FlowRequest
        var flowRequestJson = JsonSerializer.Serialize(request.Params[0]);
        return JsonSerializer.Deserialize<FlowRequest>(flowRequestJson);
    }

    /// <summary>
    /// Extracts a MaxFlowResponse from a JSON RPC response string
    /// </summary>
    public static MaxFlowResponse ExtractMaxFlowResponseFromJsonRpc(string jsonRpcResponse)
    {
        var response = JsonSerializer.Deserialize<JsonRpcResponse>(jsonRpcResponse);
        if (response == null || response.Result == null)
        {
            throw new ArgumentException("Invalid JSON RPC response format");
        }

        return response.Result;
    }

    /// <summary>
    /// Creates a test case from JSON RPC request and response strings
    /// </summary>
    public static void CreateTestCaseFromJsonRpc(
        string name,
        string jsonRpcRequest,
        string jsonRpcResponse,
        bool saveToFile = true)
    {
        var request = JsonSerializer.Deserialize<JsonRpcRequest>(jsonRpcRequest);
        var response = JsonSerializer.Deserialize<JsonRpcResponse>(jsonRpcResponse);

        if (request == null || response == null)
        {
            throw new ArgumentException("Invalid JSON RPC format");
        }

        var testCase = new JsonRpcTestCase
        {
            Name = name,
            Request = request,
            Response = response
        };

        if (saveToFile)
        {
            Directory.CreateDirectory(TestDataPath);
            var json = JsonSerializer.Serialize(testCase, new JsonSerializerOptions { WriteIndented = true });
            var filename = $"jsonrpc_test_{name.Replace(" ", "_")}.json";
            File.WriteAllText(Path.Combine(TestDataPath, filename), json);
        }
    }

    /// <summary>
    /// Converts a FlowRequest to a JSON RPC request string
    /// </summary>
    public static string CreateJsonRpcRequestFromFlowRequest(FlowRequest flowRequest, int id = 0)
    {
        var jsonRpcRequest = new JsonRpcRequest
        {
            Jsonrpc = "2.0",
            Id = id,
            Method = "circlesV2_findPath",
            Params = new object[] { flowRequest }
        };

        return JsonSerializer.Serialize(jsonRpcRequest, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Converts a MaxFlowResponse to a JSON RPC response string
    /// </summary>
    public static string CreateJsonRpcResponseFromMaxFlowResponse(MaxFlowResponse maxFlowResponse, int id = 0)
    {
        var jsonRpcResponse = new JsonRpcResponse
        {
            Jsonrpc = "2.0",
            Id = id,
            Result = maxFlowResponse
        };

        return JsonSerializer.Serialize(jsonRpcResponse, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Creates a JSON RPC test file from example text
    /// </summary>
    public static void CreateTestCaseFromExampleText(
        string name,
        string requestText,
        string responseText)
    {
        // This method accepts plain text copies of request and response and tries to extract the JSON
        try
        {
            // Clean up the texts to extract just the JSON
            string requestJson = ExtractJsonFromText(requestText);
            string responseJson = ExtractJsonFromText(responseText);

            CreateTestCaseFromJsonRpc(name, requestJson, responseJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating test case: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts a JSON string from text that might contain additional content
    /// </summary>
    private static string ExtractJsonFromText(string text)
    {
        // Look for the start of a JSON object
        int startIndex = text.IndexOf('{');
        if (startIndex < 0)
        {
            throw new ArgumentException("No JSON object found in text");
        }

        // Find the matching closing brace
        int braceCount = 1;
        int endIndex = startIndex + 1;

        while (braceCount > 0 && endIndex < text.Length)
        {
            if (text[endIndex] == '{')
            {
                braceCount++;
            }
            else if (text[endIndex] == '}')
            {
                braceCount--;
            }
            endIndex++;
        }

        if (braceCount != 0)
        {
            throw new ArgumentException("Unbalanced braces in JSON text");
        }

        return text.Substring(startIndex, endIndex - startIndex);
    }
}