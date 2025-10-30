using System.Data;
using Circles.Index.Common;

namespace Circles.Index.Query;

public record OrderBy(string Column, string SortOrder) : ISql
{
    public ParameterizedSql ToSql(IDatabaseUtils database)
    {
        var sql = $"{QuoteIdentifier(Column)} {(SortOrder.ToUpper() == "DESC" ? "DESC" : "ASC")}";
        return new ParameterizedSql(sql, Enumerable.Empty<IDbDataParameter>());
    }

    private string QuoteIdentifier(string identifier) => $"\"{identifier}\"";
}