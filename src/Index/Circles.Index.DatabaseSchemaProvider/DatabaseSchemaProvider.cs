using System.Collections.Immutable;
using Circles.Common;

namespace Circles.Index.DatabaseSchemaProvider;

/// <summary>
/// Provides access to all database schemas without depending on Nethermind assemblies.
/// This allows the RPC host to access schema information independently of the main Plugin.
/// </summary>
public static class Schemas
{
    /// <summary>
    /// Gets all database schemas registered in the Circles indexer.
    /// This is the same information that Plugin.AllSchemas contains, but without the Nethermind dependency.
    /// </summary>
    public static readonly IReadOnlyList<IDatabaseSchema> AllSchemas = new List<IDatabaseSchema>
    {
        new CirclesV1.DatabaseSchema(),
        new CirclesV1.NameRegistry.DatabaseSchema(),
        new CirclesV2.DatabaseSchema(),
        new CirclesV2.AffiliateGroupRegistry.DatabaseSchema(),
        new CirclesV2.BaseGroupDeployer.DatabaseSchema(),
        new CirclesV2.CMGroupDeployer.DatabaseSchema(),
        new CirclesV2.InvitationEscrow.DatabaseSchema(),
        new CirclesV2.InvitationsAtScale.DatabaseSchema(),
        new CirclesV2.LBP.DatabaseSchema(),
        new CirclesV2.NameRegistry.DatabaseSchema(),
        new CirclesV2.OIC.DatabaseSchema(),
        new CirclesV2.PaymentGateway.DatabaseSchema(),
        new CirclesV2.StandardTreasury.DatabaseSchema(),
        new CirclesV2.TokenOffers.DatabaseSchema(),
        new CirclesViews.DatabaseSchema(),
        new DatabaseSchema(),
        new Safe.DatabaseSchema(),
    }.ToImmutableList();
}
