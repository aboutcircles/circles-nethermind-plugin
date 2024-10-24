using System.Data;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Circles.Index.Common;
using Circles.Index.Query.Dto;

namespace Circles.Index.Query.Tests;

public class TestDatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();
    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } =
        new Dictionary<(string Namespace, string Table), EventSchema>
        {
            {
                ("public", "Users"),
                new EventSchema("public", "Users", new byte[32], [
                    new EventFieldSchema("Name", ValueTypes.String, false),
                    new EventFieldSchema("Age", ValueTypes.Int, false),
                    new EventFieldSchema("Country", ValueTypes.String, false)
                ])
            }
        };
}

public class TestDatabase : IDatabaseUtils
{
    public IDatabaseSchema Schema { get; } = new TestDatabaseSchema();

    public IEnumerable<object[]> Select(Select select)
    {
        throw new NotImplementedException();
    }

    public IDbDataParameter CreateParameter(string? name, object? value)
    {
        return new TestDbDataParameter(name, value);
    }
}

public class TestDbDataParameter(string? name, object? value) : IDbDataParameter
{
    public DbType DbType { get; set; }
    public ParameterDirection Direction { get; set; }
    public bool IsNullable => false;

#pragma warning disable CS8767 // Nullability of reference types in type of parameter doesn't match implicitly implemented member (possibly because of nullability attributes).
    public string ParameterName { get; set; } = name ?? "";
    public string SourceColumn { get; set; } = "";
#pragma warning restore CS8767 // Nullability of reference types in type of parameter doesn't match implicitly implemented member (possibly because of nullability attributes).
    public DataRowVersion SourceVersion { get; set; }
    public object? Value { get; set; } = value;
    public byte Precision { get; set; }
    public byte Scale { get; set; }
    public int Size { get; set; }
}

public class Tests
{
    readonly IDatabaseUtils _database = new TestDatabase();

    [SetUp]
    public void Setup()
    {
    }


    [Test]
    public void ParseStringsFromData()
    {
        var hexString =
            "00000000000000000000000000000000000000000000000000000000000000400000000000000000000000000000000000000000000000000000000000000080000000000000000000000000000000000000000000000000000000000000000b50657465722047726f757000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000035054520000000000000000000000000000000000000000000000000000000000";

        var bytes = Convert.FromHexString(hexString);

        var strings = LogDataStringDecoder.ReadStrings(bytes);

        Assert.That(strings[0], Is.EqualTo("Peter Group"));
        Assert.That(strings[1], Is.EqualTo("PTR"));
    }

    [Test]
    public void FilterPredicate_ToSql_Equals()
    {
        var predicate = new FilterPredicate("Name", FilterType.Equals, "John");
        var generatedSql = predicate.ToSql(_database);

        // Use regex to match the uuid part of the parameter name
        var expectedSql = "\"Name\" = @Name_([0-9a-f]{32})";
        var expectedSqlMatch = Regex.Match(generatedSql.Sql, expectedSql);
        Assert.That(expectedSqlMatch.Success);
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(1));

        var expectedParameterName = "Name_([0-9a-f]{32})";
        var expectedParameterNameMatch =
            Regex.Match(generatedSql.Parameters.First().ParameterName, expectedParameterName);
        Assert.That(expectedParameterNameMatch.Success);
        Assert.That(generatedSql.Parameters.First().Value, Is.EqualTo("John"));

        Assert.That(expectedSqlMatch.Groups[1].Value, Is.EqualTo(expectedParameterNameMatch.Groups[1].Value));
    }

    [Test]
    public void FilterPredicate_ToSql_NotEquals()
    {
        var predicate = new FilterPredicate("Name", FilterType.NotEquals, "John");
        var generatedSql = predicate.ToSql(_database);

        var expectedSql = "\"Name\" != @Name_[0-9a-f]{32}";
        Assert.That(Regex.IsMatch(generatedSql.Sql, expectedSql));
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(1));
    }

    [Test]
    public void FilterPredicate_ToSql_GreaterThan()
    {
        var predicate = new FilterPredicate("Age", FilterType.GreaterThan, 30);
        var generatedSql = predicate.ToSql(_database);

        var expectedSql = "\"Age\" > @Age_[0-9a-f]{32}";
        Assert.That(Regex.IsMatch(generatedSql.Sql, expectedSql));
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(1));
    }

    [Test]
    public void FilterPredicate_ToSql_Like()
    {
        var predicate = new FilterPredicate("Name", FilterType.Like, "J%");
        var generatedSql = predicate.ToSql(_database);

        var expectedSql = "\"Name\" LIKE @Name_[0-9a-f]{32}";
        Assert.That(Regex.IsMatch(generatedSql.Sql, expectedSql));
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(1));
    }

    [Test]
    public void FilterPredicate_ToSql_In()
    {
        var predicate = new FilterPredicate("Age", FilterType.In, new List<object> { 25, 30, 35 });
        var generatedSql = predicate.ToSql(_database);

        var expectedSql = "\"Age\" IN \\(@Age_[0-9a-f]{32}_0, @Age_[0-9a-f]{32}_1, @Age_[0-9a-f]{32}_2\\)";
        Assert.That(Regex.IsMatch(generatedSql.Sql, expectedSql));
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(3));
    }

    [Test]
    public void OrderBy_ToSql()
    {
        var orderBy = new OrderBy("Name", "ASC");
        var generatedSql = orderBy.ToSql(_database);

        Assert.That(generatedSql.Sql, Is.EqualTo("\"Name\" ASC"));
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(0));
    }

    [Test]
    public void Conjunction_ToSql_And()
    {
        var predicate1 = new FilterPredicate("Name", FilterType.Equals, "John");
        var predicate2 = new FilterPredicate("Age", FilterType.GreaterThan, 30);
        var conjunction = new Conjunction(ConjunctionType.And, new IFilterPredicate[] { predicate1, predicate2 });

        var generatedSql = conjunction.ToSql(_database);

        var expectedSql = "(\"Name\" = @Name_[0-9a-f]{32} AND \"Age\" > @Age_[0-9a-f]{32})";
        Assert.That(Regex.IsMatch(generatedSql.Sql, expectedSql));
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(2));
    }

    [Test]
    public void Conjunction_ToSql_Or()
    {
        var predicate1 = new FilterPredicate("Name", FilterType.Equals, "John");
        var predicate2 = new FilterPredicate("Age", FilterType.GreaterThan, 30);
        var conjunction = new Conjunction(ConjunctionType.Or, new IFilterPredicate[] { predicate1, predicate2 });

        var generatedSql = conjunction.ToSql(_database);

        var expectedSql = "(\"Name\" = @Name_[0-9a-f]{32} OR \"Age\" > @Age_[0-9a-f]{32})";
        Assert.That(Regex.IsMatch(generatedSql.Sql, expectedSql));
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(2));
    }

    [Test]
    public void Conjunction_ToSql_Nested()
    {
        var predicate1 = new FilterPredicate("Name", FilterType.Equals, "John");
        var predicate2 = new FilterPredicate("Age", FilterType.GreaterThan, 30);
        var innerConjunction = new Conjunction(ConjunctionType.Or, new IFilterPredicate[] { predicate1, predicate2 });

        var predicate3 = new FilterPredicate("Country", FilterType.Equals, "USA");
        var outerConjunction =
            new Conjunction(ConjunctionType.And, new IFilterPredicate[] { innerConjunction, predicate3 });

        var generatedSql = outerConjunction.ToSql(_database);

        // (("Name" = @Name_56a4d14bb806402bb324662f75c79c14 OR "Age" > @Age_0f82e44befb94ea78ac8b195483e39ee) AND "Country" = @Country_a98a69fdeead40348065abda5b8d72c2)
        var expectedSql =
            "\\(\\(\"Name\" = @Name_[0-9a-f]{32} OR \"Age\" > @Age_[0-9a-f]{32}\\) AND \"Country\" = @Country_[0-9a-f]{32}\\)";
        Assert.That(Regex.IsMatch(generatedSql.Sql, expectedSql));
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(3));
    }

    [Test]
    public void Select_ToSql_Basic()
    {
        var select = new Select("public", "Users", new[] { "Name", "Age" }, Array.Empty<IFilterPredicate>(),
            Array.Empty<OrderBy>());

        var generatedSql = select.ToSql(_database);

        var expectedSql = "SELECT \"Name\", \"Age\" FROM \"public_Users\"";
        Assert.That(generatedSql.Sql, Is.EqualTo(expectedSql));
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(0));
    }

    [Test]
    public void Select_ToSql_WithFilter()
    {
        var predicate = new FilterPredicate("Name", FilterType.Equals, "John");
        var select = new Select("public", "Users", new[] { "Name", "Age" }, new IFilterPredicate[] { predicate },
            Array.Empty<OrderBy>());

        var generatedSql = select.ToSql(_database);

        var expectedSql = "SELECT \"Name\", \"Age\" FROM \"public_Users\" WHERE \"Name\" = @Name_[0-9a-f]{32}";
        Assert.That(Regex.IsMatch(generatedSql.Sql, expectedSql));
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(1));
    }

    [Test]
    public void Select_ToSql_WithOrderBy()
    {
        var orderBy = new OrderBy("Age", "DESC");
        var select = new Select("public", "Users", new[] { "Name", "Age" }, Array.Empty<IFilterPredicate>(),
            new[] { orderBy });

        var generatedSql = select.ToSql(_database);

        var expectedSql = "SELECT \"Name\", \"Age\" FROM \"public_Users\" ORDER BY \"Age\" DESC";
        Assert.That(generatedSql.Sql, Is.EqualTo(expectedSql));
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(0));
    }

    [Test]
    public void Select_ToSql_WithFilterAndOrderBy()
    {
        var predicate = new FilterPredicate("Name", FilterType.Equals, "John");
        var orderBy = new OrderBy("Age", "DESC");
        var select = new Select("public", "Users", new[] { "Name", "Age" }, new IFilterPredicate[] { predicate },
            new[] { orderBy });

        var generatedSql = select.ToSql(_database);

        var expectedSql =
            "SELECT \"Name\", \"Age\" FROM \"public_Users\" WHERE \"Name\" = @Name_[0-9a-f]{32} ORDER BY \"Age\" DESC";
        Assert.That(Regex.IsMatch(generatedSql.Sql, expectedSql));
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(1));
    }

    [Test]
    public void Select_ToSql_WithDistinct()
    {
        var select = new Select("public", "Users", new[] { "Name", "Age" }, Array.Empty<IFilterPredicate>(),
            Array.Empty<OrderBy>(), null, true);

        var generatedSql = select.ToSql(_database);

        var expectedSql = "SELECT DISTINCT \"Name\", \"Age\" FROM \"public_Users\"";
        Assert.That(generatedSql.Sql, Is.EqualTo(expectedSql));
        Assert.That(generatedSql.Parameters.Count(), Is.EqualTo(0));
    }


    [Test]
    public void JsonSerialization_Deserialization()
    {
        var predicate = new FilterPredicate("Name", FilterType.Equals, "John");
        var orderBy = new OrderBy("Age", "DESC");
        var select = new Select("public", "Users", new[] { "Name", "Age" }, new IFilterPredicate[] { predicate },
            new[] { orderBy }, null, true);

        var selectDto = select.ToDto();

        var options = new JsonSerializerOptions();
        options.Converters.Add(new FilterPredicateDtoConverter());
        options.Converters.Add(new JsonStringEnumConverter());

        var json = JsonSerializer.Serialize(selectDto, options);

        var deserializedSelectDto = JsonSerializer.Deserialize<SelectDto>(json, options);
        Assert.That(deserializedSelectDto, Is.Not.Null);
        var deserializedSelect = deserializedSelectDto.ToModel();

        Assert.That(deserializedSelect.Namespace, Is.EqualTo(select.Namespace));
        Assert.That(deserializedSelect.Table, Is.EqualTo(select.Table));
        Assert.That(deserializedSelect.Columns, Is.EqualTo(select.Columns).AsCollection);
        Assert.That(deserializedSelect.Distinct, Is.EqualTo(select.Distinct));

        var deserializedPredicate = (FilterPredicate)deserializedSelect.Filter.First();
        Assert.That(deserializedPredicate.Column, Is.EqualTo(predicate.Column));
        Assert.That(deserializedPredicate.FilterType, Is.EqualTo(predicate.FilterType));
        Assert.That(deserializedPredicate.Value, Is.EqualTo(predicate.Value));

        var deserializedOrderBy = deserializedSelect.Order.First();
        Assert.That(deserializedOrderBy.Column, Is.EqualTo(orderBy.Column));
        Assert.That(deserializedOrderBy.SortOrder, Is.EqualTo(orderBy.SortOrder));
    }

    [Test]
    public void JsonSerialization_Deserialization_Complex()
    {
        var predicate1 = new FilterPredicate("Name", FilterType.Equals, "John");
        var predicate2 = new FilterPredicate("Age", FilterType.GreaterThan, 30);
        var conjunction = new Conjunction(ConjunctionType.And, [predicate1, predicate2]);

        var orderBy = new OrderBy("Age", "DESC");
        var select = new Select(
            "public"
            , "Users"
            , new[] { "Name", "Age" }
            , new IFilterPredicate[] { conjunction }
            , new[] { orderBy }
            , 10
            , true);

        var selectDto = select.ToDto();
        var options = new JsonSerializerOptions
        {
            Converters =
            {
                new FilterPredicateDtoConverter(),
                new JsonStringEnumConverter()
            }
        };

        var json = JsonSerializer.Serialize(selectDto, options);

        var deserializedSelectDto = JsonSerializer.Deserialize<SelectDto>(json, options);
        Assert.That(deserializedSelectDto, Is.Not.Null);
        var deserializedSelect = deserializedSelectDto.ToModel();

        Assert.That(deserializedSelect.Namespace, Is.EqualTo(select.Namespace));
        Assert.That(deserializedSelect.Table, Is.EqualTo(select.Table));
        Assert.That(deserializedSelect.Columns, Is.EqualTo(select.Columns).AsCollection);
        Assert.That(deserializedSelect.Distinct, Is.EqualTo(select.Distinct));
        Assert.That(deserializedSelect.Limit, Is.EqualTo(select.Limit));

        var deserializedConjunction = (Conjunction)deserializedSelect.Filter.First();
        Assert.That(deserializedConjunction.ConjunctionType, Is.EqualTo(conjunction.ConjunctionType));
        Assert.That(deserializedConjunction.Predicates.Length, Is.EqualTo(2));

        var deserializedPredicate1 = (FilterPredicate)deserializedConjunction.Predicates[0];
        Assert.That(deserializedPredicate1.Column, Is.EqualTo(predicate1.Column));
        Assert.That(deserializedPredicate1.FilterType, Is.EqualTo(predicate1.FilterType));
        Assert.That(deserializedPredicate1.Value, Is.EqualTo(predicate1.Value));

        var deserializedPredicate2 = (FilterPredicate)deserializedConjunction.Predicates[1];
        Assert.That(deserializedPredicate2.Column, Is.EqualTo(predicate2.Column));
        Assert.That(deserializedPredicate2.FilterType, Is.EqualTo(predicate2.FilterType));
        Assert.That(deserializedPredicate2.Value, Is.EqualTo(predicate2.Value));

        var deserializedOrderBy = deserializedSelect.Order.First();
        Assert.That(deserializedOrderBy.Column, Is.EqualTo(orderBy.Column));
        Assert.That(deserializedOrderBy.SortOrder, Is.EqualTo(orderBy.SortOrder));
    }

    [Test]
    public void B()
    {
        var t = BigInteger.Parse("238208892873504508097789456176502153024265176387")
            * (
                BigInteger.Parse("31556952")
                                 - BigInteger.Parse("4142484904995219")
            )
            + BigInteger.Parse("238208892873504508097789456176502153024265176387")
                               * BigInteger.Parse("4142484904995219");
        
        
    }
}