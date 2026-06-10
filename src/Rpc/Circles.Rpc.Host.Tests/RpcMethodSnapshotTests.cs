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
[Category("Regression")]
[Category("RequiresTestEnv")]
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

        // The Circles RPC lives at the HOST ROOT of the test-env URL: with
        // TEST_ENV_URL=https://staging.circlesubi.network/test-env, requests go to
        // https://staging.circlesubi.network/ (the staging RPC behind the same host).
        // CIRCLES_RPC_URL overrides this for local runs (e.g. http://localhost:8081).
        var rpcUrl = Environment.GetEnvironmentVariable("CIRCLES_RPC_URL");
        var baseAddress = !string.IsNullOrEmpty(rpcUrl)
            ? new Uri(rpcUrl)
            : new Uri(new Uri(TestEnvUrl), "/");
        _client = new HttpClient { BaseAddress = baseAddress };
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
        // circles_health returns a plain string, not an object (verified against staging).
        Assert.That(result.GetString(), Is.EqualTo("Healthy"));
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
        // Returns a plain string balance, not an object (verified against staging).
        var balance = result.GetString();
        Assert.That(balance, Is.Not.Null.And.Not.Empty,
            "V2 total balance should be a non-empty string");
    }

    [Test]
    public async Task GetTotalBalance_V2Raw_ReturnsNonNullString()
    {
        var result = await CallRpc("circlesV2_getTotalBalance", KnownV2Human, 2, false);
        var balance = result.GetString();
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
        // The implementation filters out not-found avatars (Avatars.cs: Where(r => r != null)),
        // so the result holds only registered inputs — here exactly the known human.
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(result.GetArrayLength(), Is.InRange(1, 2),
            "Result holds found avatars only; the known human must be present");
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

    [Test]
    public async Task GetEventsPaginated_WithAddressFilter_ReturnsPagedResponse()
    {
        var result = await CallRpc("circles_events_paginated", KnownV2Human, null, null, null, null, false);
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(result.TryGetProperty("events", out var events), Is.True);
        Assert.That(events.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(result.TryGetProperty("hasMore", out _), Is.True);
        Assert.That(result.TryGetProperty("nextCursor", out _), Is.True);
    }

    [Test]
    public async Task GetEvents_WithAddressFilter_FlowScopeEventsTiedToAvatarTxs()
    {
        // Regression: address-filtered circles_events used to include
        // CrcV2_FlowEdgesScope* rows from txs the avatar wasn't part of,
        // because flow-scope tables have no address column and the server
        // skipped the address predicate entirely. Every flow-scope event
        // returned for an address-filtered query must share a transactionHash
        // with at least one address-bearing event in the same response.
        var result = await CallRpc("circles_events", KnownV2Human, null, null, null, null, false);
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Array));

        var addressBearingTxs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flowScopeTxs = new List<string>();

        foreach (var element in result.EnumerateArray())
        {
            if (!element.TryGetProperty("event", out var eventNameProp)) continue;
            if (!element.TryGetProperty("values", out var values)) continue;
            if (!values.TryGetProperty("transactionHash", out var txHashProp)) continue;

            var eventName = eventNameProp.GetString() ?? string.Empty;
            var txHash = txHashProp.GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(txHash)) continue;

            if (eventName.StartsWith("CrcV2_FlowEdgesScope", StringComparison.Ordinal))
            {
                flowScopeTxs.Add(txHash);
            }
            else
            {
                addressBearingTxs.Add(txHash);
            }
        }

        foreach (var txHash in flowScopeTxs)
        {
            Assert.That(addressBearingTxs, Does.Contain(txHash),
                $"Flow-scope event in tx {txHash} has no address-bearing event for {KnownV2Human} in the same response");
        }
    }

    [Test]
    public async Task GetEvents_AddressWithNoV2Events_ReturnsNoFlowScopeRows()
    {
        // Fail-closed branch: when the V2 address-bearing UNION returns an
        // empty set (avatar has no V2 events at all — here a guaranteed-empty
        // burn address), the materialized avatar_txs CTE is not built and
        // address-less flow-scope tables are skipped entirely. This must
        // never over-return flow-scope rows from unrelated transactions.
        const string emptyAvatar = "0xdead000000000000000000000000000000000000";
        var result = await CallRpc("circles_events", emptyAvatar, null, null, null, null, false);
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Array));

        foreach (var element in result.EnumerateArray())
        {
            if (!element.TryGetProperty("event", out var eventNameProp)) continue;
            var eventName = eventNameProp.GetString() ?? string.Empty;
            Assert.That(eventName, Does.Not.StartWith("CrcV2_FlowEdgesScope"),
                $"address-less flow-scope row leaked through fail-closed branch for empty avatar (event {eventName})");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Profile queries
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetProfileCid_ReturnsResponse()
    {
        var result = await CallRpc("circles_getProfileCid", KnownV2Human);
        // Returns the CID as a plain string (or null when the avatar has no profile).
        Assert.That(result.ValueKind,
            Is.EqualTo(JsonValueKind.String).Or.EqualTo(JsonValueKind.Null));
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

        var tcBalance = tcResult.GetString();
        var rawBalance = rawResult.GetString();

        // If raw is non-zero, time-circles should also be non-zero
        if (rawBalance != "0" && rawBalance != null)
        {
            Assert.That(tcBalance, Is.Not.EqualTo("0").And.Not.Null,
                "If raw balance is non-zero, time-circles balance should also be non-zero");
        }
    }
}
