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

    // The NOTIFY payload from the indexer uses camelCase property names; match them
    // case-insensitively so the PascalCase BlockRangePayload record binds correctly.
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public CirclesSubscriptionService(
        ILogger<CirclesSubscriptionService> logger,
        Settings settings,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _settings = settings;
        _scopeFactory = scopeFactory;
    }

    public string Subscribe(WebSocket webSocket, string? filterAddress, SemaphoreSlim? externalSendLock = null)
    {
        var subscriptionId = Guid.NewGuid().ToString("N");
        var subscriber = new WebSocketSubscriber(webSocket, filterAddress?.ToLowerInvariant(), _logger, externalSendLock);
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

        // Listen on the same channel the indexer notifies (Settings.PgNotifyChannel,
        // default "circles_index_events"). Read it from settings rather than hardcoding so
        // this listener stays consistent with the indexer's pg_notify and the cache service's
        // NotificationListenerService, all of which derive the channel from the same setting.
        // Channel names cannot be parameterized in LISTEN, and the value is a trusted
        // env/default (not user input), so interpolation is safe here.
        await using var command = new NpgsqlCommand($"LISTEN {_settings.PgNotifyChannel}", connection);
        await command.ExecuteNonQueryAsync(stoppingToken);

        _logger.LogInformation("Listening on {Channel} channel", _settings.PgNotifyChannel);

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
            // The indexer serializes the payload with camelCase keys
            // ({"fromBlock":N,"toBlock":M,"timestamp":T} — see StateMachine.NotifyViaPostgres),
            // so deserialize case-insensitively. Without this, the PascalCase record properties
            // never bind and every range parses as 0-0, so GetEvents finds nothing and no events
            // are ever broadcast.
            blockRange = JsonSerializer.Deserialize<BlockRangePayload>(payload, PayloadJsonOptions);
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
        private readonly SemaphoreSlim _sendLock;
        private readonly bool _ownsLock;
        private readonly string? _filterAddress;

        public WebSocketSubscriber(WebSocket webSocket, string? filterAddress, ILogger logger, SemaphoreSlim? externalSendLock = null)
        {
            _webSocket = webSocket;
            _filterAddress = string.IsNullOrWhiteSpace(filterAddress) ? null : filterAddress;
            _logger = logger;
            _ownsLock = externalSendLock == null;
            _sendLock = externalSendLock ?? new SemaphoreSlim(1, 1);
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
            if (_ownsLock)
                _sendLock.Dispose();
        }
    }
}
