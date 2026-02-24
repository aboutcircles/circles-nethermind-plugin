using System.Text.Json;

namespace Circles.Rpc.Host;

internal record SubscriptionRequest
{
    public string? Jsonrpc { get; init; }
    public string? Method { get; init; }
    public SubscriptionParams? Params { get; init; }
    public JsonElement? Id { get; init; }
}

internal record SubscriptionParams
{
    public string? Address { get; init; }
}
