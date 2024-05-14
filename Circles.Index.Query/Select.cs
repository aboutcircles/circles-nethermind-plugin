using Circles.Index.Common;

namespace Circles.Index.Query;

public record Select(
    string Namespace,
    string Table,
    IEnumerable<string> Columns,
    IEnumerable<IFilterPredicate> Filter,
    IEnumerable<OrderBy> Order,
    bool Distinct = false) : ISql
{
    public ParameterizedSql ToSql(IDatabaseUtils database)
    {
        if (!database.Schema.Tables.TryGetValue((Namespace, Table), out var tableSchema))
        {
            throw new InvalidOperationException($"Table {Namespace}_{Table} not found in schema.");
        }

        var tableSchemaColumns = tableSchema.Columns.ToDictionary(o => o.Column, o => o);
        if (Columns.Any() && Columns.Any(c => !tableSchemaColumns.ContainsKey(c)))
        {
            throw new InvalidOperationException($"Select column not found in schema.");
        }

        if (Filter.OfType<FilterPredicate>().Any(f => !tableSchemaColumns.ContainsKey(f.Column)))
        {
            throw new InvalidOperationException($"Filter column not found in schema.");
        }

        if (Order.Any(o => !tableSchemaColumns.ContainsKey(o.Column)))
        {
            throw new InvalidOperationException($"Order column not found in schema.");
        }

        var columns = Columns.Any() ? string.Join(", ", Columns.Select(database.QuoteIdentifier)) : "*";
        var filterSql = Filter.Any()
            ? string.Join(" AND ", Filter.OfType<ISql>().Select(f => f.ToSql(database).Sql))
            : string.Empty;
        var orderBySql = Order.Any()
            ? $" ORDER BY {string.Join(", ", Order.Select(o => o.ToSql(database).Sql))}"
            : string.Empty;
        var distinctSql = Distinct ? "DISTINCT " : string.Empty;

        var sql = $"SELECT {distinctSql}{columns} FROM {database.QuoteIdentifier($"{Namespace}_{Table}")}";
        if (!string.IsNullOrEmpty(filterSql))
        {
            sql += $" WHERE {filterSql}";
        }

        if (!string.IsNullOrEmpty(orderBySql))
        {
            sql += orderBySql;
        }

        var parameters = Filter.OfType<ISql>().SelectMany(f => f.ToSql(database).Parameters)
            .Concat(Order.OfType<ISql>().SelectMany(o => o.ToSql(database).Parameters))
            .ToList();

        Console.WriteLine("Select.ToSql");
        Console.WriteLine("-----------------");
        Console.WriteLine("SQL:");
        Console.WriteLine(sql);
        Console.WriteLine("");
        Console.WriteLine("Parameters:");
        foreach (var parameter in parameters)
        {
            Console.WriteLine($"* {parameter.ParameterName} = {parameter.Value}");
        }
        
        return new ParameterizedSql(sql, parameters);
    }
}