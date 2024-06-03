using Circles.Index.Common;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Subscribe;

namespace Circles.Index.Rpc;

public class NotifyEventArgs(CirclesEvent[] events) : EventArgs
{
    public CirclesEvent[] Events { get; } = events;
}

public class CirclesSubscription : Subscription
{
    public override string Type => "circles";

    private readonly CirclesSubscriptionParams _param;

    public static long SubscriberCount => _subscriberCount;
    private static long _subscriberCount;

    public CirclesSubscription(IJsonRpcDuplexClient jsonRpcDuplexClient, CirclesSubscriptionParams param) : base(
        jsonRpcDuplexClient)
    {
        Notification += OnNotification;
        _param = param;

        Interlocked.Increment(ref _subscriberCount);
    }

    public static event EventHandler<NotifyEventArgs>? Notification;

    public static void Notify(Context context, Range<long> importedRange)
    {
        if (_subscriberCount == 0)
        {
            return;
        }

        var queryEvents = new QueryEvents(context);
        var events = queryEvents.CirclesEvents(null, importedRange.Min, importedRange.Max);

        Notification?.Invoke(null, new NotifyEventArgs(events));
    }

    private void OnNotification(object? sender, NotifyEventArgs e)
    {
        ScheduleAction(async () =>
        {
            CirclesEvent[] events;

            if (_param.Address != null)
            {
                var filterAddress = _param.Address!.ToString(true, false);
                events = FilterForAffectedAddress(e, filterAddress);
            }
            else
            {
                events = e.Events;
            }

            JsonRpcResult result = CreateSubscriptionMessage(events);
            await JsonRpcDuplexClient.SendJsonRpcResult(result);
        });
    }

    private CirclesEvent[] FilterForAffectedAddress(NotifyEventArgs e, string filterAddress)
    {
        var filteredForAddress = new List<CirclesEvent>();
        var addressesInEvent = new HashSet<string>();

        foreach (var circlesEvent in e.Events)
        {
            addressesInEvent.Clear();

            foreach (var circlesEventValue in circlesEvent.Values)
            {
                if (!QueryEvents.AddressColumns.Contains(circlesEventValue.Key))
                {
                    continue;
                }

                if (circlesEventValue.Value is string address)
                {
                    addressesInEvent.Add(address);
                }
            }

            if (addressesInEvent.Contains(filterAddress))
            {
                filteredForAddress.Add(circlesEvent);
            }
        }

        return filteredForAddress.ToArray();
    }

    public override void Dispose()
    {
        base.Dispose();

        Interlocked.Decrement(ref _subscriberCount);
    }
}