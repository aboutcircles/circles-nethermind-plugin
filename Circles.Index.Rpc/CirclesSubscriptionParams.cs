using System.Text.Json;
using Nethermind.Core;
using Nethermind.JsonRpc;

namespace Circles.Index.Rpc;

public class CirclesSubscriptionParams : IJsonRpcParam
{
    public Address? Address { get; private set; }

    public void ReadJson(JsonElement element, JsonSerializerOptions options)
    {
        JsonDocument? doc = null;
        try
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                doc = JsonDocument.Parse(element.GetString() ?? "{}");
                element = doc.RootElement;
            }

            if (element.TryGetProperty("address", out JsonElement addressElement))
            {
                Address = Address.TryParse(addressElement.GetString(), out Address? address) ? address : null;
            }
        }
        finally
        {
            doc?.Dispose();
        }
    }
}