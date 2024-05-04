using Circles.Index.Common;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Index.V2;

public class PostgresSink : IEventSink
{
    private readonly string _connectionString;
    private readonly int _batchSize;
    private readonly PropertyMap _propertyMap = new();
    private readonly InsertBuffer<IIndexEvent> _insertBuffer = new();

    private readonly MeteredCaller<object?, Task> _flush;
    private readonly MeteredCaller<IIndexEvent, Task> _addEvent;

    public PostgresSink(string connectionString, int batchSize = 1)
    {
        _connectionString = connectionString;
        _batchSize = batchSize;


        // New
        _propertyMap.Add(Tables.CrcV2ConvertInflation,
            new Dictionary<Columns, (NpgsqlDbType, Func<CrcV2ConvertInflationData, object?>)>
            {
                { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
                { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
                { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
                { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
                { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
                { Columns.InflationValue, (NpgsqlDbType.Numeric, e => e.InflationValue) },
                { Columns.DemurrageValue, (NpgsqlDbType.Numeric, e => e.DemurrageValue) },
                { Columns.Day, (NpgsqlDbType.Numeric, e => e.Day) }
            });

        _propertyMap.Add(Tables.CrcV2InviteHuman,
            new Dictionary<Columns, (NpgsqlDbType, Func<CrcV2InviteHumanData, object?>)>
            {
                { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
                { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
                { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
                { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
                { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
                { Columns.InviterAddress, (NpgsqlDbType.Text, e => e.InviterAddress) },
                { Columns.InviteeAddress, (NpgsqlDbType.Text, e => e.InviteeAddress) }
            });

        _propertyMap.Add(Tables.CrcV2PersonalMint,
            new Dictionary<Columns, (NpgsqlDbType, Func<CrcV2PersonalMintData, object?>)>
            {
                { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
                { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
                { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
                { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
                { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
                { Columns.HumanAddress, (NpgsqlDbType.Text, e => e.HumanAddress) },
                { Columns.Amount, (NpgsqlDbType.Numeric, e => e.Amount) },
                { Columns.StartPeriod, (NpgsqlDbType.Numeric, e => e.StartPeriod) },
                { Columns.EndPeriod, (NpgsqlDbType.Numeric, e => e.EndPeriod) }
            });

        _propertyMap.Add(Tables.CrcV2RegisterGroup,
            new Dictionary<Columns, (NpgsqlDbType, Func<CrcV2RegisterGroupData, object?>)>
            {
                { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
                { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
                { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
                { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
                { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
                { Columns.GroupAddress, (NpgsqlDbType.Text, e => e.GroupAddress) },
                { Columns.MintPolicy, (NpgsqlDbType.Text, e => e.MintPolicy) },
                { Columns.Treasury, (NpgsqlDbType.Text, e => e.Treasury) },
                { Columns.GroupName, (NpgsqlDbType.Text, e => e.GroupName) },
                { Columns.GroupSymbol, (NpgsqlDbType.Text, e => e.GroupSymbol) }
            });

        _propertyMap.Add(Tables.CrcV2RegisterHuman,
            new Dictionary<Columns, (NpgsqlDbType, Func<CrcV2RegisterHumanData, object?>)>
            {
                { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
                { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
                { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
                { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
                { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
                { Columns.HumanAddress, (NpgsqlDbType.Text, e => e.HumanAddress) }
            });

        _propertyMap.Add(Tables.CrcV2RegisterOrganization,
            new Dictionary<Columns, (NpgsqlDbType, Func<CrcV2RegisterOrganizationData, object?>)>
            {
                { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
                { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
                { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
                { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
                { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
                { Columns.OrganizationAddress, (NpgsqlDbType.Text, e => e.OrganizationAddress) },
                { Columns.OrganizationName, (NpgsqlDbType.Text, e => e.OrganizationName) }
            });

        _propertyMap.Add(Tables.CrcV2Stopped,
            new Dictionary<Columns, (NpgsqlDbType, Func<CrcV2StoppedData, object?>)>
            {
                { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
                { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
                { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
                { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
                { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
                { Columns.Address, (NpgsqlDbType.Text, e => e.Address) }
            });

        _propertyMap.Add(Tables.CrcV2Trust,
            new Dictionary<Columns, (NpgsqlDbType, Func<CrcV2TrustData, object?>)>
            {
                { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
                { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
                { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
                { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
                { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
                { Columns.TrusterAddress, (NpgsqlDbType.Text, e => e.TrusterAddress) },
                { Columns.TrusteeAddress, (NpgsqlDbType.Text, e => e.TrusteeAddress) },
                { Columns.ExpiryTime, (NpgsqlDbType.Numeric, e => e.ExpiryTime) }
            });

        _propertyMap.Add(Tables.Erc1155ApprovalForAll,
            new Dictionary<Columns, (NpgsqlDbType, Func<Erc1155ApprovalForAllData, object?>)>
            {
                { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
                { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
                { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
                { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
                { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
                { Columns.Owner, (NpgsqlDbType.Text, e => e.Owner) },
                { Columns.Operator, (NpgsqlDbType.Text, e => e.Operator) },
                { Columns.Approved, (NpgsqlDbType.Boolean, e => e.Approved) }
            });

        _propertyMap.Add(Tables.Erc1155TransferSingle,
            new Dictionary<Columns, (NpgsqlDbType, Func<Erc1155TransferSingleData, object?>)>
            {
                { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
                { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
                { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
                { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
                { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
                { Columns.OperatorAddress, (NpgsqlDbType.Text, e => e.OperatorAddress) },
                { Columns.FromAddress, (NpgsqlDbType.Text, e => e.FromAddress) },
                { Columns.ToAddress, (NpgsqlDbType.Text, e => e.ToAddress) },
                { Columns.TokenId, (NpgsqlDbType.Numeric, e => e.TokenId) },
                { Columns.Value, (NpgsqlDbType.Numeric, e => e.Value) }
            });

        _propertyMap.Add(Tables.Erc1155TransferBatch,
            new Dictionary<Columns, (NpgsqlDbType, Func<Erc1155TransferBatchData, object?>)>
            {
                { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
                { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
                { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
                { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
                { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
                { Columns.OperatorAddress, (NpgsqlDbType.Text, e => e.OperatorAddress) },
                { Columns.FromAddress, (NpgsqlDbType.Text, e => e.FromAddress) },
                { Columns.ToAddress, (NpgsqlDbType.Text, e => e.ToAddress) },
                { Columns.TokenId, (NpgsqlDbType.Numeric, e => e.TokenId) },
                { Columns.Value, (NpgsqlDbType.Numeric, e => e.Value) }
            });

        _propertyMap.Add(Tables.Erc1155Uri,
            new Dictionary<Columns, (NpgsqlDbType, Func<Erc1155UriData, object?>)>
            {
                { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
                { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
                { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
                { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
                { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
                { Columns.TokenId, (NpgsqlDbType.Numeric, e => e.TokenId) },
                { Columns.Uri, (NpgsqlDbType.Text, e => e.Uri) }
            });

        _flush = new MeteredCaller<object?, Task>("V2Sink: Flush", async _ => await PerformFlush());
        _addEvent = new MeteredCaller<IIndexEvent, Task>("V2Sink: AddEvent", PerformAddEvent);
    }

    public Task AddEvent(IIndexEvent indexEvent)
    {
        return _addEvent.Call(indexEvent);
    }

    private async Task PerformAddEvent(IIndexEvent indexEvent)
    {
        _insertBuffer.Add(indexEvent);

        if (_insertBuffer.Length >= _batchSize)
        {
            await Flush();
        }
    }

    public Task Flush()
    {
        return _flush.Call(null);
    }

    private async Task FlushEvents(Tables table, IEnumerable<IIndexEvent> events)
    {
        await using var flushConnection = new NpgsqlConnection(_connectionString);
        await flushConnection.OpenAsync();

        await using var writer = await flushConnection.BeginBinaryImportAsync(
            $@"
                COPY {table.GetIdentifier()} (
                    {string.Join(", ", _propertyMap.Map[table].Keys.Select(o => o.GetIdentifier()))}
                ) FROM STDIN (FORMAT BINARY)"
        );

        foreach (var e in events)
        {
            await writer.StartRowAsync();
            foreach (var (_, extractor) in _propertyMap.Map[table])
            {
                await writer.WriteAsync(extractor.extractor(e), extractor.type);
            }
        }

        await writer.CompleteAsync();
    }

    private async Task PerformFlush()
    {
        var events = _insertBuffer.TakeSnapshot();

        var registerOrganizationData = events.OfType<CrcV2RegisterOrganizationData>();
        var registerGroupData = events.OfType<CrcV2RegisterGroupData>();
        var registerHumanData = events.OfType<CrcV2RegisterHumanData>();
        var personalMintData = events.OfType<CrcV2PersonalMintData>();
        var inviteHumanData = events.OfType<CrcV2InviteHumanData>();
        var convertInflationData = events.OfType<CrcV2ConvertInflationData>();
        var trustData = events.OfType<CrcV2TrustData>();
        var stoppedData = events.OfType<CrcV2StoppedData>();
        var approvalForAllData = events.OfType<Erc1155ApprovalForAllData>();
        var transferSingleData = events.OfType<Erc1155TransferSingleData>();
        var transferBatchData = events.OfType<Erc1155TransferBatchData>();
        var uriData = events.OfType<Erc1155UriData>();

        await Task.WhenAll([
            FlushEvents(Tables.CrcV2RegisterOrganization, registerOrganizationData),
            FlushEvents(Tables.CrcV2RegisterGroup, registerGroupData),
            FlushEvents(Tables.CrcV2RegisterHuman, registerHumanData),
            FlushEvents(Tables.CrcV2PersonalMint, personalMintData),
            FlushEvents(Tables.CrcV2InviteHuman, inviteHumanData),
            FlushEvents(Tables.CrcV2ConvertInflation, convertInflationData),
            FlushEvents(Tables.CrcV2Trust, trustData),
            FlushEvents(Tables.CrcV2Stopped, stoppedData),
            FlushEvents(Tables.Erc1155ApprovalForAll, approvalForAllData),
            FlushEvents(Tables.Erc1155TransferSingle, transferSingleData),
            FlushEvents(Tables.Erc1155TransferBatch, transferBatchData),
            FlushEvents(Tables.Erc1155Uri, uriData)
        ]);
    }

    public async ValueTask DisposeAsync()
    {
        await _flush.Call(null);
    }
}