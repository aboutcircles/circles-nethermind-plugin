using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Tests.Fixtures;
using Circles.Pathfinder.Tests.Utils;
using Nethermind.Int256;
using System.Globalization;
using System.Text.Json;
using System.Reflection;

namespace Circles.Pathfinder.Tests;

[TestFixture]
public class PathVerificationTests
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

    /// <summary>
    /// Data structure to hold a test case with input and expected output
    /// </summary>
    public class PathTestCase
    {
        public required string Name { get; set; }
        public required FlowRequest Request { get; set; }
        public required MaxFlowResponse ExpectedResponse { get; set; }
    }

    [Test]
    public void GenerateTestCaseFile()
    {
        // This test generates a sample test case file that can be used as a template
        var testCase = new PathTestCase
        {
            Name = "Sample source to sink",
            Request = new FlowRequest
            {
                Source = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                Sink = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                TargetFlow = "1000000000000000000000",
                WithWrap = false
            },
            ExpectedResponse = new MaxFlowResponse(
                "1000000000000000000000",
                new List<TransferPathStep>
                {
                    new TransferPathStep
                    {
                        From = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                        To = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                        TokenOwner = "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c",
                        Value = "83211305000000000000"
                    },
                    new TransferPathStep
                    {
                        From = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                        To = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                        TokenOwner = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                        Value = "431913977000000000000"
                    },
                    new TransferPathStep
                    {
                        From = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                        To = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                        TokenOwner = "0x4a9affa9249f36fd0629f342c182a4e94a13c2e0",
                        Value = "484874718000000000000"
                    }
                }
            )
        };

        var testCases = new List<PathTestCase> { testCase };
        var json = JsonSerializer.Serialize(testCases, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(TestDataPath, "path_test_cases.json"), json);

        Assert.That(File.Exists(Path.Combine(TestDataPath, "path_test_cases.json")), Is.True);
    }

    [Test]
    public void RunPathVerificationTests()
    {
        // Skip test if test cases file doesn't exist
        var testCasesPath = Path.Combine(TestDataPath, "path_test_cases.json");
        if (!File.Exists(testCasesPath))
        {
            Assert.Ignore("Test cases file not found. Run GenerateTestCaseFile first.");
            return;
        }

        // Load the test cases
        var json = File.ReadAllText(testCasesPath);
        var testCases = JsonSerializer.Deserialize<List<PathTestCase>>(json);
        
        Assert.That(testCases, Is.Not.Null);
        Assert.That(testCases.Count, Is.GreaterThan(0));

        // Run each test case
        foreach (var testCase in testCases)
        {
            Console.WriteLine($"Running test case: {testCase.Name}");
            
            // Save the balances and trust relations needed for this test
            // (In a real implementation, you'd want to generate or load these based on the test case)
            
            var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
            
            // Run the pathfinder
            var result = pathfinder.ComputeMaxFlowWithData(
                _fixture.BalanceGraph,
                _fixture.TrustGraph,
                testCase.Request,
                UInt256.Parse(testCase.Request.TargetFlow));
            
            // Compare max flow
            Assert.That(result.MaxFlow, Is.EqualTo(testCase.ExpectedResponse.MaxFlow),
                $"Test case '{testCase.Name}': Max flow doesn't match expected value");

            // Compare number of transfers
            Assert.That(result.Transfers.Count, Is.EqualTo(testCase.ExpectedResponse.Transfers.Count),
                $"Test case '{testCase.Name}': Number of transfers doesn't match");
            
            // You may want to do more detailed comparison, considering that order might not matter
            // and that small variations in value might be acceptable

            // Sort both results and expected transfers by token owner for comparison
            var sortedResult = result.Transfers.OrderBy(t => t.TokenOwner).ToList();
            var sortedExpected = testCase.ExpectedResponse.Transfers.OrderBy(t => t.TokenOwner).ToList();
            
            // Compare each transfer
            for (int i = 0; i < sortedResult.Count; i++)
            {
                var actualTransfer = sortedResult[i];
                var expectedTransfer = sortedExpected[i];
                
                Assert.That(actualTransfer.From, Is.EqualTo(expectedTransfer.From),
                    $"Test case '{testCase.Name}': Transfer {i} From doesn't match");
                
                Assert.That(actualTransfer.To, Is.EqualTo(expectedTransfer.To),
                    $"Test case '{testCase.Name}': Transfer {i} To doesn't match");
                
                Assert.That(actualTransfer.TokenOwner, Is.EqualTo(expectedTransfer.TokenOwner),
                    $"Test case '{testCase.Name}': Transfer {i} TokenOwner doesn't match");
                
                // For token values, we might want to allow a small deviation (implementation specific)
                var actualValue = UInt256.Parse(actualTransfer.Value);
                var expectedValue = UInt256.Parse(expectedTransfer.Value);
                
                // Use exact comparison for now, but you could implement a tolerance if needed
                Assert.That(actualValue, Is.EqualTo(expectedValue),
                    $"Test case '{testCase.Name}': Transfer {i} Value doesn't match");
            }
            
            Console.WriteLine($"Test case '{testCase.Name}' passed!");
        }
    }

    [Test]
    public void CreateNetworkSnapshotForTestCase()
    {
        // This test is designed to create a network snapshot for a specific test case
        // Useful when you want to add a new test case based on real data
        
        // Define a sample test case
        var testCase = new PathTestCase
        {
            Name = "Specific path test case",
            Request = new FlowRequest
            {
                Source = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                Sink = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                TargetFlow = "1000000000000000000000",
                WithWrap = false
            },
            ExpectedResponse = new MaxFlowResponse("0", new List<TransferPathStep>())
        };
        
        // Initialize a pathfinder instance
        var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
        
        // Run the pathfinder to get the actual response
        var actualResponse = pathfinder.ComputeMaxFlowWithData(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            testCase.Request,
            UInt256.Parse(testCase.Request.TargetFlow));
        
        // Save the response as the expected result
        testCase.ExpectedResponse = actualResponse;
        
        // Save the complete test case
        var testCases = new List<PathTestCase> { testCase };
        var json = JsonSerializer.Serialize(testCases, new JsonSerializerOptions { WriteIndented = true });
        
        var filename = $"test_case_{testCase.Request.Source}_{testCase.Request.Sink}.json";
        File.WriteAllText(Path.Combine(TestDataPath, filename), json);
        
        // Save the network snapshot that produced this result
        _fixture.SaveCurrentNetwork(
            $"balances_{testCase.Request.Source}_{testCase.Request.Sink}.csv",
            $"trust_{testCase.Request.Source}_{testCase.Request.Sink}.csv");
        
        Console.WriteLine($"Test case saved to: {filename}");
        Assert.That(File.Exists(Path.Combine(TestDataPath, filename)), Is.True);
    }

    [Test]
    public void VerifySpecificPathExample()
    {
        // This test directly verifies the example you provided
        var request = new FlowRequest
        {
            Source = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
            Sink = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
            TargetFlow = "1000000000000000000000",
            WithWrap = false
        };

        var expectedTransfers = new List<TransferPathStep>
        {
            new TransferPathStep
            {
                From = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                To = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                TokenOwner = "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c",
                Value = "83211305000000000000"
            },
            new TransferPathStep
            {
                From = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                To = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                TokenOwner = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                Value = "431913977000000000000"
            },
            new TransferPathStep
            {
                From = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                To = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
                TokenOwner = "0x4a9affa9249f36fd0629f342c182a4e94a13c2e0",
                Value = "484874718000000000000"
            }
        };

        // Try to load a specific network snapshot for this test
        try
        {
            _fixture.LoadNetwork(
                $"balances_{request.Source}_{request.Sink}.csv",
                $"trust_{request.Source}_{request.Sink}.csv");
        }
        catch (FileNotFoundException)
        {
            // If no specific snapshot exists, we'll just create a test network
            // This won't match the expected results, but it allows the test to run
            Console.WriteLine("No specific network snapshot found. Using default test network.");
        }

        // Run the test
        var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
        var result = pathfinder.ComputeMaxFlowWithData(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            request,
            UInt256.Parse(request.TargetFlow));

        // Check high-level properties first
        Assert.That(result.MaxFlow, Is.EqualTo("1000000000000000000000"),
            "Max flow doesn't match expected value");

        // If we are using a specific network snapshot, we can compare the details
        if (File.Exists(Path.Combine(TestDataPath, $"balances_{request.Source}_{request.Sink}.csv")))
        {
            Assert.That(result.Transfers.Count, Is.EqualTo(expectedTransfers.Count),
                "Number of transfers doesn't match");

            // Sort both lists by token owner for consistent comparison
            var sortedResult = result.Transfers.OrderBy(t => t.TokenOwner).ToList();
            var sortedExpected = expectedTransfers.OrderBy(t => t.TokenOwner).ToList();

            // Compare each transfer
            for (int i = 0; i < sortedResult.Count; i++)
            {
                Assert.That(sortedResult[i].From, Is.EqualTo(sortedExpected[i].From));
                Assert.That(sortedResult[i].To, Is.EqualTo(sortedExpected[i].To));
                Assert.That(sortedResult[i].TokenOwner, Is.EqualTo(sortedExpected[i].TokenOwner));
                Assert.That(sortedResult[i].Value, Is.EqualTo(sortedExpected[i].Value));
            }
        }
        else
        {
            Console.WriteLine("Skipping detailed transfer comparison since no specific network snapshot was loaded.");
            Console.WriteLine("Run the CreateNetworkSnapshotForTestCase test first to create the required network snapshot.");
        }
    }

    [Test]
    public void VerifyFindPathEndpoint()
    {
        // This test simulates the findPath endpoint
        // In a real implementation, this would call the API directly or mock the controller
        
        var request = new FlowRequest
        {
            Source = "0xde374ece6fa50e781e81aac78e811b33d16912c7",
            Sink = "0x14c16ce62d26fd51582a646e2e30a3267b1e6d7e",
            TargetFlow = "1000000000000000000000",
            WithWrap = false
        };
        
        var expectedJson = @"{
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
}";
        
        try
        {
            // Try to load the specific network for this test
            _fixture.LoadNetwork(
                $"balances_{request.Source}_{request.Sink}.csv",
                $"trust_{request.Source}_{request.Sink}.csv");
            
            // Run the pathfinder
            var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
            var result = pathfinder.ComputeMaxFlowWithData(
                _fixture.BalanceGraph,
                _fixture.TrustGraph,
                request,
                UInt256.Parse(request.TargetFlow));
            
            // Compare with expected JSON
            var expectedResponse = JsonSerializer.Deserialize<MaxFlowResponse>(expectedJson);
            Assert.That(expectedResponse, Is.Not.Null);
            
            // Compare max flow
            Assert.That(result.MaxFlow, Is.EqualTo(expectedResponse.MaxFlow));
            
            // Compare transfers
            Assert.That(result.Transfers.Count, Is.EqualTo(expectedResponse.Transfers.Count));
            
            // Sort both lists by token owner for consistent comparison
            var sortedResult = result.Transfers.OrderBy(t => t.TokenOwner).ToList();
            var sortedExpected = expectedResponse.Transfers.OrderBy(t => t.TokenOwner).ToList();
            
            // Compare each transfer
            for (int i = 0; i < sortedResult.Count; i++)
            {
                Assert.That(sortedResult[i].From, Is.EqualTo(sortedExpected[i].From));
                Assert.That(sortedResult[i].To, Is.EqualTo(sortedExpected[i].To));
                Assert.That(sortedResult[i].TokenOwner, Is.EqualTo(sortedExpected[i].TokenOwner));
                Assert.That(sortedResult[i].Value, Is.EqualTo(sortedExpected[i].Value));
            }
        }
        catch (FileNotFoundException)
        {
            Assert.Ignore("Network snapshot not found. Run CreateNetworkSnapshotForTestCase first.");
        }
    }
}