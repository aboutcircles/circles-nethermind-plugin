using Circles.Index.Common;
using Nethermind.Core.Crypto;

namespace Circles.Index.V2;

public class DatabaseSchema : IDatabaseSchema
{
    public SchemaPropertyMap SchemaPropertyMap { get; } = new();

    public EventDtoTableMap EventDtoTableMap { get; } = new();

    public IDictionary<string, EventSchema> Tables { get; } = new Dictionary<string, EventSchema>
    {
        {
            "CrcV2ConvertInflation",
            new EventSchema("CrcV2ConvertInflation", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("InflationValue", ValueTypes.BigInt, false),
                new("DemurrageValue", ValueTypes.BigInt, false),
                new("Day", ValueTypes.BigInt, false)
            ])
        },
        {
            "CrcV2InviteHuman",
            new EventSchema("CrcV2InviteHuman", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("InviterAddress", ValueTypes.Address, true),
                new("InviteeAddress", ValueTypes.Address, true)
            ])
        },
        {
            "CrcV2PersonalMint",
            new EventSchema("CrcV2PersonalMint", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("HumanAddress", ValueTypes.Address, true),
                new("Amount", ValueTypes.BigInt, false),
                new("StartPeriod", ValueTypes.BigInt, false),
                new("EndPeriod", ValueTypes.BigInt, false)
            ])
        },
        {
            "CrcV2RegisterGroup",
            new EventSchema("CrcV2RegisterGroup", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("GroupAddress", ValueTypes.Address, true),
                new("MintPolicy", ValueTypes.Address, true),
                new("Treasury", ValueTypes.Address, true),
                new("GroupName", ValueTypes.String, true),
                new("GroupSymbol", ValueTypes.String, true)
            ])
        },
        {
            "CrcV2RegisterHuman",
            new EventSchema("CrcV2RegisterHuman", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("HumanAddress", ValueTypes.Address, true)
            ])
        },
        {
            "CrcV2RegisterOrganization",
            new EventSchema("CrcV2RegisterOrganization", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("OrganizationAddress", ValueTypes.Address, true),
                new("OrganizationName", ValueTypes.String, true)
            ])
        },
        {
            "CrcV2Stopped",
            new EventSchema("CrcV2Stopped", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("Address", ValueTypes.Address, true)
            ])
        },
        {
            "CrcV2Trust",
            new EventSchema("CrcV2Trust", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("TrusterAddress", ValueTypes.Address, true),
                new("TrusteeAddress", ValueTypes.Address, true),
                new("ExpiryTime", ValueTypes.BigInt, false)
            ])
        },


        // Existing:
        {
            "Erc20Transfer",
            new EventSchema("Erc20Transfer", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("TokenAddress", ValueTypes.Address, true),
                new("FromAddress", ValueTypes.Address, true),
                new("ToAddress", ValueTypes.Address, true),
                new("Amount", ValueTypes.BigInt, false)
            ])
        },
        {
            "Erc1155ApprovalForAll",
            new EventSchema("Erc1155ApprovalForAll", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("Owner", ValueTypes.Address, true),
                new("Operator", ValueTypes.Address, true),
                new("Approved", ValueTypes.Boolean, true)
            ])
        },
        {
            "Erc1155TransferBatch",
            new EventSchema("Erc1155TransferBatch", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("BatchIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("OperatorAddress", ValueTypes.Address, true),
                new("FromAddress", ValueTypes.Address, true),
                new("ToAddress", ValueTypes.Address, true),
                new("TokenId", ValueTypes.BigInt, true),
                new("Value", ValueTypes.BigInt, false)
            ])
        },
        {
            "Erc1155TransferSingle",
            new EventSchema("Erc1155TransferSingle", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("OperatorAddress", ValueTypes.Address, true),
                new("FromAddress", ValueTypes.Address, true),
                new("ToAddress", ValueTypes.Address, true),
                new("TokenId", ValueTypes.BigInt, true),
                new("Value", ValueTypes.BigInt, false)
            ])
        },
        {
            "Erc1155Uri",
            new EventSchema("Erc1155Uri", new Hash256(new byte[32]),
            [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("TransactionIndex", ValueTypes.Int, true),
                new("LogIndex", ValueTypes.Int, true),
                new("TransactionHash", ValueTypes.String, true),
                new("TokenId", ValueTypes.BigInt, true),
                new("Uri", ValueTypes.BigInt, false)
            ])
        }
    };

    public DatabaseSchema()
    {
        EventDtoTableMap.Add<CrcV2ConvertInflationData>("CrcV2ConvertInflation");
        SchemaPropertyMap.Add("CrcV2ConvertInflation",
            new Dictionary<string, Func<CrcV2ConvertInflationData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "InflationValue", e => e.InflationValue },
                { "DemurrageValue", e => e.DemurrageValue },
                { "Day", e => e.Day }
            });

        EventDtoTableMap.Add<CrcV2InviteHumanData>("CrcV2InviteHuman");
        SchemaPropertyMap.Add("CrcV2InviteHuman",
            new Dictionary<string, Func<CrcV2InviteHumanData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "InviterAddress", e => e.InviterAddress },
                { "InviteeAddress", e => e.InviteeAddress }
            });

        EventDtoTableMap.Add<CrcV2PersonalMintData>("CrcV2PersonalMint");
        SchemaPropertyMap.Add("CrcV2PersonalMint",
            new Dictionary<string, Func<CrcV2PersonalMintData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "HumanAddress", e => e.HumanAddress },
                { "Amount", e => e.Amount },
                { "StartPeriod", e => e.StartPeriod },
                { "EndPeriod", e => e.EndPeriod }
            });

        EventDtoTableMap.Add<CrcV2RegisterGroupData>("CrcV2RegisterGroup");
        SchemaPropertyMap.Add("CrcV2RegisterGroup",
            new Dictionary<string, Func<CrcV2RegisterGroupData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "GroupAddress", e => e.GroupAddress },
                { "MintPolicy", e => e.MintPolicy },
                { "Treasury", e => e.Treasury },
                { "GroupName", e => e.GroupName },
                { "GroupSymbol", e => e.GroupSymbol }
            });

        EventDtoTableMap.Add<CrcV2RegisterHumanData>("CrcV2RegisterHuman");
        SchemaPropertyMap.Add("CrcV2RegisterHuman",
            new Dictionary<string, Func<CrcV2RegisterHumanData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "HumanAddress", e => e.HumanAddress }
            });

        EventDtoTableMap.Add<CrcV2RegisterOrganizationData>("CrcV2RegisterOrganization");
        SchemaPropertyMap.Add("CrcV2RegisterOrganization",
            new Dictionary<string, Func<CrcV2RegisterOrganizationData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "OrganizationAddress", e => e.OrganizationAddress },
                { "OrganizationName", e => e.OrganizationName }
            });

        EventDtoTableMap.Add<CrcV2StoppedData>("CrcV2Stopped");
        SchemaPropertyMap.Add("CrcV2Stopped",
            new Dictionary<string, Func<CrcV2StoppedData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "Address", e => e.Address }
            });

        EventDtoTableMap.Add<CrcV2TrustData>("CrcV2Trust");
        SchemaPropertyMap.Add("CrcV2Trust",
            new Dictionary<string, Func<CrcV2TrustData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "TrusterAddress", e => e.TrusterAddress },
                { "TrusteeAddress", e => e.TrusteeAddress },
                { "ExpiryTime", e => e.ExpiryTime }
            });

        EventDtoTableMap.Add<Erc1155ApprovalForAllData>("Erc1155ApprovalForAll");
        SchemaPropertyMap.Add("Erc1155ApprovalForAll",
            new Dictionary<string, Func<Erc1155ApprovalForAllData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "Owner", e => e.Owner },
                { "Operator", e => e.Operator },
                { "Approved", e => e.Approved }
            });

        EventDtoTableMap.Add<Erc1155TransferSingleData>("Erc1155TransferSingle");
        SchemaPropertyMap.Add("Erc1155TransferSingle",
            new Dictionary<string, Func<Erc1155TransferSingleData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "OperatorAddress", e => e.OperatorAddress },
                { "FromAddress", e => e.FromAddress },
                { "ToAddress", e => e.ToAddress },
                { "TokenId", e => e.TokenId },
                { "Value", e => e.Value }
            });

        EventDtoTableMap.Add<Erc1155TransferBatchData>("Erc1155TransferBatch");
        SchemaPropertyMap.Add("Erc1155TransferBatch",
            new Dictionary<string, Func<Erc1155TransferBatchData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "OperatorAddress", e => e.OperatorAddress },
                { "FromAddress", e => e.FromAddress },
                { "ToAddress", e => e.ToAddress },
                { "TokenId", e => e.TokenId },
                { "Value", e => e.Value }
            });

        EventDtoTableMap.Add<Erc1155UriData>("Erc1155Uri");
        SchemaPropertyMap.Add("Erc1155Uri",
            new Dictionary<string, Func<Erc1155UriData, object?>>
            {
                { "BlockNumber", e => e.BlockNumber },
                { "Timestamp", e => e.Timestamp },
                { "TransactionIndex", e => e.TransactionIndex },
                { "LogIndex", e => e.LogIndex },
                { "TransactionHash", e => e.TransactionHash },
                { "TokenId", e => e.TokenId },
                { "Uri", e => e.Uri }
            });
    }
}