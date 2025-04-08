using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Tests.Utils;
using System.Reflection;

namespace Circles.Pathfinder.Tests.Fixtures;

/// <summary>
/// Base test fixture for pathfinder tests that handles loading test data and setting up the graphs.
/// </summary>
public class PathfinderTestFixture
{
    // Paths to test data files
    private static readonly string TestDataPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty,
        "TestData");

    // Graph instances
    public BalanceGraph BalanceGraph { get; private set; } = null!;
    public TrustGraph TrustGraph { get; private set; } = null!;
    public GraphFactory GraphFactory { get; } = new GraphFactory();
    public TestGraphFactory TestGraphFactory { get; } = new TestGraphFactory();
    public ILoadGraph LoadGraph { get; private set; } = null!;
    public List<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)> Balances { get; private set; } = null!;
    public List<(string Truster, string Trustee, int Limit)> TrustRelations { get; private set; } = null!;
    
    /// <summary>
    /// Initialize with default sample data
    /// </summary>
    public PathfinderTestFixture()
    {
        GraphFactory = new GraphFactory();
        TestGraphFactory = new TestGraphFactory();
        LoadSampleData();
        InitializeGraphs();
    }

    /// <summary>
    /// Initialize with custom data
    /// </summary>
    public PathfinderTestFixture(
        List<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)> balances,
        List<(string Truster, string Trustee, int Limit)> trustRelations)
    {
        GraphFactory = new GraphFactory();
        TestGraphFactory = new TestGraphFactory();
        Balances = balances;
        TrustRelations = trustRelations;
        InitializeGraphs();
    }

    /// <summary>
    /// Loads sample data from CSV files. If files don't exist, creates a simple test network.
    /// </summary>
    private void LoadSampleData()
    {
        try
        {
            Directory.CreateDirectory(TestDataPath);
            var balancesPath = Path.Combine(TestDataPath, "sample_balances.csv");
            var trustPath = Path.Combine(TestDataPath, "sample_trust.csv");

            if (File.Exists(balancesPath) && File.Exists(trustPath))
            {
                // Load from existing files
                Balances = TestUtils.LoadBalancesFromCsv(balancesPath);
                TrustRelations = TestUtils.LoadTrustFromCsv(trustPath);
            }
            else
            {
                // Create sample data and save it
                var network = TestUtils.CreateTestNetwork(5);
                Balances = network.Balances;
                TrustRelations = network.TrustRelations;
                
                TestUtils.SaveNetworkSnapshot(Balances, TrustRelations, balancesPath, trustPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading sample data: {ex.Message}");
            // Fallback to basic test network
            var network = TestUtils.CreateTestNetwork(5);
            Balances = network.Balances;
            TrustRelations = network.TrustRelations;
        }
    }

    /// <summary>
    /// Initialize graphs with the current test data
    /// </summary>
    private void InitializeGraphs()
    {
        // Create mock load graph
        LoadGraph = TestUtils.CreateMockLoadGraph(Balances, TrustRelations);
        
        // Initialize graphs using our test factory
        TrustGraph = TestGraphFactory.CreateTrustGraph(LoadGraph);
        BalanceGraph = TestGraphFactory.CreateBalanceGraph(LoadGraph);
    }

    /// <summary>
    /// Creates a new test network with the specified parameters and reinitializes the graphs
    /// </summary>
    public void ReconfigureWithTestNetwork(int nodeCount, long baseAmount = 100000000, bool linear = false)
    {
        var network = linear 
            ? TestUtils.CreateLinearNetwork(nodeCount, baseAmount)
            : TestUtils.CreateTestNetwork(nodeCount, baseAmount);
            
        Balances = network.Balances;
        TrustRelations = network.TrustRelations;
        
        InitializeGraphs();
    }

    /// <summary>
    /// Save the current network configuration to CSV files
    /// </summary>
    public void SaveCurrentNetwork(string balancesFileName, string trustFileName)
    {
        var balancesPath = Path.Combine(TestDataPath, balancesFileName);
        var trustPath = Path.Combine(TestDataPath, trustFileName);
        
        TestUtils.SaveNetworkSnapshot(Balances, TrustRelations, balancesPath, trustPath);
    }

    /// <summary>
    /// Load network configuration from CSV files
    /// </summary>
    public void LoadNetwork(string balancesFileName, string trustFileName)
    {
        var balancesPath = Path.Combine(TestDataPath, balancesFileName);
        var trustPath = Path.Combine(TestDataPath, trustFileName);
        
        if (File.Exists(balancesPath) && File.Exists(trustPath))
        {
            Balances = TestUtils.LoadBalancesFromCsv(balancesPath);
            TrustRelations = TestUtils.LoadTrustFromCsv(trustPath);
            InitializeGraphs();
        }
        else
        {
            throw new FileNotFoundException($"Test network files not found: {balancesFileName}, {trustFileName}");
        }
    }
}