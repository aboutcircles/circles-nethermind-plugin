namespace Circles.Index.Common;

public class DatabaseSchema : IDatabaseSchema
{
    public IDictionary<Tables, TableSchema> Tables { get; } = new Dictionary<Tables, TableSchema>
    {
        {
            Common.Tables.Block,
            new TableSchema(Common.Tables.Block, [
                new (Columns.BlockNumber, ValueTypes.Int, true, true),
                new (Columns.Timestamp, ValueTypes.Int, false, true),
                new (Columns.BlockHash, ValueTypes.String, false, true)
            ])
        }
    };
}