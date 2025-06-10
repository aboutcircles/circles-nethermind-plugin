using System.Data;
using Dapper;

namespace Circles.Index.Rpc.Queries;

public static class circles_getProfileByCidBatchQuery
{
    public record Row(string? Payload);

    static readonly string Sql = SqlLoader.Load("circles_getProfileByCidBatch.sql");

    public static Task<IEnumerable<Row>> ExecuteAsync(
        IDbConnection db,
        IEnumerable<string?> cids,
        CancellationToken ct = default)
    {
        var param = new { cids = cids.ToArray() };
        var cmd = new CommandDefinition(Sql, param, cancellationToken: ct);
        return db.QueryAsync<Row>(cmd);
    }
}