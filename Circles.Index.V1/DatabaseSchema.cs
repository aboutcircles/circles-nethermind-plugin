using System.Numerics;
using Circles.Index.Common;

namespace Circles.Index.V1;

public class DatabaseSchema : IDatabaseSchema
{
    public SchemaPropertyMap SchemaPropertyMap { get; } = new();

    public EventDtoTableMap EventDtoTableMap { get; } = new();

    public IDictionary<Tables, TableSchema> Tables { get; } = new Dictionary<Tables, TableSchema>
    {
        {
            Common.Tables.CrcV1HubTransfer,
            new TableSchema(Common.Tables.CrcV1HubTransfer,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.FromAddress, ValueTypes.Address, false, true),
                new(Columns.ToAddress, ValueTypes.Address, false, true),
                new(Columns.Amount, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Common.Tables.CrcV1Signup,
            new TableSchema(Common.Tables.CrcV1Signup,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.CirclesAddress, ValueTypes.Address, false, true),
                new(Columns.TokenAddress, ValueTypes.Address, false, true)
            ])
        },
        {
            Common.Tables.CrcV1Trust,
            new TableSchema(Common.Tables.CrcV1Trust,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.UserAddress, ValueTypes.Address, false, true),
                new(Columns.CanSendToAddress, ValueTypes.Address, false, true),
                new(Columns.Limit, ValueTypes.Int, false, false)
            ])
        }
    };

    public DatabaseSchema()
    {
        EventDtoTableMap.Add<CirclesSignupData>(Common.Tables.CrcV1Signup);
        SchemaPropertyMap.Add(Common.Tables.CrcV1Signup, new Dictionary<Columns, Func<CirclesSignupData, object?>>
        {
            { Columns.BlockNumber, e => e.BlockNumber },
            { Columns.Timestamp, e => e.Timestamp },
            { Columns.TransactionIndex, e => e.TransactionIndex },
            { Columns.LogIndex, e => e.LogIndex },
            { Columns.TransactionHash, e => e.TransactionHash },
            { Columns.CirclesAddress, e => e.CirclesAddress },
            { Columns.TokenAddress, e => e.TokenAddress ?? string.Empty }
        });

        EventDtoTableMap.Add<CirclesTrustData>(Common.Tables.CrcV1Trust);
        SchemaPropertyMap.Add(Common.Tables.CrcV1Trust, new Dictionary<Columns, Func<CirclesTrustData, object?>>
        {
            { Columns.BlockNumber, e => e.BlockNumber },
            { Columns.Timestamp, e => e.Timestamp },
            { Columns.TransactionIndex, e => e.TransactionIndex },
            { Columns.LogIndex, e => e.LogIndex },
            { Columns.TransactionHash, e => e.TransactionHash },
            { Columns.UserAddress, e => e.UserAddress },
            { Columns.CanSendToAddress, e => e.CanSendToAddress },
            { Columns.Limit, e => e.Limit }
        });

        EventDtoTableMap.Add<CirclesHubTransferData>(Common.Tables.CrcV1HubTransfer);
        SchemaPropertyMap.Add(Common.Tables.CrcV1HubTransfer,
            new Dictionary<Columns, Func<CirclesHubTransferData, object?>>
            {
                { Columns.BlockNumber, e => e.BlockNumber },
                { Columns.Timestamp, e => e.Timestamp },
                { Columns.TransactionIndex, e => e.TransactionIndex },
                { Columns.LogIndex, e => e.LogIndex },
                { Columns.TransactionHash, e => e.TransactionHash },
                { Columns.FromAddress, e => e.FromAddress },
                { Columns.ToAddress, e => e.ToAddress },
                { Columns.Amount, e => (BigInteger)e.Amount }
            });

        EventDtoTableMap.Add<CirclesHubTransferData>(Common.Tables.CrcV1HubTransfer);
        SchemaPropertyMap.Add(Common.Tables.Erc20Transfer, new Dictionary<Columns, Func<Erc20TransferData, object?>>
        {
            { Columns.BlockNumber, e => e.BlockNumber },
            { Columns.Timestamp, e => e.Timestamp },
            { Columns.TransactionIndex, e => e.TransactionIndex },
            { Columns.LogIndex, e => e.LogIndex },
            { Columns.TransactionHash, e => e.TransactionHash },
            { Columns.TokenAddress, e => e.TokenAddress },
            { Columns.FromAddress, e => e.From },
            { Columns.ToAddress, e => e.To },
            { Columns.Amount, e => (BigInteger)e.Value }
        });
    }
}