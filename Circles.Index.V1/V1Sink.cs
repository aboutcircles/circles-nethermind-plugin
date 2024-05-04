using System.Numerics;
using Circles.Index.Common;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Index.V1;

public class PropertyMap
{
    public Dictionary<Tables, Dictionary<Columns, (NpgsqlDbType type, Func<IIndexEvent, object?> extractor)>> Map { get; } = new();
    
    public void Add<TEvent>(Tables table, Dictionary<Columns, (NpgsqlDbType type, Func<TEvent, object?> extractor)> map) 
        where TEvent : IIndexEvent
    {
        Map.Add(table, map.ToDictionary(o => o.Key, o => ((NpgsqlDbType, Func<IIndexEvent, object?>))(o.Value.type, e => o.Value.extractor((TEvent)e))));
    }
}

public class V1Sink : IEventSink
{
    private readonly string _connectionString;
    private readonly int _batchSize;
    private readonly PropertyMap _propertyMap = new();
    private readonly InsertBuffer<IIndexEvent> _insertBuffer = new ();

    private MeteredCaller<object?, Task> _flush;
    private MeteredCaller<IIndexEvent, Task> _addEvent;
    
    public V1Sink(string connectionString, int batchSize = 1)
    {
        _connectionString = connectionString;
        _batchSize = batchSize;
        
        _propertyMap.Add(Tables.CrcV1Signup, new Dictionary<Columns, (NpgsqlDbType, Func<CirclesSignupData, object?>)>
        {
            { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
            { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
            { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
            { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
            { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
            { Columns.CirclesAddress, (NpgsqlDbType.Text, e => e.CirclesAddress) },
            { Columns.TokenAddress, (NpgsqlDbType.Text, e => e.TokenAddress ?? string.Empty) }
        });
        
        _propertyMap.Add(Tables.CrcV1Trust, new Dictionary<Columns, (NpgsqlDbType, Func<CirclesTrustData, object?>)>
        {
            { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
            { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
            { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
            { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
            { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
            { Columns.UserAddress, (NpgsqlDbType.Text, e => e.UserAddress) },
            { Columns.CanSendToAddress, (NpgsqlDbType.Text, e => e.CanSendToAddress) },
            { Columns.Limit, (NpgsqlDbType.Bigint, e => e.Limit) }
        });
        
        _propertyMap.Add(Tables.CrcV1HubTransfer, new Dictionary<Columns, (NpgsqlDbType, Func<CirclesHubTransferData, object?>)>
        {
            { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
            { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
            { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
            { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
            { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
            { Columns.FromAddress, (NpgsqlDbType.Text, e => e.FromAddress) },
            { Columns.ToAddress, (NpgsqlDbType.Text, e => e.ToAddress) },
            { Columns.Amount, (NpgsqlDbType.Numeric, e => (BigInteger)e.Amount) }
        });
        
        _propertyMap.Add(Tables.Erc20Transfer, new Dictionary<Columns, (NpgsqlDbType, Func<Erc20TransferData, object?>)>
        {
            { Columns.BlockNumber, (NpgsqlDbType.Bigint, e => e.BlockNumber) },
            { Columns.Timestamp, (NpgsqlDbType.Bigint, e => e.Timestamp) },
            { Columns.TransactionIndex, (NpgsqlDbType.Bigint, e => e.TransactionIndex) },
            { Columns.LogIndex, (NpgsqlDbType.Bigint, e => e.LogIndex) },
            { Columns.TransactionHash, (NpgsqlDbType.Text, e => e.TransactionHash) },
            { Columns.TokenAddress, (NpgsqlDbType.Text, e => e.TokenAddress) },
            { Columns.FromAddress, (NpgsqlDbType.Text, e => e.From) },
            { Columns.ToAddress, (NpgsqlDbType.Text, e => e.To) },
            { Columns.Amount, (NpgsqlDbType.Numeric, e => (BigInteger)e.Value) }
        });
        
        _flush = new MeteredCaller<object?, Task>("V1Sink: Flush", _ => PerformFlush());
        _addEvent = new MeteredCaller<IIndexEvent, Task>("V1Sink: AddEvent", PerformAddEvent);
    }
    
    public async Task FlushEvents(Tables table, IEnumerable<IIndexEvent> events)
    {
        // Console.WriteLine($"Flushing {events.Count()} events to {table}");
        
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
    
    public async ValueTask DisposeAsync()
    {
        await _flush.Call(null);
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
    
    private async Task PerformFlush()
    {
        var events = _insertBuffer.TakeSnapshot();
        
        var signupEvents = events.OfType<CirclesSignupData>();
        var trustEvents = events.OfType<CirclesTrustData>();
        var hubTransferEvents = events.OfType<CirclesHubTransferData>();
        var erc20TransferEvents = events.OfType<Erc20TransferData>();

        await Task.WhenAll([
            FlushEvents(Tables.CrcV1Signup, signupEvents),
            FlushEvents(Tables.CrcV1Trust, trustEvents),
            FlushEvents(Tables.CrcV1HubTransfer, hubTransferEvents),
            FlushEvents(Tables.Erc20Transfer, erc20TransferEvents)
        ]);
    }
}