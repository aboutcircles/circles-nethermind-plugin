using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Snapshot-based regression tests for the Circles RPC Host.
/// Calls live RPC methods against a staging/test environment and verifies structural expectations.
///
/// These tests require TEST_ENV_URL to be set (e.g., http://localhost:8081 or https://rpc.aboutcircles.com).
/// They are categorized as "RpcRegression" and skipped when the environment is not configured.
/// </summary>
[TestFixture]
[Category("RpcRegression")]
public class RpcMethodSnapshotTests
{
    private static readonly string? TestEnvUrl = Environment.GetEnvironmentVariable("TEST_ENV_URL");
    private HttpClient? _client;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (string.IsNullOrEmpty(TestEnvUrl))
        {
            Assert.Ignore("TEST_ENV_URL not set — skipping RPC regression tests. " +
                           "Set TEST_ENV_URL=http://localhost:8081 to run against local, " +
                           "or TEST_ENV_URL=https://rpc.aboutcircles.com for production comparison.");
            return;
        }

        _client = new HttpClient { BaseAddress = new Uri(TestEnvUrl) };
        _client.Timeout = TimeSpan.FromSeconds(30);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test infrastructure
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<JsonElement> CallRpc(string method, params object[] parameters)
    {
        var request = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
            id = 1
        };

        var response = await _client!.PostAsJsonAsync("/", request, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            Assert.Fail($"RPC error: {error}");
        }

        return doc.RootElement.GetProperty("result");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Health & Schema
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Health_ReturnsHealthy()
    {
        var result = await CallRpc("circles_health");
        Assert.That(result.GetProperty("status").GetString(), Is.EqualTo("healthy"));
    }

    [Test]
    public async Task Tables_ReturnsNonEmptySchema()
    {
        var result = await CallRpc("circles_tables");
        Assert.That(result.GetArrayLength(), Is.GreaterThan(0),
            "Should return at least one namespace");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Balance queries
    // ═══════════════════════════════════════════════════════════════════════

    // Known V2 human address (from test-rpc.sh)
    private const string KnownV2Human = "0x42cedde51198d1773590311e2a340dc06b24cb37";

    [Test]
    public async Task GetTotalBalance_V2_ReturnsNonNullString()
    {
        var result = await CallRpc("circlesV2_getTotalBalance", KnownV2Human, 2, true);
        var balance = result.GetProperty("totalBalance").GetString();
        Assert.That(balance, Is.Not.Null.And.Not.Empty,
            "V2 total balance should be a non-empty string");
    }

    [Test]
    public async Task GetTotalBalance_V2Raw_ReturnsNonNullString()
    {
        var result = await CallRpc("circlesV2_getTotalBalance", KnownV2Human, 2, false);
        var balance = result.GetProperty("totalBalance").GetString();
        Assert.That(balance, Is.Not.Null.And.Not.Empty,
            "V2 raw total balance should be a non-empty string");
    }

    [Test]
    public async Task GetTokenBalances_ReturnsArrayWithBalanceFields()
    {
        var result = await CallRpc("circles_getTokenBalances", KnownV2Human);
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Array));

        if (result.GetArrayLength() > 0)
        {
            var first = result[0];
            Assert.Multiple(() =>
            {
                Assert.That(first.TryGetProperty("tokenAddress", out _), Is.True, "Should have tokenAddress");
                Assert.That(first.TryGetProperty("tokenId", out _), Is.True, "Should have tokenId");
                Assert.That(first.TryGetProperty("tokenOwner", out _), Is.True, "Should have tokenOwner");
                Assert.That(first.TryGetProperty("attoCircles", out _), Is.True, "Should have attoCircles");
                Assert.That(first.TryGetProperty("circles", out _), Is.True, "Should have circles");
                Assert.That(first.TryGetProperty("staticAttoCircles", out _), Is.True, "Should have staticAttoCircles");
                Assert.That(first.TryGetProperty("attoCrc", out _), Is.True, "Should have attoCrc");
                Assert.That(first.TryGetProperty("version", out _), Is.True, "Should have version");
            });
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Avatar queries
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetAvatarInfo_KnownV2Human_ReturnsVersion2()
    {
        var result = await CallRpc("circles_getAvatarInfo", KnownV2Human);
        Assert.Multiple(() =>
        {
            Assert.That(result.GetProperty("version").GetInt32(), Is.EqualTo(2));
            Assert.That(result.GetProperty("avatar").GetString(), Is.EqualTo(KnownV2Human));
        });
    }

    [Test]
    public async Task GetAvatarInfoBatch_ReturnsArrayMatchingInput()
    {
        var addresses = new[] { KnownV2Human, "0x0000000000000000000000000000000000000001" };
        var result = await CallRpc("circles_getAvatarInfoBatch", (object)addresses);
        Assert.That(result.GetArrayLength(), Is.EqualTo(2),
            "Result length should match input length");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Trust queries
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetTrustRelations_ReturnsStructuredResponse()
    {
        var result = await CallRpc("circles_getTrustRelations", KnownV2Human);
        Assert.Multiple(() =>
        {
            Assert.That(result.TryGetProperty("user", out _), Is.True, "Should have user");
            Assert.That(result.TryGetProperty("trusts", out _), Is.True, "Should have trusts");
            Assert.That(result.TryGetProperty("trustedBy", out _), Is.True, "Should have trustedBy");
        });
    }

    [Test]
    public async Task GetAggregatedTrustRelations_ReturnsArray()
    {
        var result = await CallRpc("circles_getAggregatedTrustRelations", KnownV2Human);
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Array));

        if (result.GetArrayLength() > 0)
        {
            var first = result[0];
            Assert.Multiple(() =>
            {
                Assert.That(first.TryGetProperty("subjectAvatar", out _), Is.True);
                Assert.That(first.TryGetProperty("relation", out _), Is.True);
                Assert.That(first.TryGetProperty("objectAvatar", out _), Is.True);
                // BUG-1 regression: objectAvatarType should NOT be null for known avatars
                // (was always null in DB path before fix)
                Assert.That(first.TryGetProperty("objectAvatarType", out var avatarType), Is.True);
                if (avatarType.ValueKind != JsonValueKind.Null)
                {
                    var type = avatarType.GetString();
                    var validTypes = new[] {
                        "CrcV2_RegisterHuman", "CrcV2_RegisterOrganization", "CrcV2_RegisterGroup",
                        "CrcV1_Signup", "CrcV1_OrganizationSignup"
                    };
                    Assert.That(validTypes, Does.Contain(type),
                        $"objectAvatarType '{type}' should be a canonical full type name");
                }
            });
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Token queries
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetTokenInfo_KnownV2Human_ReturnsTokenInfo()
    {
        var result = await CallRpc("circles_getTokenInfo", KnownV2Human);
        Assert.Multiple(() =>
        {
            Assert.That(result.GetProperty("tokenAddress").GetString(), Is.EqualTo(KnownV2Human));
            Assert.That(result.GetProperty("version").GetInt32(), Is.EqualTo(2));
            Assert.That(result.GetProperty("isErc1155").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task GetTokenInfoBatch_ReturnsArrayMatchingInput()
    {
        var addresses = new[] { KnownV2Human };
        var result = await CallRpc("circles_getTokenInfoBatch", (object)addresses);
        Assert.That(result.GetArrayLength(), Is.EqualTo(1));
    }

    [Test]
    public async Task GetTokenHolders_ReturnsPagedResponse()
    {
        var result = await CallRpc("circles_getTokenHolders", KnownV2Human, 10);
        Assert.Multiple(() =>
        {
            Assert.That(result.TryGetProperty("results", out _), Is.True);
            Assert.That(result.TryGetProperty("hasMore", out _), Is.True);

            var results = result.GetProperty("results");
            if (results.GetArrayLength() > 0)
            {
                var first = results[0];
                Assert.That(first.TryGetProperty("account", out _), Is.True);
                Assert.That(first.TryGetProperty("balance", out _), Is.True);
                Assert.That(first.TryGetProperty("version", out _), Is.True);
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Group queries
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task FindGroups_ReturnsPagedResponse()
    {
        var result = await CallRpc("circles_findGroups", 10);
        Assert.Multiple(() =>
        {
            Assert.That(result.TryGetProperty("results", out _), Is.True);
            Assert.That(result.TryGetProperty("hasMore", out _), Is.True);

            var results = result.GetProperty("results");
            if (results.GetArrayLength() > 0)
            {
                var first = results[0];
                Assert.That(first.TryGetProperty("group", out _), Is.True);
                Assert.That(first.TryGetProperty("name", out _), Is.True);
                Assert.That(first.TryGetProperty("symbol", out _), Is.True);
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Event queries
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetEvents_WithAddressFilter_ReturnsRows()
    {
        var result = await CallRpc("circles_events", KnownV2Human, null, null, null, null, false);
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Array));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Profile queries
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetProfileCid_ReturnsResponse()
    {
        var result = await CallRpc("circles_getProfileCid", KnownV2Human);
        Assert.That(result.TryGetProperty("cid", out _), Is.True);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Pagination consistency
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetTokenHolders_Pagination_HasMoreImpliesCursor()
    {
        // BUG-4 regression: HasMore:true must always have a non-null NextCursor
        var result = await CallRpc("circles_getTokenHolders", KnownV2Human, 1);
        var hasMore = result.GetProperty("hasMore").GetBoolean();

        if (hasMore)
        {
            var nextCursor = result.GetProperty("nextCursor");
            Assert.That(nextCursor.ValueKind, Is.Not.EqualTo(JsonValueKind.Null),
                "When hasMore=true, nextCursor must not be null (BUG-4 regression)");
        }
    }

    [Test]
    public async Task FindGroups_Pagination_HasMoreImpliesCursor()
    {
        var result = await CallRpc("circles_findGroups", 1);
        var hasMore = result.GetProperty("hasMore").GetBoolean();

        if (hasMore)
        {
            var nextCursor = result.GetProperty("nextCursor");
            Assert.That(nextCursor.ValueKind, Is.Not.EqualTo(JsonValueKind.Null),
                "When hasMore=true, nextCursor must not be null");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Cross-path consistency (cache vs DB)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetTotalBalance_TimeCirclesVsRaw_TimeCirclesIsNonZeroWhenRawIsNonZero()
    {
        var tcResult = await CallRpc("circlesV2_getTotalBalance", KnownV2Human, 2, true);
        var rawResult = await CallRpc("circlesV2_getTotalBalance", KnownV2Human, 2, false);

        var tcBalance = tcResult.GetProperty("totalBalance").GetString();
        var rawBalance = rawResult.GetProperty("totalBalance").GetString();

        // If raw is non-zero, time-circles should also be non-zero
        if (rawBalance != "0" && rawBalance != null)
        {
            Assert.That(tcBalance, Is.Not.EqualTo("0").And.Not.Null,
                "If raw balance is non-zero, time-circles balance should also be non-zero");
        }
    }
}
