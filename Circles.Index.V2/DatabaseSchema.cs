using Circles.Index.Common;

namespace Circles.Index.V2;

public class DatabaseSchema : IDatabaseSchema
{
    public SchemaPropertyMap SchemaPropertyMap { get; } = new();

    public EventDtoTableMap EventDtoTableMap { get; } = new();

    public IDictionary<Tables, TableSchema> Tables { get; } = new Dictionary<Tables, TableSchema>
    {
        {
            Common.Tables.CrcV2ConvertInflation,
            new TableSchema(Common.Tables.CrcV2ConvertInflation,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.InflationValue, ValueTypes.BigInt, false, false),
                new(Columns.DemurrageValue, ValueTypes.BigInt, false, false),
                new(Columns.Day, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Common.Tables.CrcV2InviteHuman,
            new TableSchema(Common.Tables.CrcV2InviteHuman,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.InviterAddress, ValueTypes.Address, false, true),
                new(Columns.InviteeAddress, ValueTypes.Address, false, true)
            ])
        },
        {
            Common.Tables.CrcV2PersonalMint,
            new TableSchema(Common.Tables.CrcV2PersonalMint,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.HumanAddress, ValueTypes.Address, false, true),
                new(Columns.Amount, ValueTypes.BigInt, false, false),
                new(Columns.StartPeriod, ValueTypes.BigInt, false, false),
                new(Columns.EndPeriod, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Common.Tables.CrcV2RegisterGroup,
            new TableSchema(Common.Tables.CrcV2RegisterGroup,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.GroupAddress, ValueTypes.Address, false, true),
                new(Columns.MintPolicy, ValueTypes.Address, false, true),
                new(Columns.Treasury, ValueTypes.Address, false, true),
                new(Columns.GroupName, ValueTypes.String, false, true),
                new(Columns.GroupSymbol, ValueTypes.String, false, true)
            ])
        },
        {
            Common.Tables.CrcV2RegisterHuman,
            new TableSchema(Common.Tables.CrcV2RegisterHuman,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.HumanAddress, ValueTypes.Address, false, true)
            ])
        },
        {
            Common.Tables.CrcV2RegisterOrganization,
            new TableSchema(Common.Tables.CrcV2RegisterOrganization,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.OrganizationAddress, ValueTypes.Address, false, true),
                new(Columns.OrganizationName, ValueTypes.String, false, true)
            ])
        },
        {
            Common.Tables.CrcV2Stopped,
            new TableSchema(Common.Tables.CrcV2Stopped,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.Address, ValueTypes.Address, false, true)
            ])
        },
        {
            Common.Tables.CrcV2Trust,
            new TableSchema(Common.Tables.CrcV2Trust,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.TrusterAddress, ValueTypes.Address, false, true),
                new(Columns.TrusteeAddress, ValueTypes.Address, false, true),
                new(Columns.ExpiryTime, ValueTypes.BigInt, false, false)
            ])
        },


        // Existing:
        {
            Common.Tables.Erc20Transfer,
            new TableSchema(Common.Tables.Erc20Transfer,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.TokenAddress, ValueTypes.Address, false, true),
                new(Columns.FromAddress, ValueTypes.Address, false, true),
                new(Columns.ToAddress, ValueTypes.Address, false, true),
                new(Columns.Amount, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Common.Tables.Erc1155ApprovalForAll,
            new TableSchema(Common.Tables.Erc1155ApprovalForAll,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.Owner, ValueTypes.Address, false, true),
                new(Columns.Operator, ValueTypes.Address, false, true),
                new(Columns.Approved, ValueTypes.Boolean, false, true)
            ])
        },
        {
            Common.Tables.Erc1155TransferBatch,
            new TableSchema(Common.Tables.Erc1155TransferBatch,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.BatchIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.OperatorAddress, ValueTypes.Address, false, true),
                new(Columns.FromAddress, ValueTypes.Address, false, true),
                new(Columns.ToAddress, ValueTypes.Address, false, true),
                new(Columns.TokenId, ValueTypes.BigInt, false, true),
                new(Columns.Value, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Common.Tables.Erc1155TransferSingle,
            new TableSchema(Common.Tables.Erc1155TransferSingle,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.OperatorAddress, ValueTypes.Address, false, true),
                new(Columns.FromAddress, ValueTypes.Address, false, true),
                new(Columns.ToAddress, ValueTypes.Address, false, true),
                new(Columns.TokenId, ValueTypes.BigInt, false, true),
                new(Columns.Value, ValueTypes.BigInt, false, false)
            ])
        },
        {
            Common.Tables.Erc1155Uri,
            new TableSchema(Common.Tables.Erc1155Uri,
            [
                new(Columns.BlockNumber, ValueTypes.Int, true, true),
                new(Columns.Timestamp, ValueTypes.Int, false, true),
                new(Columns.TransactionIndex, ValueTypes.Int, true, true),
                new(Columns.LogIndex, ValueTypes.Int, true, true),
                new(Columns.TransactionHash, ValueTypes.String, false, true),
                new(Columns.TokenId, ValueTypes.BigInt, false, true),
                new(Columns.Uri, ValueTypes.BigInt, false, false)
            ])
        }
    };

    public DatabaseSchema()
    {
        EventDtoTableMap.Add<CrcV2ConvertInflationData>(Common.Tables.CrcV2ConvertInflation);
        SchemaPropertyMap.Add(Common.Tables.CrcV2ConvertInflation,
            new Dictionary<Columns, Func<CrcV2ConvertInflationData, object?>>
            {
                { Columns.BlockNumber, e => e.BlockNumber },
                { Columns.Timestamp, e => e.Timestamp },
                { Columns.TransactionIndex, e => e.TransactionIndex },
                { Columns.LogIndex, e => e.LogIndex },
                { Columns.TransactionHash, e => e.TransactionHash },
                { Columns.InflationValue, e => e.InflationValue },
                { Columns.DemurrageValue, e => e.DemurrageValue },
                { Columns.Day, e => e.Day }
            });

        EventDtoTableMap.Add<CrcV2InviteHumanData>(Common.Tables.CrcV2InviteHuman);
        SchemaPropertyMap.Add(Common.Tables.CrcV2InviteHuman,
            new Dictionary<Columns, Func<CrcV2InviteHumanData, object?>>
            {
                { Columns.BlockNumber, e => e.BlockNumber },
                { Columns.Timestamp, e => e.Timestamp },
                { Columns.TransactionIndex, e => e.TransactionIndex },
                { Columns.LogIndex, e => e.LogIndex },
                { Columns.TransactionHash, e => e.TransactionHash },
                { Columns.InviterAddress, e => e.InviterAddress },
                { Columns.InviteeAddress, e => e.InviteeAddress }
            });

        EventDtoTableMap.Add<CrcV2PersonalMintData>(Common.Tables.CrcV2PersonalMint);
        SchemaPropertyMap.Add(Common.Tables.CrcV2PersonalMint,
            new Dictionary<Columns, Func<CrcV2PersonalMintData, object?>>
            {
                { Columns.BlockNumber, e => e.BlockNumber },
                { Columns.Timestamp, e => e.Timestamp },
                { Columns.TransactionIndex, e => e.TransactionIndex },
                { Columns.LogIndex, e => e.LogIndex },
                { Columns.TransactionHash, e => e.TransactionHash },
                { Columns.HumanAddress, e => e.HumanAddress },
                { Columns.Amount, e => e.Amount },
                { Columns.StartPeriod, e => e.StartPeriod },
                { Columns.EndPeriod, e => e.EndPeriod }
            });

        EventDtoTableMap.Add<CrcV2RegisterGroupData>(Common.Tables.CrcV2RegisterGroup);
        SchemaPropertyMap.Add(Common.Tables.CrcV2RegisterGroup,
            new Dictionary<Columns, Func<CrcV2RegisterGroupData, object?>>
            {
                { Columns.BlockNumber, e => e.BlockNumber },
                { Columns.Timestamp, e => e.Timestamp },
                { Columns.TransactionIndex, e => e.TransactionIndex },
                { Columns.LogIndex, e => e.LogIndex },
                { Columns.TransactionHash, e => e.TransactionHash },
                { Columns.GroupAddress, e => e.GroupAddress },
                { Columns.MintPolicy, e => e.MintPolicy },
                { Columns.Treasury, e => e.Treasury },
                { Columns.GroupName, e => e.GroupName },
                { Columns.GroupSymbol, e => e.GroupSymbol }
            });

        EventDtoTableMap.Add<CrcV2RegisterHumanData>(Common.Tables.CrcV2RegisterHuman);
        SchemaPropertyMap.Add(Common.Tables.CrcV2RegisterHuman,
            new Dictionary<Columns, Func<CrcV2RegisterHumanData, object?>>
            {
                { Columns.BlockNumber, e => e.BlockNumber },
                { Columns.Timestamp, e => e.Timestamp },
                { Columns.TransactionIndex, e => e.TransactionIndex },
                { Columns.LogIndex, e => e.LogIndex },
                { Columns.TransactionHash, e => e.TransactionHash },
                { Columns.HumanAddress, e => e.HumanAddress }
            });

        EventDtoTableMap.Add<CrcV2RegisterOrganizationData>(Common.Tables.CrcV2RegisterOrganization);
        SchemaPropertyMap.Add(Common.Tables.CrcV2RegisterOrganization,
            new Dictionary<Columns, Func<CrcV2RegisterOrganizationData, object?>>
            {
                { Columns.BlockNumber, e => e.BlockNumber },
                { Columns.Timestamp, e => e.Timestamp },
                { Columns.TransactionIndex, e => e.TransactionIndex },
                { Columns.LogIndex, e => e.LogIndex },
                { Columns.TransactionHash, e => e.TransactionHash },
                { Columns.OrganizationAddress, e => e.OrganizationAddress },
                { Columns.OrganizationName, e => e.OrganizationName }
            });

        EventDtoTableMap.Add<CrcV2StoppedData>(Common.Tables.CrcV2Stopped);
        SchemaPropertyMap.Add(Common.Tables.CrcV2Stopped,
            new Dictionary<Columns, Func<CrcV2StoppedData, object?>>
            {
                { Columns.BlockNumber, e => e.BlockNumber },
                { Columns.Timestamp, e => e.Timestamp },
                { Columns.TransactionIndex, e => e.TransactionIndex },
                { Columns.LogIndex, e => e.LogIndex },
                { Columns.TransactionHash, e => e.TransactionHash },
                { Columns.Address, e => e.Address }
            });

        EventDtoTableMap.Add<CrcV2TrustData>(Common.Tables.CrcV2Trust);
        SchemaPropertyMap.Add(Common.Tables.CrcV2Trust,
            new Dictionary<Columns, Func<CrcV2TrustData, object?>>
            {
                { Columns.BlockNumber, e => e.BlockNumber },
                { Columns.Timestamp, e => e.Timestamp },
                { Columns.TransactionIndex, e => e.TransactionIndex },
                { Columns.LogIndex, e => e.LogIndex },
                { Columns.TransactionHash, e => e.TransactionHash },
                { Columns.TrusterAddress, e => e.TrusterAddress },
                { Columns.TrusteeAddress, e => e.TrusteeAddress },
                { Columns.ExpiryTime, e => e.ExpiryTime }
            });

        EventDtoTableMap.Add<Erc1155ApprovalForAllData>(Common.Tables.Erc1155ApprovalForAll);
        SchemaPropertyMap.Add(Common.Tables.Erc1155ApprovalForAll,
            new Dictionary<Columns, Func<Erc1155ApprovalForAllData, object?>>
            {
                { Columns.BlockNumber, e => e.BlockNumber },
                { Columns.Timestamp, e => e.Timestamp },
                { Columns.TransactionIndex, e => e.TransactionIndex },
                { Columns.LogIndex, e => e.LogIndex },
                { Columns.TransactionHash, e => e.TransactionHash },
                { Columns.Owner, e => e.Owner },
                { Columns.Operator, e => e.Operator },
                { Columns.Approved, e => e.Approved }
            });

        EventDtoTableMap.Add<Erc1155TransferSingleData>(Common.Tables.Erc1155TransferSingle);
        SchemaPropertyMap.Add(Common.Tables.Erc1155TransferSingle,
            new Dictionary<Columns, Func<Erc1155TransferSingleData, object?>>
            {
                { Columns.BlockNumber, e => e.BlockNumber },
                { Columns.Timestamp, e => e.Timestamp },
                { Columns.TransactionIndex, e => e.TransactionIndex },
                { Columns.LogIndex, e => e.LogIndex },
                { Columns.TransactionHash, e => e.TransactionHash },
                { Columns.OperatorAddress, e => e.OperatorAddress },
                { Columns.FromAddress, e => e.FromAddress },
                { Columns.ToAddress, e => e.ToAddress },
                { Columns.TokenId, e => e.TokenId },
                { Columns.Value, e => e.Value }
            });

        EventDtoTableMap.Add<Erc1155TransferBatchData>(Common.Tables.Erc1155TransferBatch);
        SchemaPropertyMap.Add(Common.Tables.Erc1155TransferBatch,
            new Dictionary<Columns, Func<Erc1155TransferBatchData, object?>>
            {
                { Columns.BlockNumber, e => e.BlockNumber },
                { Columns.Timestamp, e => e.Timestamp },
                { Columns.TransactionIndex, e => e.TransactionIndex },
                { Columns.LogIndex, e => e.LogIndex },
                { Columns.TransactionHash, e => e.TransactionHash },
                { Columns.OperatorAddress, e => e.OperatorAddress },
                { Columns.FromAddress, e => e.FromAddress },
                { Columns.ToAddress, e => e.ToAddress },
                { Columns.TokenId, e => e.TokenId },
                { Columns.Value, e => e.Value }
            });

        EventDtoTableMap.Add<Erc1155UriData>(Common.Tables.Erc1155Uri);
        SchemaPropertyMap.Add(Common.Tables.Erc1155Uri,
            new Dictionary<Columns, Func<Erc1155UriData, object?>>
            {
                { Columns.BlockNumber, e => e.BlockNumber },
                { Columns.Timestamp, e => e.Timestamp },
                { Columns.TransactionIndex, e => e.TransactionIndex },
                { Columns.LogIndex, e => e.LogIndex },
                { Columns.TransactionHash, e => e.TransactionHash },
                { Columns.TokenId, e => e.TokenId },
                { Columns.Uri, e => e.Uri }
            });
    }
}