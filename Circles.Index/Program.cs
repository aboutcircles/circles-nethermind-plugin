using System.Data.Common;
using Circles.Index.Data;
using Circles.Index.Data.Query;
using Circles.Index.Rpc;
using Newtonsoft.Json;
using Npgsql;

namespace Circles.Index;

public static class Program
{
    public static void Main()
    {
        DbProviderFactory factory = NpgsqlFactory.Instance;
        Query.Initialize(factory);
        

        CirclesQuery q = new()
        {
            Table = Tables.Erc20Transfer, Columns =
            [
                Columns.BlockNumber,
                Columns.TransactionIndex,
                Columns.LogIndex,
                Columns.TransactionHash,
                Columns.FromAddress,
                Columns.ToAddress,
                Columns.Amount
            ],
            Conditions =
            {
                new Expression
                {
                    Type = "LessThan",
                    Column = Columns.Amount,
                    Value = "500000000000000000000000000000"
                },
                new Expression
                {
                    Type = "Equals",
                    Column = Columns.FromAddress,
                    Value = "0x0000000000000000000000000000000000000000"
                }
            }
        };

        var json = JsonConvert.SerializeObject(q);
        Console.WriteLine(json);

        var result = circles_query(q);

        foreach (var row in result)
        {
            Console.WriteLine(string.Join(", ", row));
        }

        Console.WriteLine("Hello, Circles!");
    }


    public static IEnumerable<object[]> circles_query(CirclesQuery query)
    {
        using NpgsqlConnection connection = new("Host=localhost;Username=postgres;Database=postgres;Port=7432;Include Error Detail=true;");
        connection.Open();
        
        Schema.Migrate(connection);

        var select = Query.Select(query.Table,
            query.Columns ?? throw new InvalidOperationException("Columns are null"));

        if (query.Conditions.Any())
        {
            foreach (var condition in query.Conditions)
            {
                select.Where(BuildCondition(query.Table, condition));
            }
        }

        Console.WriteLine(select.ToString());

        var result = Query.Execute(connection, select).ToList();

        return result;
    }

    private static IQuery BuildCondition(Tables table, Expression expression)
    {
        if (expression.Type == "Equals")
        {
            return Query.Equals(table, expression.Column!.Value, expression.Value!);
        }

        if (expression.Type == "GreaterThan")
        {
            return Query.GreaterThan(table, expression.Column!.Value, expression.Value!);
        }

        if (expression.Type == "LessThan")
        {
            return Query.LessThan(table, expression.Column!.Value, expression.Value!);
        }

        if (expression.Type == "And")
        {
            return Query.And(expression.Elements!.Select(o => BuildCondition(table, o)).ToArray());
        }

        if (expression.Type == "Or")
        {
            return Query.Or(expression.Elements!.Select(o => BuildCondition(table, o)).ToArray());
        }

        throw new InvalidOperationException($"Unknown expression type: {expression.Type}");
    }
}