using Circles.Pathfinder.DTOs;
using Circles.Pathfinder.Tests.Fixtures;
using Circles.Pathfinder.Tests.Utils;
using Nethermind.Int256;
using System.Reflection;

namespace Circles.Pathfinder.Tests;

[TestFixture]
[Category("SnapshotTests")]
public class SnapshotTests
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
    [Category("SnapshotCreation")]
    public void CreateNetworkSnapshot_From_ExistingFixture()
    {
        // This test creates a snapshot of the current test fixture data
        // It can be used to create a baseline for regression testing
        
        var balancesFile = "snapshot_balances.csv";
        var trustFile = "snapshot_trust.csv";
        
        _fixture.SaveCurrentNetwork(balancesFile, trustFile);
        
        // Verify files exist
        Assert.That(File.Exists(Path.Combine(TestDataPath, balancesFile)), Is.True);
        Assert.That(File.Exists(Path.Combine(TestDataPath, trustFile)), Is.True);
    }

    [Test]
    [Category("SnapshotTest")]
    public void RunTestWithSnapshot_IfExists()
    {
        var balancesFile = "snapshot_balances.csv";
        var trustFile = "snapshot_trust.csv";
        
        // Skip test if snapshot doesn't exist
        if (!File.Exists(Path.Combine(TestDataPath, balancesFile)) || 
            !File.Exists(Path.Combine(TestDataPath, trustFile)))
        {
            Assert.Ignore("Snapshot files not found. Run CreateNetworkSnapshot_From_ExistingFixture first.");
        }
        
        try
        {
            // Load network from snapshot
            _fixture.LoadNetwork(balancesFile, trustFile);
            
            // Run a sample pathfinding test
            var sourceAddress = _fixture.Balances[0].Account;
            var sinkAddress = _fixture.Balances[1].Account;
            var targetFlow = "1000000000000000000"; // 1 token
            
            var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
            
            // Act
            var result = pathfinder.ComputeMaxFlowWithData(
                _fixture.BalanceGraph,
                _fixture.TrustGraph,
                TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, targetFlow),
                UInt256.Parse(targetFlow));
            
            // Assert - basic validation
            Assert.That(result, Is.Not.Null);
            
            // Advanced validation would depend on the specific snapshot
        }
        catch (FileNotFoundException)
        {
            Assert.Ignore("Snapshot files could not be loaded. Run CreateNetworkSnapshot_From_ExistingFixture first.");
        }
    }

    [Test]
    [Category("RegressionTest")]
    public void RunRegressionTest_WithKnownPath()
    {
        var balancesFile = "regression_balances.csv";
        var trustFile = "regression_trust.csv";
        
        // Skip test if snapshot doesn't exist
        if (!File.Exists(Path.Combine(TestDataPath, balancesFile)) || 
            !File.Exists(Path.Combine(TestDataPath, trustFile)))
        {
            // Create a regression test network
            var (balances, trust) = TestUtils.CreateLinearNetwork(5, 1000000000);
            
            // Add a known good test case
            var testSourceAddress = "0x0000000000000000000000000000000000000000";
            var testSinkAddress = "0x0000000000000000000000000000000000000004";
            
            TestUtils.SaveNetworkSnapshot(balances, trust, 
                Path.Combine(TestDataPath, balancesFile),
                Path.Combine(TestDataPath, trustFile));
        }
        
        // Load the regression test network
        _fixture.LoadNetwork(balancesFile, trustFile);
        
        // Define the test parameters
        var sourceAddress = "0x0000000000000000000000000000000000000000";
        var sinkAddress = "0x0000000000000000000000000000000000000004";
        var targetFlow = "100000000";
        
        var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
        
        // Act
        var result = pathfinder.ComputeMaxFlowWithData(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, targetFlow),
            UInt256.Parse(targetFlow));
        
        // Assert - we should find a path in our linear network
        Assert.That(result.MaxFlow, Is.Not.EqualTo("0"));
        Assert.That(result.Transfers.Count, Is.GreaterThan(0));
        
        // The linear network should require 4 transfers from 0->1->2->3->4
        Assert.That(result.Transfers.Count, Is.EqualTo(4));
    }

    [Test]
    [Category("Benchmark")]
    public void RunPerformanceBenchmark()
    {
        // Create a dense network for performance testing
        var nodeCount = 20; // A moderately sized network
        _fixture.ReconfigureWithTestNetwork(nodeCount);
        
        var sourceAddress = "0x0000000000000000000000000000000000000000";
        var sinkAddress = "0x0000000000000000000000000000000000000001";
        var targetFlow = "10000000000";
        
        var pathfinder = new V2Pathfinder(_fixture.GraphFactory);
        
        // Measure performance
        var stopwatch = new System.Diagnostics.Stopwatch();
        stopwatch.Start();
        
        var result = pathfinder.ComputeMaxFlowWithData(
            _fixture.BalanceGraph,
            _fixture.TrustGraph,
            TestUtils.CreateFlowRequest(sourceAddress, sinkAddress, targetFlow),
            UInt256.Parse(targetFlow));
        
        stopwatch.Stop();
        
        // Log the performance
        Console.WriteLine($"Network size: {nodeCount} nodes");
        Console.WriteLine($"Balance nodes: {_fixture.BalanceGraph.BalanceNodes.Count}");
        Console.WriteLine($"Trust edges: {_fixture.TrustGraph.Edges.Count}");
        Console.WriteLine($"Time to compute max flow: {stopwatch.ElapsedMilliseconds}ms");
        Console.WriteLine($"Flow result: {result.MaxFlow}");
        Console.WriteLine($"Transfer steps: {result.Transfers.Count}");
        
        // No assertion - this is just for benchmarking
    }
}