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

    // V2 Hub epoch on gnosis mainnet (same as V1): Hub(0x5524...).inflationDayZero() == 1_602_720_000
    private const uint InflationDayZeroUnix = 1_602_720_000;

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

            // Handle static token conversion: inflationary → demurraged at today's day index
            if (balance.IsStatic)
            {
                var staticAttoCircles = BigInteger.Parse(amount);
                var targetDay = CirclesConverter.DayFromTimestamp(DateTimeOffset.UtcNow, InflationDayZeroUnix);
                var demurragedAttoCircles = CirclesConverter.InflationaryToDemurrage(staticAttoCircles, targetDay);
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

    public IEnumerable<string> LoadOrganizations() => [];

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

    /// <summary>
    /// Derives all unique addresses from the fixture subgraph.
    /// In fixture scenarios, every address that appears is implicitly registered.
    /// </summary>
    public IEnumerable<string> LoadRegisteredAvatars()
    {
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_subgraph.Balances != null)
        {
            foreach (var b in _subgraph.Balances)
            {
                addresses.Add(b.Holder.ToLowerInvariant());
                addresses.Add(b.Token.ToLowerInvariant());
            }
        }

        if (_subgraph.Trust != null)
        {
            foreach (var t in _subgraph.Trust)
            {
                if (t.Length >= 2)
                {
                    addresses.Add(t[0].ToLowerInvariant());
                    addresses.Add(t[1].ToLowerInvariant());
                }
            }
        }

        if (_subgraph.Groups != null)
        {
            foreach (var g in _subgraph.Groups)
                addresses.Add(g.ToLowerInvariant());
        }

        if (_subgraph.ConsentedAvatars != null)
        {
            foreach (var a in _subgraph.ConsentedAvatars)
                addresses.Add(a.ToLowerInvariant());
        }

        return addresses;
    }

    /// <summary>
    /// Returns empty — fixture scenarios don't include wrapper data.
    /// </summary>
    public IEnumerable<(string WrapperAddress, string UnderlyingAvatar, int CirclesType)> LoadWrapperMappings()
        => Array.Empty<(string, string, int)>();
}
