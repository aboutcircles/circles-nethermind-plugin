using Circles.Pathfinder.Data;
using Circles.Pathfinder.DTOs;
using Nethermind.Int256;
using System.Globalization;

namespace Circles.Pathfinder.Tests.Utils;

/// <summary>
/// Provides utility methods for loading test data and creating test fixtures.
/// </summary>
public static class TestUtils
{
    /// <summary>
    /// Creates a mock LoadGraph instance that returns the provided balance and trust data.
    /// </summary>
    public static MockLoadGraph CreateMockLoadGraph(
        IEnumerable<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)> balances,
        IEnumerable<(string Truster, string Trustee, int Limit)> trustRelations)
    {
        return new MockLoadGraph(balances, trustRelations);
    }

    /// <summary>
    /// Loads balance data from a CSV file.
    /// Expected format: Balance,Account,TokenAddress,IsWrapped,IsStatic
    /// </summary>
    public static List<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)> LoadBalancesFromCsv(string filePath)
    {
        var balances = new List<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)>();
        var lines = File.ReadAllLines(filePath);

        // Skip header
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length >= 5)
            {
                balances.Add((
                    parts[0],
                    parts[1],
                    parts[2],
                    bool.Parse(parts[3]),
                    bool.Parse(parts[4])
                ));
            }
        }

        return balances;
    }

    /// <summary>
    /// Loads trust relation data from a CSV file.
    /// Expected format: Truster,Trustee,Limit
    /// </summary>
    public static List<(string Truster, string Trustee, int Limit)> LoadTrustFromCsv(string filePath)
    {
        var trustRelations = new List<(string Truster, string Trustee, int Limit)>();
        var lines = File.ReadAllLines(filePath);

        // Skip header
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length >= 3)
            {
                trustRelations.Add((
                    parts[0],
                    parts[1],
                    int.Parse(parts[2])
                ));
            }
        }

        return trustRelations;
    }

    /// <summary>
    /// Creates a standard flow request for testing.
    /// </summary>
    public static FlowRequest CreateFlowRequest(
        string source,
        string sink,
        string targetFlow,
        List<string>? fromTokens = null,
        List<string>? toTokens = null,
        bool? withWrap = false)
    {
        return new FlowRequest
        {
            Source = source,
            Sink = sink,
            TargetFlow = targetFlow,
            FromTokens = fromTokens,
            ToTokens = toTokens,
            WithWrap = withWrap
        };
    }

    /// <summary>
    /// Saves the current balance and trust state to CSV files for future test runs.
    /// </summary>
    public static void SaveNetworkSnapshot(
        IEnumerable<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)> balances,
        IEnumerable<(string Truster, string Trustee, int Limit)> trustRelations,
        string balancesFilePath,
        string trustFilePath)
    {
        // Save balances
        using (var writer = new StreamWriter(balancesFilePath))
        {
            writer.WriteLine("Balance,Account,TokenAddress,IsWrapped,IsStatic");
            foreach (var balance in balances)
            {
                writer.WriteLine($"{balance.Balance},{balance.Account},{balance.TokenAddress},{balance.IsWrapped},{balance.IsStatic}");
            }
        }

        // Save trust relations
        using (var writer = new StreamWriter(trustFilePath))
        {
            writer.WriteLine("Truster,Trustee,Limit");
            foreach (var trust in trustRelations)
            {
                writer.WriteLine($"{trust.Truster},{trust.Trustee},{trust.Limit}");
            }
        }
    }

    /// <summary>
    /// Creates a minimal test network with the specified number of nodes.
    /// </summary>
    public static (List<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)> Balances,
           List<(string Truster, string Trustee, int Limit)> TrustRelations) 
        CreateTestNetwork(int nodeCount, long baseAmount = 100000000)
    {
        var balances = new List<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)>();
        var trustRelations = new List<(string Truster, string Trustee, int Limit)>();

        // Create addresses
        var addresses = new string[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            addresses[i] = $"0x{i:x40}";
        }

        // Each node has a token and holds some amount of it
        for (int i = 0; i < nodeCount; i++)
        {
            // Node has its own token
            balances.Add((
                (baseAmount * (i + 1)).ToString(),
                addresses[i],
                addresses[i],
                false,
                false
            ));
        }

        // Create trust relationships
        for (int i = 0; i < nodeCount; i++)
        {
            for (int j = 0; j < nodeCount; j++)
            {
                if (i != j)
                {
                    // Node i trusts node j (but not itself)
                    trustRelations.Add((
                        addresses[i],
                        addresses[j],
                        100
                    ));
                }
            }
        }

        return (balances, trustRelations);
    }

    /// <summary>
    /// Creates a linear trust network where each node trusts the next one
    /// </summary>
    public static (List<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)> Balances,
           List<(string Truster, string Trustee, int Limit)> TrustRelations) 
        CreateLinearNetwork(int nodeCount, long baseAmount = 100000000)
    {
        var balances = new List<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)>();
        var trustRelations = new List<(string Truster, string Trustee, int Limit)>();

        // Create addresses
        var addresses = new string[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            addresses[i] = $"0x{i:x40}";
        }

        // Each node has a token and holds some amount of it
        for (int i = 0; i < nodeCount; i++)
        {
            // Node has its own token
            balances.Add((
                baseAmount.ToString(),
                addresses[i],
                addresses[i],
                false,
                false
            ));
        }

        // Create linear trust chain: 0 -> 1 -> 2 -> ... -> n-1
        for (int i = 0; i < nodeCount - 1; i++)
        {
            trustRelations.Add((
                addresses[i],
                addresses[i + 1],
                100
            ));
        }

        return (balances, trustRelations);
    }

    /// <summary>
    /// Validates that a path in the transfers list forms a valid path from source to sink
    /// </summary>
    public static bool ValidatePath(List<TransferPathStep> transfers, string source, string sink)
    {
        if (transfers.Count == 0) return false;
        
        // Check if the first transfer starts from the source
        bool startsFromSource = transfers.Any(t => t.From == source);
        
        // Check if the last transfer goes to the sink
        bool endsAtSink = transfers.Any(t => t.To == sink);
        
        return startsFromSource && endsAtSink;
    }

    /// <summary>
    /// Checks if the sum of outgoing transfers from source equals the sum of incoming transfers to sink
    /// </summary>
    public static bool ValidateFlowConservation(List<TransferPathStep> transfers, string source, string sink)
    {
        UInt256 outFlow = UInt256.Zero;
        UInt256 inFlow = UInt256.Zero;
        
        foreach (var transfer in transfers)
        {
            if (transfer.From == source)
            {
                outFlow += UInt256.Parse(transfer.Value);
            }
            
            if (transfer.To == sink)
            {
                inFlow += UInt256.Parse(transfer.Value);
            }
        }
        
        return outFlow == inFlow;
    }
}