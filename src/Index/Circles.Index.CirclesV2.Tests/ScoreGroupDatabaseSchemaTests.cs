using System.Numerics;
using Circles.Common;
using Circles.Index.CirclesV2.ScoreGroup;
using Nethermind.Int256;
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

    [Test]
    public void ScoreGroupBigIntMappings_ReturnBigIntegerForNumericColumns()
    {
        var dbSchema = new ScoreGroupDatabaseSchema();
        var map = dbSchema.SchemaPropertyMap.Map[("CrcV2_ScoreGroup", "PersonalMinted")];
        var personalMinted = new PersonalMinted(
            BlockNumber: 1,
            Timestamp: 2,
            TransactionIndex: 3,
            LogIndex: 4,
            TransactionHash: "0xabc",
            Emitter: "0x1111111111111111111111111111111111111111",
            Group: "0x2222222222222222222222222222222222222222",
            Collateral: new UInt256(123),
            Amount: new UInt256(456),
            Score: new UInt256(789),
            MintedAmountOnToday: new UInt256(101112),
            Day: new UInt256(131415));

        Assert.Multiple(() =>
        {
            Assert.That(map["collateral"](personalMinted), Is.TypeOf<BigInteger>());
            Assert.That(map["amount"](personalMinted), Is.TypeOf<BigInteger>());
            Assert.That(map["score"](personalMinted), Is.TypeOf<BigInteger>());
            Assert.That(map["mintedAmountOnToday"](personalMinted), Is.TypeOf<BigInteger>());
            Assert.That(map["day"](personalMinted), Is.TypeOf<BigInteger>());
        });
    }
}
