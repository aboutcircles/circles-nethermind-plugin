using NUnit.Framework;

namespace Circles.Common.Tests;

/// <summary>
/// Tests for CompositeDatabaseSchema startup validation: duplicate (namespace, table)
/// definitions across protocol schemas must fail fast with a message naming the
/// colliding components.
/// </summary>
[TestFixture]
public class CompositeDatabaseSchemaTests
{
    private sealed class SchemaA : BaseDatabaseSchema
    {
        public SchemaA()
        {
            Tables[("CrcTest", "Transfer")] = new EventSchema("CrcTest", "Transfer", new byte[32], []);
            Tables[("CrcTest", "Trust")] = new EventSchema("CrcTest", "Trust", new byte[32], []);
        }
    }

    private sealed class SchemaB : BaseDatabaseSchema
    {
        public SchemaB()
        {
            Tables[("CrcTest", "Signup")] = new EventSchema("CrcTest", "Signup", new byte[32], []);
        }
    }

    private sealed class SchemaCollidingWithA : BaseDatabaseSchema
    {
        public SchemaCollidingWithA()
        {
            Tables[("CrcTest", "Transfer")] = new EventSchema("CrcTest", "Transfer", new byte[32], []);
        }
    }

    [Test]
    public void DistinctSchemas_ComposeSuccessfully()
    {
        var composite = new CompositeDatabaseSchema([new SchemaA(), new SchemaB()]);

        Assert.That(composite.Tables, Has.Count.EqualTo(3));
        Assert.That(composite.Tables.ContainsKey(("CrcTest", "Transfer")), Is.True);
        Assert.That(composite.Tables.ContainsKey(("CrcTest", "Signup")), Is.True);
    }

    [Test]
    public void DuplicateTableAcrossSchemas_ThrowsWithComponentNames()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _ = new CompositeDatabaseSchema([new SchemaA(), new SchemaB(), new SchemaCollidingWithA()]));

        Assert.That(ex!.Message, Does.Contain("CrcTest_Transfer"));
        Assert.That(ex.Message, Does.Contain(nameof(SchemaA)));
        Assert.That(ex.Message, Does.Contain(nameof(SchemaCollidingWithA)));
        Assert.That(ex.Message, Does.Not.Contain(nameof(SchemaB)));
    }
}
