using System.Data;
using Dapper;

namespace Circles.Index.Rpc.Queries;

public static class SearchProfilesQuery
{
    public record Row(
        DateTime Timestamp,
        long Receive_Count, // column is receive_count
        string Avatar,
        string? Avatar_Name, // avatar_name
        string? Short_Name, // short_name
        byte[]? Metadata_Digest, // metadata_digest
        string? Payload);

    static readonly string Sql = SqlLoader.Load("circles_searchProfiles.sql");

    public static Task<IEnumerable<Row>> ExecuteAsync(
        IDbConnection db,
        string search,
        int limit,
        int offset,
        CancellationToken ct = default)
    {
        var param = new { search, limit, offset };
        return db.QueryAsync<Row>(
            new CommandDefinition(Sql, param, cancellationToken: ct));
    }
}