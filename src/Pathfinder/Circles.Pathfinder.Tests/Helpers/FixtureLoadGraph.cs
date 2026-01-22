using System.Globalization;
using System.Numerics;
using Circles.Common;
using Circles.Pathfinder.Data;

namespace Circles.Pathfinder.Tests.Helpers;

/// <summary>
/// ILoadGraph implementation that reads from embedded fixture subgraph data.
/// Enables fast unit tests without database or network dependencies.
/// </summary>
public class FixtureLoadGraph : ILoadGraph
{
    private readonly FixtureSubgraph _subgraph;

    public FixtureLoadGraph(FixtureSubgraph subgraph)
    {
        _subgraph = subgraph ?? throw new ArgumentNullException(nameof(subgraph));
    }

    public IEnumerable<(string Balance, int Account, int TokenAddress, bool IsWrapped, bool IsStatic)>
        LoadV2Balances()
    {
        if (_subgraph.Balances == null)
        {
            yield break;
        }

        foreach (var balance in _subgraph.Balances)
        {
            var amount = balance.Amount;

            // Handle static token conversion (same as LoadGraph.cs)
            if (balance.IsStatic)
            {
                var staticAttoCircles = BigInteger.Parse(amount);
                var demurragedAttoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(staticAttoCircles);
                if (demurragedAttoCircles == 0)
                {
                    continue;
                }

                amount = demurragedAttoCircles.ToString(CultureInfo.InvariantCulture);
            }

            if (amount == "0")
            {
                continue;
            }

            yield return (
                amount,
                AddressIdPool.IdOf(balance.Holder.ToLowerInvariant()),
                AddressIdPool.IdOf(balance.Token.ToLowerInvariant()),
                balance.IsWrapped,
                balance.IsStatic
            );
        }
    }

    public IEnumerable<(string Truster, string Trustee, int Limit)> LoadV2Trust()
    {
        if (_subgraph.Trust == null)
        {
            yield break;
        }

        foreach (var trustPair in _subgraph.Trust)
        {
            if (trustPair.Length < 2)
            {
                continue;
            }

            var truster = trustPair[0];
            var trustee = trustPair[1];

            yield return (truster, trustee, 100); // Default trust limit of 100 in V2
        }
    }

    public IEnumerable<string> LoadGroups()
    {
        if (_subgraph.Groups == null)
        {
            yield break;
        }

        foreach (var groupAddress in _subgraph.Groups)
        {
            yield return groupAddress;
        }
    }

    public IEnumerable<(string GroupAddress, string TrustedToken)> LoadGroupTrusts()
    {
        if (_subgraph.GroupTrusts == null)
        {
            yield break;
        }

        foreach (var groupTrustPair in _subgraph.GroupTrusts)
        {
            if (groupTrustPair.Length < 2)
            {
                continue;
            }

            var groupAddress = groupTrustPair[0];
            var trustedToken = groupTrustPair[1];

            yield return (groupAddress, trustedToken);
        }
    }

    public IEnumerable<(string Avatar, bool HasConsentedFlow)> LoadConsentedFlowFlags()
    {
        if (_subgraph.ConsentedAvatars == null)
        {
            yield break;
        }

        foreach (var avatar in _subgraph.ConsentedAvatars)
        {
            yield return (avatar, true);
        }
    }
}
