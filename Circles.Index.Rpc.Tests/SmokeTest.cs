using System.Net;
using System.Text;
using System.Text.Json;

namespace Circles.Index.Rpc.Tests;

[TestFixture]
[NonParallelizable] // we reuse a single HttpClient / id counter
public class CirclesRpcSmokeTests
{
    private static readonly Uri Endpoint = new("http://localhost:8545");
    private static readonly HttpClient Client = new() { BaseAddress = Endpoint };
    private static int _id;

    /* ---------------------------------------------------------------
       Helper: send a JSON-RPC request and ensure the response is OK
       --------------------------------------------------------------- */
    private static async Task<JsonElement> CallAsync(string method, params object[] @params)
    {
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _id),
            method,
            @params
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("", content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"HTTP {(int)response.StatusCode} for {method}");

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.That(json.TryGetProperty("result", out _) ||
                    json.TryGetProperty("error", out _),
            $"Response for {method} has neither result nor error.");

        return json;
    }

    /* ---------- A handful of constants we reuse in multiple calls --- */
    private const string AnyAddress = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
    private const string AnyAddress2 = "0xe8fc7a2d0573e5164597b05f14fa9a7fca7b215c";
    private const string AnyCid = "Qmb2s3hjxXXcFqWvDDSPCd1fXXa9gcFJd8bzdZNNAvkq9W";
    private const string AnyTokenAddr = "0x42cedde51198d1773590311e2a340dc06b24cb37";

    /* -----------------------------------------------------------------
       One test per RPC method – name mirrors the method exactly
       ----------------------------------------------------------------- */
    [Test]
    public Task circles_getTotalBalance() =>
        CallAsync("circles_getTotalBalance", AnyAddress);

    [Test]
    public Task circlesV2_getTotalBalance() =>
        CallAsync("circlesV2_getTotalBalance", AnyAddress);

    [Test]
    public Task circles_getTrustRelations() =>
        CallAsync("circles_getTrustRelations", AnyAddress);

    [Test]
    public Task circles_getTokenBalances() =>
        CallAsync("circles_getTokenBalances", AnyAddress);

    [Test]
    public Task circles_getCommonTrust() =>
        CallAsync("circles_getCommonTrust", AnyAddress, AnyAddress2);

    [Test]
    public Task circles_query() =>
        CallAsync("circles_query", new
        {
            Namespace = "V_Crc", // light “ping” query
            Table = "Avatars",
            Columns = Array.Empty<string>(),
            Filter = Array.Empty<object>(),
            Order = Array.Empty<object>()
        });

    [Test]
    public Task circles_events() =>
        CallAsync("circles_events", null, 39_000_000, 39_000_500);

    [Test]
    public Task circlesV2_findPath() =>
        CallAsync("circlesV2_findPath", new
        {
            Source = AnyAddress,
            Sink = AnyAddress2,
            TargetFlow = "1000000000000000000"
        });

    [Test]
    public Task circles_health() =>
        CallAsync("circles_health");

    [Test]
    public Task circles_tables() =>
        CallAsync("circles_tables");

    [Test]
    public Task circles_getProfileCid() =>
        CallAsync("circles_getProfileCid", AnyAddress);

    [Test]
    public Task circles_getProfileCidBatch() =>
        CallAsync("circles_getProfileCidBatch", AnyAddress, AnyAddress2);

    [Test]
    public Task circles_getBalanceBreakdown() =>
        CallAsync("circles_getBalanceBreakdown", AnyAddress);

    [Test]
    public Task circles_getAvatarInfo() =>
        CallAsync("circles_getAvatarInfo", AnyAddress);

    [Test]
    public Task circles_getAvatarInfoBatch() =>
        CallAsync("circles_getAvatarInfoBatch", AnyAddress);

    [Test]
    public Task circles_getProfileByCid() =>
        CallAsync("circles_getProfileByCid", AnyCid);

    [Test]
    public Task circles_getProfileByCidBatch() =>
        CallAsync("circles_getProfileByCidBatch", AnyCid, null);

    [Test]
    public Task circles_getProfileByAddress() =>
        CallAsync("circles_getProfileByAddress", AnyAddress);

    [Test]
    public Task circles_getProfileByAddressBatch() =>
        CallAsync("circles_getProfileByAddressBatch", AnyAddress, AnyAddress2, null);

    [Test]
    public Task circles_getTokenInfo() =>
        CallAsync("circles_getTokenInfo", AnyTokenAddr);

    [Test]
    public Task circles_getTokenInfoBatch() =>
        CallAsync("circles_getTokenInfoBatch", AnyTokenAddr);

    [Test]
    public Task circles_getNetworkSnapshot() =>
        CallAsync("circles_getNetworkSnapshot");

    [Test]
    public Task circles_searchProfiles() =>
        CallAsync("circles_searchProfiles", "Circles");
}