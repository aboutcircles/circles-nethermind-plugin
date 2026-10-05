using System.Text.Json;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Runs the WHERE clauses built by circles_query and circles_events against a real
/// Postgres table, with filter values sent in the JSON type a client uses, which is
/// not always the column's type: a uint256 amount has to be a JSON string, so a
/// NUMERIC column gets "0" or "1000000000000000000000".
/// Postgres has no operator between NUMERIC and TEXT, so a value that is not
/// converted to its column's type fails with 42883 and the client gets "Internal error".
/// "FilteredView" stands for a view whose declared column type differs from the database,
/// as V_CrcV2_Transfers.id does: declared BigInt, holds a token address.
/// </summary>
[TestFixture]
[Category("RequiresDocker")]
public class FilterValueColumnTypeTests
{
    private const string Uint256Max =
        "115792089237316195423570985008687907853269984665640564039457584007913129639935";

    // Column types as DatabaseSchemaMap.TableColumns reports them (ValueTypes names).
    private static readonly IReadOnlyDictionary<string, string> ColumnTypes = new Dictionary<string, string>
    {
        ["value"] = "BigInt",
        ["blockNumber"] = "Int",
        ["name"] = "String",
        ["flag"] = "Boolean",
        ["ratio"] = "Double",
    };

    private const string TokenAddress = "0x31b5a61090181014829bf5db9df1bfcd7ad017f8";

    private static readonly IReadOnlyDictionary<string, string> ViewColumnTypes = new Dictionary<string, string>
    {
        ["id"] = "BigInt",
        ["value"] = "BigInt",
    };

    private PostgreSqlContainer? _postgres;
    private NpgsqlDataSource? _dataSource;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        try
        {
            _postgres = new PostgreSqlBuilder("postgres:15-alpine").Build();
            await _postgres.StartAsync();
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Docker/Postgres test container unavailable: {ex.Message}");
            return;
        }

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE "Filtered" (
                "value" NUMERIC, "blockNumber" BIGINT, "name" TEXT, "flag" BOOLEAN, "ratio" DOUBLE PRECISION);
            INSERT INTO "Filtered" VALUES
                (0, 1, 'alice', true, 0.5),
                (5, 2, 'bob', false, 1.5),
                (1000000000000000000000, 3, '123', true, 2.5),
                ({Uint256Max}, 4, 'carol', false, 3.5);
            CREATE TABLE "FilteredView" ("id" TEXT, "value" NUMERIC);
            INSERT INTO "FilteredView" VALUES ('{TokenAddress}', 0), ('0x78bab8d5ea6b72f8375cc21436857815210f7d02', 7);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource != null) await _dataSource.DisposeAsync();
        if (_postgres != null) await _postgres.DisposeAsync();
    }

    private static FilterPredicateDto Filter(string column, FilterType type, object? value) =>
        new() { Column = column, FilterType = type, Value = value };

    private static JsonElement JsonArray(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IEnumerable<TestCaseData> MatchingCases()
    {
        yield return new TestCaseData(Filter("value", FilterType.NotEquals, "0"), 3)
            .SetName("NotEquals on NUMERIC with a numeric string");
        yield return new TestCaseData(Filter("value", FilterType.Equals, "1000000000000000000000"), 1)
            .SetName("Equals on NUMERIC with a string above long range");
        yield return new TestCaseData(Filter("value", FilterType.Equals, Uint256Max), 1)
            .SetName("Equals on NUMERIC with uint256 max");
        yield return new TestCaseData(Filter("value", FilterType.In, JsonArray("""["0", "5"]""")), 2)
            .SetName("In on NUMERIC with numeric strings");
        yield return new TestCaseData(Filter("value", FilterType.NotIn, JsonArray("""["0"]""")), 3)
            .SetName("NotIn on NUMERIC with a numeric string");
        yield return new TestCaseData(Filter("value", FilterType.GreaterThan, "100000000000000000000000000000"), 1)
            .SetName("GreaterThan on NUMERIC with a string above decimal range");
        yield return new TestCaseData(Filter("blockNumber", FilterType.Equals, "3"), 1)
            .SetName("Equals on BIGINT with a numeric string");
        yield return new TestCaseData(Filter("blockNumber", FilterType.GreaterThanOrEquals, "3"), 2)
            .SetName("GreaterThanOrEquals on BIGINT with a numeric string");
        yield return new TestCaseData(Filter("name", FilterType.Equals, 123), 1)
            .SetName("Equals on TEXT with a JSON number");
        yield return new TestCaseData(Filter("name", FilterType.LessThan, "200"), 1)
            .SetName("LessThan on TEXT with a numeric-looking string");
        yield return new TestCaseData(Filter("name", FilterType.Like, "%o%"), 2)
            .SetName("Like on TEXT");
        yield return new TestCaseData(Filter("flag", FilterType.Equals, "true"), 2)
            .SetName("Equals on BOOLEAN with a string");
        yield return new TestCaseData(Filter("ratio", FilterType.Equals, "1.5"), 1)
            .SetName("Equals on DOUBLE with a numeric string");
    }

    private static IEnumerable<TestCaseData> RejectedCases()
    {
        yield return new TestCaseData(Filter("value", FilterType.NotEquals, "abc"))
            .SetName("NotEquals on NUMERIC with a non-numeric string");
        yield return new TestCaseData(Filter("value", FilterType.In, JsonArray("""["1", "x"]""")))
            .SetName("In on NUMERIC with a non-numeric element");
        yield return new TestCaseData(Filter("blockNumber", FilterType.Equals, "0x10"))
            .SetName("Equals on BIGINT with a hex string");
        yield return new TestCaseData(Filter("flag", FilterType.Equals, "yes"))
            .SetName("Equals on BOOLEAN with a non-boolean string");
        yield return new TestCaseData(Filter("value", FilterType.Like, "1%"))
            .SetName("Like on NUMERIC");
    }

    [TestCaseSource(nameof(MatchingCases))]
    public async Task Query_filter_matches_rows_whatever_the_JSON_type_of_its_value(FilterPredicateDto filter, int expected)
    {
        var parameters = new List<NpgsqlParameter>();
        var clause = CirclesRpcModule.BuildQueryPredicateClause(filter, parameters, ColumnTypes,
            columnTypesMatchDatabase: true);

        Assert.That(await CountAsync($"""SELECT count(*) FROM "Filtered" WHERE {clause}""", parameters),
            Is.EqualTo(expected));
    }

    private static IEnumerable<TestCaseData> ViewCases()
    {
        yield return new TestCaseData(Filter("id", FilterType.Equals, TokenAddress), 1)
            .SetName("View: Equals with an address on a column declared BigInt");
        yield return new TestCaseData(Filter("id", FilterType.Like, "0x31%"), 1)
            .SetName("View: Like on a column declared BigInt");
        yield return new TestCaseData(Filter("id", FilterType.In, JsonArray($"""["{TokenAddress}", "0xdead"]""")), 1)
            .SetName("View: In with addresses on a column declared BigInt");
        yield return new TestCaseData(Filter("value", FilterType.NotEquals, "0"), 1)
            .SetName("View: NotEquals on NUMERIC with a numeric string");
    }

    // A view filter that worked before keeps working: a value that does not parse as the
    // declared type is sent unchanged instead of rejected.
    [TestCaseSource(nameof(ViewCases))]
    public async Task Query_filter_on_a_view_keeps_values_its_declared_type_cannot_hold(FilterPredicateDto filter, int expected)
    {
        var parameters = new List<NpgsqlParameter>();
        var clause = CirclesRpcModule.BuildQueryPredicateClause(filter, parameters, ViewColumnTypes,
            columnTypesMatchDatabase: false);

        Assert.That(await CountAsync($"""SELECT count(*) FROM "FilteredView" WHERE {clause}""", parameters),
            Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(MatchingCases))]
    public async Task Events_filter_matches_rows_whatever_the_JSON_type_of_its_value(FilterPredicateDto filter, int expected)
    {
        var parameters = new List<NpgsqlParameter>();
        var clause = CirclesRpcModule.BuildPredicateClause(filter, parameters, "Filtered", ColumnTypes);

        Assert.That(await CountAsync($"""SELECT count(*) FROM "Filtered" t WHERE {clause}""", parameters),
            Is.EqualTo(expected));
    }

    // ArgumentException is what RpcDispatcher answers with JSON-RPC "invalid params";
    // anything else becomes "Internal error".
    [TestCaseSource(nameof(RejectedCases))]
    public void Query_filter_rejects_a_value_its_column_cannot_hold(FilterPredicateDto filter)
    {
        Assert.Throws<ArgumentException>(() =>
            CirclesRpcModule.BuildQueryPredicateClause(filter, new List<NpgsqlParameter>(), ColumnTypes,
                columnTypesMatchDatabase: true));
    }

    [TestCaseSource(nameof(RejectedCases))]
    public void Events_filter_rejects_a_value_its_column_cannot_hold(FilterPredicateDto filter)
    {
        Assert.Throws<ArgumentException>(() =>
            CirclesRpcModule.BuildPredicateClause(filter, new List<NpgsqlParameter>(), "Filtered", ColumnTypes));
    }

    private async Task<long> CountAsync(string sql, List<NpgsqlParameter> parameters)
    {
        if (_dataSource == null) Assert.Ignore("No data source (container unavailable).");

        await using var conn = await _dataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters.ToArray());
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
