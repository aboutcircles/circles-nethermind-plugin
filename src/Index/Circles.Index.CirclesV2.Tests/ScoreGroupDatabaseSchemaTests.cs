using Circles.Common;
using ScoreGroupDatabaseSchema = Circles.Index.CirclesV2.ScoreGroup.DatabaseSchema;

namespace Circles.Index.CirclesV2.Tests;

[TestFixture]
public class ScoreGroupDatabaseSchemaTests
{
    [Test]
    public void ScoreGroupEventSchemas_IncludeEmitterColumnForPolicyScopedQueries()
    {
        var schemas = new[]
        {
            ScoreGroupDatabaseSchema.GroupInitialized,
            ScoreGroupDatabaseSchema.MerkleRootUpdated,
            ScoreGroupDatabaseSchema.HistoricalSupply,
            ScoreGroupDatabaseSchema.PersonalMinted,
            ScoreGroupDatabaseSchema.RouterMinted
        };

        Assert.Multiple(() =>
        {
            foreach (var schema in schemas)
            {
                var emitterColumns = schema.Columns.Where(c => c.Column == "emitter").ToList();

                Assert.That(emitterColumns, Has.Count.EqualTo(1), $"{schema.Table} must expose one emitter column");
                Assert.That(schema.Columns[5].Column, Is.EqualTo("emitter"), $"{schema.Table} emitter column must follow base log columns");
                Assert.That(schema.Columns[5].Type, Is.EqualTo(ValueTypes.Address), $"{schema.Table} emitter column must be an address");
                Assert.That(schema.Columns[5].IsIndexed, Is.True, $"{schema.Table} emitter column should be indexed for policy filters");
            }
        });
    }
}
