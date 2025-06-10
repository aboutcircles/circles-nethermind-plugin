using System.Data;
using Dapper;

namespace Circles.Index.Rpc.Queries;

public static class TrustRelationsQuery
{
    public record Row(string User, string CanSendTo, int Limit);

    static readonly string Sql = SqlLoader.Load("circles_getTrustRelations.sql");

    public static Task<IEnumerable<Row>> ExecuteAsync(
        IDbConnection db,
        string address,
        CancellationToken ct = default)
    {
        var param = new { address };
        return db.QueryAsync<Row>(
            new CommandDefinition(Sql, param, cancellationToken: ct));
    }
}