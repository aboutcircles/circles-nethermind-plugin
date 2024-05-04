using Circles.Index.Common;

namespace Circles.Index.V1;

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
        },
        {
            Tables.CrcV1HubTransfer,
            new TableSchema(Tables.CrcV1HubTransfer,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.FromAddress, ValueTypes.Address, false, true),
                (Columns.ToAddress, ValueTypes.Address, false, true),
                (Columns.Amount, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Tables.CrcV1Signup,
            new TableSchema(Tables.CrcV1Signup,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.CirclesAddress, ValueTypes.Address, false, true),
                (Columns.TokenAddress, ValueTypes.Address, false, true)
            ])
        },
        {
            Tables.CrcV1Trust,
            new TableSchema(Tables.CrcV1Trust,
            [
                (Columns.BlockNumber, ValueTypes.Int, true, true),
                (Columns.Timestamp, ValueTypes.Int, false, true),
                (Columns.TransactionIndex, ValueTypes.Int, true, true),
                (Columns.LogIndex, ValueTypes.Int, true, true),
                (Columns.TransactionHash, ValueTypes.String, false, true),
                (Columns.UserAddress, ValueTypes.Address, false, true),
                (Columns.CanSendToAddress, ValueTypes.Address, false, true),
                (Columns.Limit, ValueTypes.Int, false, false)
            ])
        }
    };
}