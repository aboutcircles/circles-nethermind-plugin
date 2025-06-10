using System.Data;
using Dapper;
namespace Circles.Index.Rpc.Queries;

public static class circles_getProfileByCidQuery
{
    public record Row(string Payload);

    static readonly string Sql = SqlLoader.Load("circles_getProfileByCid.sql");

    public static Task<Row?> ExecuteAsync(
        IDbConnection db,
        string cid,
        CancellationToken ct = default)
    {
        var param = new { cid };
        return db.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(Sql, param, cancellationToken: ct));
    }
}