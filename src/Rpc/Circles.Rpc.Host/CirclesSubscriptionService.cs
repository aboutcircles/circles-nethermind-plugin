using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Background service that listens for PostgreSQL notifications and forwards fresh events
/// to connected WebSocket subscribers.
/// </summary>
public sealed class CirclesSubscriptionService : BackgroundService
{
    private readonly ILogger<CirclesSubscriptionService> _logger;
    private readonly Settings _settings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, WebSocketSubscriber> _subscribers = new();

    public CirclesSubscriptionService(
        ILogger<CirclesSubscriptionService> logger,
        Settings settings,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _settings = settings;
        _scopeFactory = scopeFactory;
    }

    public string Subscribe(WebSocket webSocket, string? filterAddress)
    {
        var subscriptionId = Guid.NewGuid().ToString("N");
        var subscriber = new WebSocketSubscriber(webSocket, filterAddress?.ToLowerInvariant(), _logger);
        _subscribers[subscriptionId] = subscriber;
        _logger.LogInformation(
            "Registered subscription {SubscriptionId} (filter: {Filter})",
            subscriptionId,
            filterAddress ?? "all");
        return subscriptionId;
    }

    public void Unsubscribe(string subscriptionId)
    {
        if (_subscribers.TryRemove(subscriptionId, out var subscriber))
        {
            subscriber.Dispose();
            _logger.LogInformation("Unregistered subscription {SubscriptionId}", subscriptionId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Circles subscription listener");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenForNotifications(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Subscription listener failed; retrying shortly");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("Stopping Circles subscription listener");
    }

    private async Task ListenForNotifications(CancellationToken stoppingToken)
    {
        await using var connection = new NpgsqlConnection(_settings.IndexReadonlyDbConnectionString);
        await connection.OpenAsync(stoppingToken);

        connection.Notification += (_, args) =>
        {
            _ = Task.Run(async () => await HandleNotificationAsync(args.Payload, stoppingToken), stoppingToken);
        };

        await using var command = new NpgsqlCommand("LISTEN circles_events", connection);
        await command.ExecuteNonQueryAsync(stoppingToken);

        _logger.LogInformation("Listening on circles_events channel");

        while (!stoppingToken.IsCancellationRequested)
        {
            await connection.WaitAsync(stoppingToken);
        }
    }

    private async Task HandleNotificationAsync(string payload, CancellationToken cancellationToken)
    {
        BlockRangePayload? blockRange;
        try
        {
            blockRange = JsonSerializer.Deserialize<BlockRangePayload>(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse notification payload: {Payload}", payload);
            return;
        }

        if (blockRange is null)
        {
            return;
        }

        await NotifySubscribersAsync(blockRange.FromBlock, blockRange.ToBlock, cancellationToken);
    }

    private async Task NotifySubscribersAsync(long fromBlock, long toBlock, CancellationToken cancellationToken)
    {
        if (_subscribers.IsEmpty)
        {
            _logger.LogDebug("No subscribers to notify for blocks {From}-{To}", fromBlock, toBlock);
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var rpcModule = scope.ServiceProvider.GetRequiredService<CirclesRpcModule>();
            var eventsResponse = await rpcModule.GetEvents(
                address: null,
                fromBlock: fromBlock,
                toBlock: toBlock,
                eventTypes: null,
                filterPredicates: null,
                sortAscending: false);

            if (eventsResponse.Events.Length == 0)
            {
                _logger.LogDebug("No events generated for range {From}-{To}", fromBlock, toBlock);
                return;
            }

            var sendTasks = _subscribers.Values
                .Select(subscriber => subscriber.SendAsync(eventsResponse.Events, cancellationToken));

            await Task.WhenAll(sendTasks);

            _logger.LogInformation(
                "Broadcasted {EventCount} events to {SubscriberCount} subscribers for blocks {From}-{To}",
                eventsResponse.Events.Length,
                _subscribers.Count,
                fromBlock,
                toBlock);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast events for range {From}-{To}", fromBlock, toBlock);
        }
    }

    private sealed record BlockRangePayload(long FromBlock, long ToBlock, long Timestamp);

    private sealed class WebSocketSubscriber : IDisposable
    {
        private readonly WebSocket _webSocket;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly string? _filterAddress;

        public WebSocketSubscriber(WebSocket webSocket, string? filterAddress, ILogger logger)
        {
            _webSocket = webSocket;
            _filterAddress = string.IsNullOrWhiteSpace(filterAddress) ? null : filterAddress;
            _logger = logger;
        }

        public async Task SendAsync(object[] eventsPayload, CancellationToken cancellationToken)
        {
            if (_webSocket.State != WebSocketState.Open)
            {
                return;
            }

            var eventsToSend = _filterAddress == null
                ? eventsPayload
                : eventsPayload.Where(e => EventContainsAddress(e, _filterAddress)).ToArray();

            if (eventsToSend.Length == 0)
            {
                return;
            }

            var message = new
            {
                jsonrpc = "2.0",
                method = "circles_subscription",
                @params = new { result = eventsToSend }
            };

            var payload = JsonSerializer.SerializeToUtf8Bytes(message);

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send subscription payload");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private static bool EventContainsAddress(object eventPayload, string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                return true;
            }

            var json = JsonSerializer.Serialize(eventPayload);
            return json.Contains(address, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            _sendLock.Dispose();
        }
    }
}
