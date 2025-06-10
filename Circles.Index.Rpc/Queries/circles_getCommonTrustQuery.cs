using System.Data;
using Dapper;

namespace Circles.Index.Rpc.Queries;

public static class CommonTrustQuery
{
    public record Row(string Trustee);

    static readonly string SqlV1 = SqlLoader.Load("circles_getCommonTrust_v1.sql");
    static readonly string SqlV2 = SqlLoader.Load("circles_getCommonTrust_v2.sql");

    public static Task<IEnumerable<Row>> ExecuteV1Async(
        IDbConnection db,
        string address1,
        string address2,
        CancellationToken ct = default)
    {
        var param = new { address1, address2 };
        return db.QueryAsync<Row>(
            new CommandDefinition(SqlV1, param, cancellationToken: ct));
    }

    public static Task<IEnumerable<Row>> ExecuteV2Async(
        IDbConnection db,
        string address1,
        string address2,
        CancellationToken ct = default)
    {
        var param = new { address1, address2 };
        return db.QueryAsync<Row>(
            new CommandDefinition(SqlV2, param, cancellationToken: ct));
    }
}