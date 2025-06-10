using System.Data;
using Dapper;
namespace Circles.Index.Rpc.Queries;

public static class TokenExposureIdsQuery
{
    public record Row(string TokenAddress, string Type, string TokenOwner);

    static readonly string Sql = SqlLoader.Load("GetTokenExposureIds.sql");

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