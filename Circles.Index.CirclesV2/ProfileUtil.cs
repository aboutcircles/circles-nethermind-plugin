using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Circles.Index.CirclesV2;

public static class ProfileUtil
{
    // Cache for profile names: address -> (profile name or short form)
    private static readonly ConcurrentDictionary<string, string> ProfileNameCache = new();

    public static (string, string) GetShortForm(string fromAddress, string toAddress)
    {
        // special-case burn or mint
        if (toAddress == "0x0000000000000000000000000000000000000000")
            return (ResolveProfileNameOrDefault(fromAddress), "Burn");

        if (fromAddress == "0x0000000000000000000000000000000000000000")
            return ("Mint", ResolveProfileNameOrDefault(toAddress));

        return (ResolveProfileNameOrDefault(fromAddress), ResolveProfileNameOrDefault(toAddress));
    }

    public static string ResolveProfileNameOrDefault(string address)
    {
        if (ProfileNameCache.TryGetValue(address, out var cached))
            return cached;

        string fallbackShort = address.Length >= 12
            ? address[..8] + "..." + address[^4..]
            : address;

        try
        {
            using var httpClient = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://rpc.aboutcircles.com/profiles/search?address={address}");
            var response = httpClient.Send(request);
            var content = response.Content.ReadAsStringAsync().Result;
            var jsonArray = JsonSerializer.Deserialize<JsonArray>(content);

            if (jsonArray?.Count > 0)
            {
                string? name = jsonArray[0]?["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    ProfileNameCache[address] = name;
                    return name;
                }
            }
        }
        catch
        {
            // swallow
        }

        ProfileNameCache[address] = fallbackShort;
        return fallbackShort;
    }
}