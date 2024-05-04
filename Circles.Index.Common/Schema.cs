namespace Circles.Index.Common;

public class Schema : ISchema
{
    public IDictionary<Tables, TableSchema> TableSchemas { get; } = new Dictionary<Tables, TableSchema>
    {
        {
            Tables.Block,
            new TableSchema(Tables.Block, [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.BlockHash, ValueTypes.String, false, true)
            ])
        }
    };
}