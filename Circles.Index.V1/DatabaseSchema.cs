using System.Numerics;
using Circles.Index.Common;
using Nethermind.Core.Crypto;

namespace Circles.Index.V1;

public class DatabaseSchema : IDatabaseSchema
{
    public SchemaPropertyMap SchemaPropertyMap { get; } = new();

    public EventDtoTableMap EventDtoTableMap { get; } = new();

    public IDictionary<string, EventSchema> Tables { get; } = new Dictionary<string, EventSchema>
    {
        {
            "CrcV1HubTransfer",
            new EventSchema("CrcV1HubTransfer", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("FromAddress", ValueTypes.Address, true),
                new("ToAddress", ValueTypes.Address, true),
                new("Amount", ValueTypes.BigInt, false)
            ])
        },
        {
            "CrcV1Signup",
            new EventSchema("CrcV1Signup", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("CirclesAddress", ValueTypes.Address, true),
                new("TokenAddress", ValueTypes.Address, true)
            ])
        },
        {
            "CrcV1Trust",
            new EventSchema("CrcV1Trust", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("UserAddress", ValueTypes.Address, true),
                new("CanSendToAddress", ValueTypes.Address, true),
                new("Limit", ValueTypes.Int, false)
            ])
        }
    };

    public DatabaseSchema()
    {
        EventDtoTableMap.Add<CirclesSignupData>("CrcV1Signup");
        SchemaPropertyMap.Add("CrcV1Signup", 
            new Dictionary<string, Func<CirclesSignupData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "CirclesAddress", e => e.CirclesAddress },
                { "TokenAddress", e => e.TokenAddress ?? string.Empty }
            });

        EventDtoTableMap.Add<CirclesTrustData>("CrcV1Trust");
        SchemaPropertyMap.Add("CrcV1Trust",
            new Dictionary<string, Func<CirclesTrustData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "UserAddress", e => e.UserAddress },
                { "CanSendToAddress", e => e.CanSendToAddress },
                { "Limit", e => e.Limit }
            });

        EventDtoTableMap.Add<CirclesHubTransferData>("CrcV1HubTransfer");
        SchemaPropertyMap.Add("CrcV1HubTransfer", 
            new Dictionary<string, Func<CirclesHubTransferData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "FromAddress", e => e.FromAddress },
                { "ToAddress", e => e.ToAddress },
                { "Amount", e => (BigInteger)e.Amount }
            });

        EventDtoTableMap.Add<CirclesHubTransferData>("CrcV1HubTransfer");
        SchemaPropertyMap.Add("Erc20Transfer", 
            new Dictionary<string, Func<Erc20TransferData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "TokenAddress", e => e.TokenAddress },
                { "FromAddress", e => e.From },
                { "ToAddress", e => e.To },
                { "Amount", e => (BigInteger)e.Value }
            });
    }
}