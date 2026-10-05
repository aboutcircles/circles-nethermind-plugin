using System.Diagnostics;
using System.Text.Json;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;
using SchemaProvider = Circles.Index.DatabaseSchemaProvider.Schemas;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Runs the WHERE clauses built by circles_query and circles_events against a real
/// Postgres table, with filter values sent in the JSON type a client uses, which is
/// not always the column's type: a uint256 amount has to be a JSON string, so a
/// NUMERIC column gets "0" or "1000000000000000000000".
/// Postgres has no operator between NUMERIC and TEXT, so a string bound as text to a
/// NUMERIC column fails with 42883 and the client gets "Internal server error".
/// "FilteredView" stands for a view whose declared column types differ from the database.
/// V_CrcV2_Transfers.id is declared BigInt but is text holding decimal token ids and
/// wrapper addresses. Bucket views declare their timestamptz "timestamp" column as BigInt or Int.
/// </summary>
[TestFixture]
[Category("RequiresDocker")]
public class FilterValueColumnTypeTests
{
    internal const string Uint256Max =
        "115792089237316195423570985008687907853269984665640564039457584007913129639935";

    private const string Uint256MaxMinusOne =
        "115792089237316195423570985008687907853269984665640564039457584007913129639934";

    internal const string TokenAddress = "0x31b5a61090181014829bf5db9df1bfcd7ad017f8";
    private const string OwnerA = "0x1111111111111111111111111111111111111111";
    private const string OwnerB = "0x2222222222222222222222222222222222222222";

    // Column types as DatabaseSchemaMap.TableColumns reports them (ValueTypes names).
    internal static readonly IReadOnlyDictionary<string, string> ColumnTypes = new Dictionary<string, string>
    {
        ["value"] = "BigInt",
        ["blockNumber"] = "Int",
        ["name"] = "String",
        ["flag"] = "Boolean",
        ["ratio"] = "Double",
        ["owner"] = "Address",
    };

    internal static readonly IReadOnlyDictionary<string, string> ViewColumnTypes = new Dictionary<string, string>
    {
        ["id"] = "BigInt",
        ["value"] = "BigInt",
        ["timestamp"] = "Int",
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
                "value" NUMERIC, "blockNumber" BIGINT, "name" TEXT, "flag" BOOLEAN,
                "ratio" DOUBLE PRECISION, "owner" TEXT);
            INSERT INTO "Filtered" VALUES
                (0, 1, 'alice', true, 0.5, '{OwnerA}'),
                (5, 2, 'bob', false, 1.5, '{OwnerB}'),
                (1000000000000000000000, 3, '123', true, 2.5, '{OwnerA}'),
                (1000000000000000000001, 4, 'dave', false, 4.5, '{OwnerB}'),
                ({Uint256MaxMinusOne}, 5, 'erin', true, 5.5, '{OwnerB}'),
                ({Uint256Max}, 6, 'carol', false, 3.5, '{OwnerB}');
            CREATE TABLE "FilteredView" ("id" TEXT, "value" NUMERIC, "timestamp" TIMESTAMPTZ);
            INSERT INTO "FilteredView" VALUES
                ('{TokenAddress}', 0, '2024-01-01T00:00:00Z'),
                ('0x78bab8d5ea6b72f8375cc21436857815210f7d02', 7, '2024-06-01T00:00:00Z'),
                ('1234', 9, '2025-01-01T00:00:00Z');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource != null) await _dataSource.DisposeAsync();
        if (_postgres != null) await _postgres.DisposeAsync();
    }

    internal static FilterPredicateDto Filter(string column, FilterType type, object? value) =>
        new() { Column = column, FilterType = type, Value = value };

    internal static ConjunctionDto Conjunction(ConjunctionType type, params IFilterPredicateDto[] predicates) =>
        new() { ConjunctionType = type, Predicates = predicates };

    internal static JsonElement JsonArray(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // Rows: value 0, 5, 1e21, 1e21+1, uint256 max - 1, uint256 max; blockNumber 1 to 6.
    // The neighbouring huge values fail any case that compares them as doubles.
    private static IEnumerable<TestCaseData> MatchingCases()
    {
        yield return new TestCaseData(Filter("value", FilterType.NotEquals, "0"), 5)
            .SetName("NotEquals on NUMERIC with a numeric string");
        yield return new TestCaseData(Filter("value", FilterType.Equals, "1000000000000000000000"), 1)
            .SetName("Equals on NUMERIC with a string above long range");
        yield return new TestCaseData(Filter("value", FilterType.Equals, "1000000000000000000001"), 1)
            .SetName("Equals on NUMERIC with the neighbour of a string above long range");
        yield return new TestCaseData(Filter("value", FilterType.Equals, Uint256Max), 1)
            .SetName("Equals on NUMERIC with uint256 max");
        yield return new TestCaseData(Filter("value", FilterType.Equals, Uint256MaxMinusOne), 1)
            .SetName("Equals on NUMERIC with uint256 max minus one");
        yield return new TestCaseData(Filter("value", FilterType.In, JsonArray("""["0", "5"]""")), 2)
            .SetName("In on NUMERIC with numeric strings");
        yield return new TestCaseData(Filter("value", FilterType.NotIn, JsonArray("""["0"]""")), 5)
            .SetName("NotIn on NUMERIC with a numeric string");
        yield return new TestCaseData(Filter("value", FilterType.GreaterThan, "100000000000000000000000000000"), 2)
            .SetName("GreaterThan on NUMERIC with a string above decimal range");
        yield return new TestCaseData(Filter("value", FilterType.GreaterThan, "0.5"), 5)
            .SetName("GreaterThan on NUMERIC with a fractional string");
        yield return new TestCaseData(Filter("value", FilterType.NotEquals, "1e-30"), 6)
            .SetName("NotEquals on NUMERIC with a value below decimal precision");
        yield return new TestCaseData(Filter("value", FilterType.LessThan, "1e999"), 6)
            .SetName("LessThan on NUMERIC with the largest accepted exponent");
        yield return new TestCaseData(Filter("value", FilterType.Equals, " 5 "), 1)
            .SetName("Equals on NUMERIC with surrounding spaces");
        yield return new TestCaseData(Filter("value", FilterType.Equals, "+5"), 1)
            .SetName("Equals on NUMERIC with a plus sign");
        yield return new TestCaseData(Filter("value", FilterType.Equals, "5."), 1)
            .SetName("Equals on NUMERIC with a trailing decimal point");
        yield return new TestCaseData(Filter("value", FilterType.Equals, ".5e1"), 1)
            .SetName("Equals on NUMERIC with a leading decimal point and an exponent");
        yield return new TestCaseData(Filter("value", FilterType.Equals, "50E-1"), 1)
            .SetName("Equals on NUMERIC with a negative exponent");
        yield return new TestCaseData(Filter("value", FilterType.Equals, 5), 1)
            .SetName("Equals on NUMERIC with a JSON integer");
        yield return new TestCaseData(Filter("value", FilterType.LessThan, 10000000000L), 2)
            .SetName("LessThan on NUMERIC with a JSON integer above int range");
        yield return new TestCaseData(Filter("value", FilterType.GreaterThan, 4.5), 5)
            .SetName("GreaterThan on NUMERIC with a JSON fraction");
        yield return new TestCaseData(Filter("value", FilterType.In, JsonArray("[0, 5]")), 2)
            .SetName("In on NUMERIC with JSON integers");
        yield return new TestCaseData(Filter("blockNumber", FilterType.Equals, "3"), 1)
            .SetName("Equals on BIGINT with a numeric string");
        yield return new TestCaseData(Filter("blockNumber", FilterType.GreaterThanOrEquals, "3"), 4)
            .SetName("GreaterThanOrEquals on BIGINT with a numeric string");
        yield return new TestCaseData(Filter("blockNumber", FilterType.Equals, "3.0"), 1)
            .SetName("Equals on BIGINT with a fractional string");
        yield return new TestCaseData(Filter("blockNumber", FilterType.In, JsonArray("""["1", "2"]""")), 2)
            .SetName("In on BIGINT with numeric strings");
        yield return new TestCaseData(Filter("blockNumber", FilterType.Equals, 3), 1)
            .SetName("Equals on BIGINT with a JSON integer");
        yield return new TestCaseData(Filter("blockNumber", FilterType.LessThan, 2.5), 2)
            .SetName("LessThan on BIGINT with a JSON fraction");
        yield return new TestCaseData(Filter("name", FilterType.Equals, 123), 1)
            .SetName("Equals on TEXT with a JSON number");
        yield return new TestCaseData(Filter("name", FilterType.LessThan, "200"), 1)
            .SetName("LessThan on TEXT with a numeric-looking string");
        yield return new TestCaseData(Filter("name", FilterType.Like, "%o%"), 2)
            .SetName("Like on TEXT");
        yield return new TestCaseData(Filter("name", FilterType.Like, 123), 1)
            .SetName("Like on TEXT with a JSON number");
        yield return new TestCaseData(Filter("name", FilterType.ILike, "%O%"), 2)
            .SetName("ILike on TEXT");
        yield return new TestCaseData(Filter("name", FilterType.NotLike, "%o%"), 4)
            .SetName("NotLike on TEXT");
        yield return new TestCaseData(Filter("owner", FilterType.Equals, OwnerA), 2)
            .SetName("Equals on an Address column");
        yield return new TestCaseData(Filter("owner", FilterType.In, JsonArray($"""["{OwnerA}", "0xdead"]""")), 2)
            .SetName("In on an Address column");
        yield return new TestCaseData(Filter("flag", FilterType.Equals, "true"), 3)
            .SetName("Equals on BOOLEAN with a string");
        yield return new TestCaseData(Filter("flag", FilterType.Equals, true), 3)
            .SetName("Equals on BOOLEAN with a JSON boolean");
        yield return new TestCaseData(Filter("ratio", FilterType.Equals, "1.5"), 1)
            .SetName("Equals on DOUBLE with a numeric string");
        yield return new TestCaseData(Filter("ratio", FilterType.LessThan, "1e0"), 1)
            .SetName("LessThan on DOUBLE with an exponent string");
        yield return new TestCaseData(Filter("ratio", FilterType.Equals, 1.5), 1)
            .SetName("Equals on DOUBLE with a JSON fraction");
        yield return new TestCaseData(Filter("ratio", FilterType.GreaterThan, 2), 4)
            .SetName("GreaterThan on DOUBLE with a JSON integer");
        yield return new TestCaseData(
                Conjunction(ConjunctionType.Or,
                    Filter("value", FilterType.Equals, "0"),
                    Filter("blockNumber", FilterType.Equals, "4")), 2)
            .SetName("Or conjunction of NUMERIC and BIGINT filters");
        yield return new TestCaseData(
                Conjunction(ConjunctionType.And,
                    Filter("value", FilterType.GreaterThan, "0"),
                    Conjunction(ConjunctionType.Or,
                        Filter("flag", FilterType.Equals, "true"),
                        Filter("name", FilterType.ILike, "%o%"))), 4)
            .SetName("And conjunction with a nested Or");
    }

    // circles_events has no array form for Equals and NotEquals.
    private static IEnumerable<TestCaseData> QueryOnlyMatchingCases()
    {
        yield return new TestCaseData(Filter("value", FilterType.Equals, JsonArray("""["0", "5"]""")), 2)
            .SetName("Equals on NUMERIC with an array of numeric strings");
        yield return new TestCaseData(Filter("value", FilterType.NotEquals, JsonArray("""["0", "5"]""")), 4)
            .SetName("NotEquals on NUMERIC with an array of numeric strings");
        yield return new TestCaseData(Filter("blockNumber", FilterType.Equals, JsonArray("[1, 2]")), 2)
            .SetName("Equals on BIGINT with an array of JSON integers");
    }

    [TestCaseSource(nameof(MatchingCases))]
    [TestCaseSource(nameof(QueryOnlyMatchingCases))]
    public async Task Query_filter_matches_rows_whatever_the_JSON_type_of_its_value(IFilterPredicateDto filter, int expected)
    {
        var parameters = new List<NpgsqlParameter>();
        var clause = CirclesRpcModule.BuildQueryPredicateClause(filter, parameters, ColumnTypes,
            columnTypesMatchDatabase: true);

        Assert.That(await CountAsync($"""SELECT count(*) FROM "Filtered" WHERE {clause}""", parameters),
            Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(MatchingCases))]
    public async Task Events_filter_matches_rows_whatever_the_JSON_type_of_its_value(IFilterPredicateDto filter, int expected)
    {
        var parameters = new List<NpgsqlParameter>();
        var clause = CirclesRpcModule.BuildPredicateClause(filter, parameters, "Filtered", ColumnTypes);

        Assert.That(await CountAsync($"""SELECT count(*) FROM "Filtered" t WHERE {clause}""", parameters),
            Is.EqualTo(expected));
    }

    // Rows: id TokenAddress, another address, "1234"; timestamp 2024-01-01, 2024-06-01, 2025-01-01.
    private static IEnumerable<TestCaseData> ViewCases()
    {
        yield return new TestCaseData(Filter("id", FilterType.Equals, TokenAddress), 1)
            .SetName("View: Equals with an address on a column declared BigInt");
        yield return new TestCaseData(Filter("id", FilterType.Like, "0x31%"), 1)
            .SetName("View: Like on a column declared BigInt");
        yield return new TestCaseData(Filter("id", FilterType.In, JsonArray($"""["{TokenAddress}", "0xdead"]""")), 1)
            .SetName("View: In with addresses on a column declared BigInt");
        yield return new TestCaseData(Filter("id", FilterType.Equals, "1234"), 1)
            .SetName("View: Equals with a decimal token id on a text column declared BigInt");
        yield return new TestCaseData(Filter("id", FilterType.In, JsonArray("""["1234"]""")), 1)
            .SetName("View: In with a decimal token id on a text column declared BigInt");
        yield return new TestCaseData(Filter("id", FilterType.NotEquals, "1234"), 2)
            .SetName("View: NotEquals with a decimal token id on a text column declared BigInt");
        yield return new TestCaseData(Filter("timestamp", FilterType.GreaterThanOrEquals, "2024-06-01T00:00:00Z"), 2)
            .SetName("View: GreaterThanOrEquals with an ISO date on a timestamptz column declared Int");
        yield return new TestCaseData(Filter("value", FilterType.NotEquals, "0"), 2)
            .SetName("View: NotEquals on NUMERIC with a numeric string");
        yield return new TestCaseData(Filter("value", FilterType.GreaterThan, 5), 2)
            .SetName("View: GreaterThan on NUMERIC with a JSON integer");
        yield return new TestCaseData(
                Conjunction(ConjunctionType.Or,
                    Filter("id", FilterType.Equals, TokenAddress),
                    Filter("id", FilterType.Equals, "1234")), 2)
            .SetName("View: Or conjunction of an address and a decimal token id");
    }

    // A view column's declared type can differ from the database, so a string value is sent
    // untyped and Postgres reads it as the column's real type.
    [TestCaseSource(nameof(ViewCases))]
    public async Task Query_filter_on_a_view_matches_by_the_database_column_type(IFilterPredicateDto filter, int expected)
    {
        var parameters = new List<NpgsqlParameter>();
        var clause = CirclesRpcModule.BuildQueryPredicateClause(filter, parameters, ViewColumnTypes,
            columnTypesMatchDatabase: false);

        Assert.That(await CountAsync($"""SELECT count(*) FROM "FilteredView" WHERE {clause}""", parameters),
            Is.EqualTo(expected));
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

/// <summary>
/// Filter value conversion checks that need no database, so they run where Docker is not available.
/// RpcDispatcher answers ArgumentException and JsonException with JSON-RPC "invalid params" (-32602)
/// and any other exception with "Internal server error".
/// </summary>
[TestFixture]
public class FilterValueColumnTypeUnitTests
{
    private static readonly IReadOnlyDictionary<string, string> ColumnTypes = FilterValueColumnTypeTests.ColumnTypes;

    private static FilterPredicateDto Filter(string column, FilterType type, object? value) =>
        FilterValueColumnTypeTests.Filter(column, type, value);

    private static IEnumerable<TestCaseData> RejectedCases()
    {
        var digits101 = new string('9', 101);

        yield return new TestCaseData(Filter("value", FilterType.NotEquals, "abc"), "value")
            .SetName("NotEquals on NUMERIC with a non-numeric string");
        yield return new TestCaseData(Filter("value", FilterType.In, FilterValueColumnTypeTests.JsonArray("""["1", "x"]""")), "value")
            .SetName("In on NUMERIC with a non-numeric element");
        yield return new TestCaseData(Filter("value", FilterType.Equals, "1,5"), "value")
            .SetName("Equals on NUMERIC with a thousands separator");
        yield return new TestCaseData(Filter("value", FilterType.Equals, "(5)"), "value")
            .SetName("Equals on NUMERIC with a number in parentheses");
        yield return new TestCaseData(Filter("value", FilterType.Equals, "5-"), "value")
            .SetName("Equals on NUMERIC with a trailing sign");
        yield return new TestCaseData(Filter("value", FilterType.Equals, "0x10"), "value")
            .SetName("Equals on NUMERIC with a hex string");
        yield return new TestCaseData(Filter("value", FilterType.Equals, "NaN"), "value")
            .SetName("Equals on NUMERIC with NaN");
        yield return new TestCaseData(Filter("value", FilterType.Equals, "1e1000"), "value")
            .SetName("Equals on NUMERIC with a four-digit exponent");
        yield return new TestCaseData(Filter("value", FilterType.Equals, digits101), "value")
            .SetName("Equals on NUMERIC with 101 digits");
        yield return new TestCaseData(Filter("value", FilterType.Equals, true), "value")
            .SetName("Equals on NUMERIC with a JSON boolean");
        yield return new TestCaseData(Filter("blockNumber", FilterType.Equals, "0x10"), "blockNumber")
            .SetName("Equals on BIGINT with a hex string");
        yield return new TestCaseData(Filter("blockNumber", FilterType.Equals, "1,5"), "blockNumber")
            .SetName("Equals on BIGINT with a thousands separator");
        yield return new TestCaseData(Filter("blockNumber", FilterType.Equals, "(5)"), "blockNumber")
            .SetName("Equals on BIGINT with a number in parentheses");
        yield return new TestCaseData(Filter("blockNumber", FilterType.Equals, "5-"), "blockNumber")
            .SetName("Equals on BIGINT with a trailing sign");
        yield return new TestCaseData(Filter("blockNumber", FilterType.Equals, digits101), "blockNumber")
            .SetName("Equals on BIGINT with 101 digits");
        yield return new TestCaseData(Filter("ratio", FilterType.Equals, "1,5"), "ratio")
            .SetName("Equals on DOUBLE with a thousands separator");
        yield return new TestCaseData(Filter("ratio", FilterType.Equals, "(5)"), "ratio")
            .SetName("Equals on DOUBLE with a number in parentheses");
        yield return new TestCaseData(Filter("ratio", FilterType.Equals, "5-"), "ratio")
            .SetName("Equals on DOUBLE with a trailing sign");
        yield return new TestCaseData(Filter("ratio", FilterType.Equals, digits101), "ratio")
            .SetName("Equals on DOUBLE with 101 digits");
        yield return new TestCaseData(Filter("flag", FilterType.Equals, "yes"), "flag")
            .SetName("Equals on BOOLEAN with a non-boolean string");
        yield return new TestCaseData(Filter("name", FilterType.Equals, 2.5), "name")
            .SetName("Equals on TEXT with a JSON fraction");
        yield return new TestCaseData(Filter("name", FilterType.Equals, true), "name")
            .SetName("Equals on TEXT with a JSON boolean");
        yield return new TestCaseData(Filter("name", FilterType.Like, 2.5), "name")
            .SetName("Like on TEXT with a JSON fraction");
        yield return new TestCaseData(Filter("owner", FilterType.Equals, 1.5), "owner")
            .SetName("Equals on an Address column with a JSON fraction");
        yield return new TestCaseData(Filter("owner", FilterType.Equals, false), "owner")
            .SetName("Equals on an Address column with a JSON boolean");
        yield return new TestCaseData(Filter("value", FilterType.Like, "1%"), "value")
            .SetName("Like on NUMERIC");
        yield return new TestCaseData(Filter("value", FilterType.ILike, "1%"), "value")
            .SetName("ILike on NUMERIC");
        yield return new TestCaseData(Filter("blockNumber", FilterType.NotLike, "1%"), "blockNumber")
            .SetName("NotLike on BIGINT");
        yield return new TestCaseData(Filter("ratio", FilterType.Like, "1%"), "ratio")
            .SetName("Like on DOUBLE");
        yield return new TestCaseData(Filter("flag", FilterType.ILike, "t%"), "flag")
            .SetName("ILike on BOOLEAN");
    }

    [TestCaseSource(nameof(RejectedCases))]
    public void Query_filter_rejects_a_value_its_column_cannot_hold(FilterPredicateDto filter, string column)
    {
        Assert.That(() => CirclesRpcModule.BuildQueryPredicateClause(filter, new List<NpgsqlParameter>(), ColumnTypes,
                columnTypesMatchDatabase: true),
            Throws.ArgumentException.With.Message.Contains($"'{column}'"));
    }

    [TestCaseSource(nameof(RejectedCases))]
    public void Events_filter_rejects_a_value_its_column_cannot_hold(FilterPredicateDto filter, string column)
    {
        Assert.That(() => CirclesRpcModule.BuildPredicateClause(filter, new List<NpgsqlParameter>(), "Filtered", ColumnTypes),
            Throws.ArgumentException.With.Message.Contains($"'{column}'"));
    }

    // Before the length cap, parsing a 4,000,000-digit string as a BigInteger took about two seconds,
    // and circles_events converts the filter once per event table.
    [TestCase("value")]
    [TestCase("blockNumber")]
    [TestCase("ratio")]
    public void A_long_numeric_string_is_rejected_without_parsing_it(string column)
    {
        var filter = Filter(column, FilterType.Equals, new string('9', 4_000_000));
        var stopwatch = Stopwatch.StartNew();

        Assert.That(() => CirclesRpcModule.BuildQueryPredicateClause(filter, new List<NpgsqlParameter>(), ColumnTypes,
                columnTypesMatchDatabase: true),
            Throws.ArgumentException);
        Assert.That(() => CirclesRpcModule.BuildPredicateClause(filter, new List<NpgsqlParameter>(), "Filtered", ColumnTypes),
            Throws.ArgumentException);
        Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(500));
    }

    private static IEnumerable<TestCaseData> ConvertedValueCases()
    {
        yield return new TestCaseData("3", "Int", 3L).SetName("A numeric string on Int becomes a long");
        yield return new TestCaseData(" -3 ", "Int", -3L).SetName("A signed numeric string with spaces on Int becomes a long");
        yield return new TestCaseData("3.5", "Int", 3.5m).SetName("A fractional string on Int becomes a decimal");
        yield return new TestCaseData(5, "Int", 5).SetName("A JSON integer on Int stays an int");
        yield return new TestCaseData(2.5, "Int", 2.5).SetName("A JSON fraction on Int stays a double");
        yield return new TestCaseData("1.5", "Double", 1.5).SetName("A numeric string on Double becomes a double");
        yield return new TestCaseData(5, "BigInt", 5).SetName("A JSON integer on BigInt stays an int");
        yield return new TestCaseData(4.5, "BigInt", 4.5).SetName("A JSON fraction on BigInt stays a double");
        yield return new TestCaseData(" true ", "Boolean", true).SetName("A boolean string on Boolean becomes a bool");
        yield return new TestCaseData(7, "String", "7").SetName("A JSON integer on String becomes a string");
        yield return new TestCaseData(7L, "Address", "7").SetName("A JSON long on Address becomes a string");
        yield return new TestCaseData("x", "String", "x").SetName("A string on String stays a string");
        yield return new TestCaseData(2.5, "Bytes", 2.5).SetName("A value on another column type stays as sent");
    }

    [TestCaseSource(nameof(ConvertedValueCases))]
    public void ConvertFilterValue_converts_to_the_declared_column_type(object value, string columnType, object expected)
    {
        var converted = CirclesRpcModule.ConvertFilterValue(value, "column", columnType, columnTypesMatchDatabase: true);

        Assert.That(converted, Is.EqualTo(expected).And.TypeOf(expected.GetType()));
    }

    [Test]
    public void ConvertFilterValue_keeps_null()
    {
        Assert.That(CirclesRpcModule.ConvertFilterValue(null, "value", "BigInt", columnTypesMatchDatabase: true), Is.Null);
        Assert.That(CirclesRpcModule.ConvertFilterValue(null, "value", "BigInt", columnTypesMatchDatabase: false), Is.Null);
    }

    // Postgres parses an untyped parameter as the type of the column it is compared with,
    // exactly and with the column's own index.
    [Test]
    public void A_numeric_string_on_a_NUMERIC_table_column_is_sent_untyped_and_trimmed()
    {
        var parameters = new List<NpgsqlParameter>();
        CirclesRpcModule.BuildQueryPredicateClause(Filter("value", FilterType.Equals, " 1234 "), parameters, ColumnTypes,
            columnTypesMatchDatabase: true);

        Assert.That(parameters, Has.Count.EqualTo(1));
        Assert.That(parameters[0].NpgsqlDbType, Is.EqualTo(NpgsqlDbType.Unknown));
        Assert.That(parameters[0].Value, Is.EqualTo("1234"));
    }

    [Test]
    public void Numeric_strings_in_an_Events_In_filter_are_sent_untyped()
    {
        var parameters = new List<NpgsqlParameter>();
        CirclesRpcModule.BuildPredicateClause(
            Filter("value", FilterType.In, FilterValueColumnTypeTests.JsonArray("""["1", "2"]""")),
            parameters, "Filtered", ColumnTypes);

        Assert.That(parameters.Select(p => p.NpgsqlDbType), Is.All.EqualTo(NpgsqlDbType.Unknown));
        Assert.That(parameters.Select(p => p.Value), Is.EqualTo(new object[] { "1", "2" }));
    }

    [Test]
    public void A_view_filter_sends_strings_untyped_and_other_values_as_sent()
    {
        var parameters = new List<NpgsqlParameter>();
        CirclesRpcModule.BuildQueryPredicateClause(
            Filter("id", FilterType.In, FilterValueColumnTypeTests.JsonArray("""["1234", 5]""")),
            parameters, FilterValueColumnTypeTests.ViewColumnTypes, columnTypesMatchDatabase: false);

        Assert.That(parameters, Has.Count.EqualTo(2));
        Assert.That(parameters[0].NpgsqlDbType, Is.EqualTo(NpgsqlDbType.Unknown));
        Assert.That(parameters[0].Value, Is.EqualTo("1234"));
        Assert.That(parameters[1].Value, Is.EqualTo(5).And.TypeOf<int>());
    }

    [Test]
    public void A_view_Like_pattern_is_sent_untyped()
    {
        var parameters = new List<NpgsqlParameter>();
        CirclesRpcModule.BuildQueryPredicateClause(Filter("id", FilterType.Like, "0x31%"), parameters,
            FilterValueColumnTypeTests.ViewColumnTypes, columnTypesMatchDatabase: false);

        Assert.That(parameters[0].NpgsqlDbType, Is.EqualTo(NpgsqlDbType.Unknown));
        Assert.That(parameters[0].Value, Is.EqualTo("0x31%"));
    }

    [TestCase("V_CrcV2_Transfers", true)]
    [TestCase("V_Crc_Avatars", true)]
    [TestCase("CrcV2_Transfers", false)]
    [TestCase("System_Block", false)]
    public void IsViewTable_recognises_view_names(string tableName, bool expected)
    {
        Assert.That(CirclesRpcModule.IsViewTable(tableName), Is.EqualTo(expected));
    }

    // circles_query and circles_events used StartsWith('V') on the namespace, PostgresDb uses
    // StartsWith("V_") on the namespace. Both must name the same views as IsViewTable.
    [Test]
    public void IsViewTable_names_the_same_views_as_the_previous_rule_and_PostgresDb()
    {
        var tables = SchemaProvider.AllSchemas.SelectMany(s => s.Tables.Keys).ToList();
        Assert.That(tables, Is.Not.Empty);

        Assert.Multiple(() =>
        {
            foreach (var (ns, table) in tables)
            {
                var tableName = $"{ns}_{table}";
                Assert.That(CirclesRpcModule.IsViewTable(tableName), Is.EqualTo(ns.StartsWith('V')), tableName);
                Assert.That(CirclesRpcModule.IsViewTable(tableName), Is.EqualTo(ns.StartsWith("V_")), tableName);
            }
        });
    }
}
