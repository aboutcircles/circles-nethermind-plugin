using Circles.Pathfinder.Data;

namespace Circles.Pathfinder.Tests.Utils;

/// <summary>
/// Mock implementation of ILoadGraph for testing, returning pre-defined data.
/// </summary>
public class MockLoadGraph : ILoadGraph
{
    private readonly IEnumerable<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)> _balances;
    private readonly IEnumerable<(string Truster, string Trustee, int Limit)> _trustRelations;

    public MockLoadGraph(
        IEnumerable<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)> balances,
        IEnumerable<(string Truster, string Trustee, int Limit)> trustRelations)
    {
        _balances = balances;
        _trustRelations = trustRelations;
    }

    public IEnumerable<(string Balance, string Account, string TokenAddress, bool IsWrapped, bool IsStatic)> LoadV2Balances()
    {
        return _balances;
    }

    public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
    {
        return _trustRelations;
    }
}