using Circles.Index.Common;

namespace Circles.Index.Query;

public record Conjunction(ConjunctionType ConjunctionType, IFilterPredicate[] Predicates) : ISql, IFilterPredicate
{
    public ParameterizedSql ToSql(IDatabaseUtils database)
    {
        var conjunction = ConjunctionType == ConjunctionType.And ? " AND " : " OR ";
        var predicateSqls = Predicates.OfType<ISql>().Select(p => p.ToSql(database)).ToList();

        var sql = $"({string.Join(conjunction, predicateSqls.Select(p => p.Sql))})";
        var parameters = predicateSqls.SelectMany(p => p.Parameters).ToList();

        return new ParameterizedSql(sql, parameters);
    }
}