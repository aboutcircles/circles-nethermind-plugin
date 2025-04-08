using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Tests.Fixtures;
using Circles.Pathfinder.Tests.Utils;
using Nethermind.Int256;
using System.Text.Json;
using System.Reflection;

namespace Circles.Pathfinder.Tests;

[TestFixture]
public class PathVerificationRunner
{
    private PathfinderTestFixture _fixture;
    private static readonly string TestDataPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty,
        "TestData");

    [SetUp]
    public void Setup()
    {
        _fixture = new PathfinderTestFixture();
        Directory.CreateDirectory(TestDataPath);
    }

    [Test]
    public void CreateExampleTestCase()
    {
        // This example creates a test case from the JSON RPC example you provided
        
        // Define the JSON RPC request and response
        string jsonRpcRequest = @"{
            ""jsonrpc"":""2.0"",
            ""id"":0,
            ""method"":""circlesV2_findPath"",
            ""params"":[{
                ""Source"":""0xde374ece6fa50e781e81aac78e811b33d16912c7"",
                ""Sink"":""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
                ""TargetFlow"":""1000000000000000000000"",
                ""WithWrap"":false
            }]
        }";

        string jsonRpcResponse = @"{
            ""jsonrpc"": ""2.0"",
            ""result"": {
                ""maxFlow"": ""1000000000000000000000"",
                ""transfers"": [
                    {
                        ""from"": ""0xde374ece6fa50e781e81aac78e811b33d16912c7"",
                        ""to"": ""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
                        ""tokenOwner"": ""0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c"",
                        ""value"": ""83211305000000000000""
                    },
                    {
                        ""from"": ""0xde374ece6fa50e781e81aac78e811b33d16912c7"",
                        ""to"": ""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
                        ""tokenOwner"": ""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
                        ""value"": ""431913977000000000000""
                    },
                    {
                        ""from"": ""0xde374ece6fa50e781e81aac78e811b33d16912c7"",
                        ""to"": ""0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e"",
                        ""tokenOwner"": ""0x4a9affa9249f36fd0629f342c182a4e94a13c2e0"",
                        ""value"": ""484874718000000000000""
                    }
                ]
            },
            ""id"": 0
        }";

        // Create a test case from the JSON RPC
        PathTestGenerator.CreateFromJsonRpcExample("Example JSON RPC Test", jsonRpcRequest, jsonRpcResponse);

        // Verify the test case file was created
        var testFiles = Directory.GetFiles(TestDataPath, "path_test_*.json");
        Assert.That(testFiles.Length, Is.GreaterThan(0));
        
        Console.WriteLine($"Test case created at: {testFiles.First()}");
    }

    [Test]
    public void RunAllVerificationTests()
    {
        // Find all test case files
        var testFiles = Directory.GetFiles(TestDataPath, "path_test_*.json");
        
        if (testFiles.Length == 0)
        {
            Assert.Ignore("No test cases found. Run CreateExampleTestCase first.");
            return;
        }

        Console.WriteLine($"Found {testFiles.Length} test cases:");
        foreach (var file in testFiles)
        {
            Console.WriteLine($"  - {Path.GetFileName(file)}");
        }

        // Run each test
        foreach (var testFile in testFiles)
        {
            try
            {
                // Load the test case
                var json = File.ReadAllText(testFile);
                var test = JsonSerializer.Deserialize<PathTestGenerator.PathVerificationTest>(json);
                
                if (test == null)
                {
                    Console.WriteLine($"Failed to load test case from {testFile}");
                    continue;
                }
                
                Console.WriteLine($"Running test: {test.Name}");
                
                // Try to load the specific network snapshot for this test
                var safeName = test.Name.Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
                
                try
                {
                    _fixture.LoadNetwork(
                        $"balances_{safeName}.csv",
                        $"trust_{safeName}.csv");
                    Console.WriteLine($"Loaded network snapshot for test: {test.Name}");
                }
                catch (FileNotFoundException)
                {
                    Console.WriteLine($"No specific network snapshot found for test: {test.Name}");
                    Console.WriteLine("Test will run with default network, which may not match expected results.");
                }
                
                // Run the pathfinder
                var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
                var result = pathfinder.ComputeMaxFlowWithData(
                    _fixture.BalanceGraph,
                    _fixture.TrustGraph,
                    test.FlowRequest,
                    UInt256.Parse(test.FlowRequest.TargetFlow ?? "0"));
                
                // Compare results
                Console.WriteLine($"Expected Max Flow: {test.ExpectedResponse.MaxFlow}");
                Console.WriteLine($"Actual Max Flow: {result.MaxFlow}");
                
                // Basic verification - max flow should match
                Assert.That(result.MaxFlow, Is.EqualTo(test.ExpectedResponse.MaxFlow), 
                    $"Max flow mismatch for test: {test.Name}");
                
                // Detailed verification - only if we have the correct network snapshot
                if (File.Exists(Path.Combine(TestDataPath, $"balances_{safeName}.csv")))
                {
                    // Verify the number of transfers
                    Assert.That(result.Transfers.Count, Is.EqualTo(test.ExpectedResponse.Transfers.Count),
                        $"Number of transfers mismatch for test: {test.Name}");
                    
                    // Sort both results for consistent comparison
                    var sortedExpected = test.ExpectedResponse.Transfers
                        .OrderBy(t => t.TokenOwner)
                        .ThenBy(t => t.Value)
                        .ToList();
                    
                    var sortedResult = result.Transfers
                        .OrderBy(t => t.TokenOwner)
                        .ThenBy(t => t.Value)
                        .ToList();
                    
                    // Compare each transfer
                    for (int i = 0; i < sortedResult.Count; i++)
                    {
                        Assert.That(sortedResult[i].From, Is.EqualTo(sortedExpected[i].From),
                            $"Transfer {i} From mismatch for test: {test.Name}");
                        
                        Assert.That(sortedResult[i].To, Is.EqualTo(sortedExpected[i].To),
                            $"Transfer {i} To mismatch for test: {test.Name}");
                        
                        Assert.That(sortedResult[i].TokenOwner, Is.EqualTo(sortedExpected[i].TokenOwner),
                            $"Transfer {i} TokenOwner mismatch for test: {test.Name}");
                        
                        Assert.That(sortedResult[i].Value, Is.EqualTo(sortedExpected[i].Value),
                            $"Transfer {i} Value mismatch for test: {test.Name}");
                    }
                    
                    Console.WriteLine($"Test {test.Name} passed with full verification!");
                }
                else
                {
                    Console.WriteLine($"Test {test.Name} passed basic verification (max flow only).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error running test from {testFile}: {ex.Message}");
                Assert.Fail($"Test from {testFile} failed: {ex.Message}");
            }
        }
    }
    
    [Test]
    public void VerifyPathFromExample()
    {
        // This test uses the exact example you provided with hardcoded values
        var source = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var sink = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e";
        var targetFlow = "1000000000000000000000";
        
        var expectedTransfers = new List<TransferPathStep>
        {
            new TransferPathStep
            {
                From = source,
                To = sink,
                TokenOwner = "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c",
                Value = "83211305000000000000"
            },
            new TransferPathStep
            {
                From = source,
                To = sink,
                TokenOwner = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                Value = "431913977000000000000"
            },
            new TransferPathStep
            {
                From = source,
                To = sink,
                TokenOwner = "0x4a9affa9249f36fd0629f342c182a4e94a13c2e0",
                Value = "484874718000000000000"
            }
        };
        
        // Try to load a specific network for this example
        var networkFilename = $"balances_{source}_{sink}.csv";
        var trustFilename = $"trust_{source}_{sink}.csv";
        
        if (File.Exists(Path.Combine(TestDataPath, networkFilename)) && 
            File.Exists(Path.Combine(TestDataPath, trustFilename)))
        {
            _fixture.LoadNetwork(networkFilename, trustFilename);
            Console.WriteLine("Loaded specific network for this example");
        }
        else
        {
            Console.WriteLine("No specific network found for this example. Using default test network.");
            Console.WriteLine("This may cause the test to fail if the default network doesn't match the expected transfers.");
        }
        
        // Run the pathfinder
        var request = new FlowRequest
        {
            Source = source,
            Sink = sink,
            TargetFlow = targetFlow,
            WithWrap = false
        };
        
        var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
        var result = pathfinder.ComputeMaxFlowWithData(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            request,
            UInt256.Parse(targetFlow));
        
        // Verify max flow
        Assert.That(result.MaxFlow, Is.EqualTo(targetFlow), "Max flow doesn't match");
        
        // Only verify transfers if we have the specific network
        if (File.Exists(Path.Combine(TestDataPath, networkFilename)))
        {
            // Verify number of transfers
            Assert.That(result.Transfers.Count, Is.EqualTo(expectedTransfers.Count), 
                "Number of transfers doesn't match");
            
            // Sort for consistent comparison
            var sortedExpected = expectedTransfers
                .OrderBy(t => t.TokenOwner)
                .ThenBy(t => t.Value)
                .ToList();
            
            var sortedResult = result.Transfers
                .OrderBy(t => t.TokenOwner)
                .ThenBy(t => t.Value)
                .ToList();
            
            // Compare each transfer
            for (int i = 0; i < sortedResult.Count; i++)
            {
                Assert.That(sortedResult[i].From, Is.EqualTo(sortedExpected[i].From),
                    $"Transfer {i} From doesn't match");
                Assert.That(sortedResult[i].To, Is.EqualTo(sortedExpected[i].To),
                    $"Transfer {i} To doesn't match");
                Assert.That(sortedResult[i].TokenOwner, Is.EqualTo(sortedExpected[i].TokenOwner),
                    $"Transfer {i} TokenOwner doesn't match");
                Assert.That(sortedResult[i].Value, Is.EqualTo(sortedExpected[i].Value),
                    $"Transfer {i} Value doesn't match");
            }
            
            Console.WriteLine("All transfers verified successfully!");
        }
    }
    
    [Test]
    public void CreateTestCaseFromParams()
    {
        // This test allows you to manually create a test case
        
        var source = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
        var sink = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e";
        var targetFlow = "1000000000000000000000";
        
        var expectedTransfers = new List<TransferPathStep>
        {
            new TransferPathStep
            {
                From = source,
                To = sink,
                TokenOwner = "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c",
                Value = "83211305000000000000"
            },
            new TransferPathStep
            {
                From = source,
                To = sink,
                TokenOwner = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                Value = "431913977000000000000"
            },
            new TransferPathStep
            {
                From = source,
                To = sink,
                TokenOwner = "0x4a9affa9249f36fd0629f342c182a4e94a13c2e0",
                Value = "484874718000000000000"
            }
        };
        
        // Create the test case
        PathTestGenerator.CreateFromInputs(
            "Manual Test Case",
            source,
            sink,
            targetFlow,
            null,  // fromTokens
            null,  // toTokens
            false, // withWrap
            expectedTransfers,
            targetFlow // expectedMaxFlow
        );
        
        Console.WriteLine("Test case created successfully!");
    }
}