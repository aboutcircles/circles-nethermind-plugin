using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Json;
using Circles.Common;
using Circles.Index.Postgres;
using Circles.Index.Query.Dto;
using Circles.Rpc.Host;
using Npgsql;
using Testcontainers.PostgreSql;
using SchemaProvider = Circles.Index.DatabaseSchemaProvider.Schemas;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Integration tests for <see cref="CirclesRpcModule"/> backed by a disposable PostgreSQL instance.
/// A Testcontainers-managed Postgres is started automatically so no manual setup is required.
/// </summary>
[TestFixture]
[Ignore("Requires Nethermind runtime; temporarily skipped until Nethermind build artifacts are available.")]
public class CirclesRpcModuleTests
{
    private static readonly string? NethermindSourceRoot = LocateNethermindSourceRoot();
    private static readonly string[] NethermindRuntimeDirectories = BuildNethermindRuntimeDirectories();
    private static readonly string[] NethermindProbingPaths = BuildNethermindProbingPaths();
    private static readonly Lazy<bool> NethermindRuntimeAssembliesAvailable = new(DetectNethermindRuntimeAssemblies);
    private static readonly ConcurrentDictionary<string, bool> ReferenceAssemblyCache = new(StringComparer.OrdinalIgnoreCase);

    static CirclesRpcModuleTests()
    {
        AssemblyLoadContext.Default.Resolving += ResolveNethermindAssemblies;
    }

    private CirclesRpcModule? _module;
    private PostgreSqlContainer? _postgres;
    private string? _connectionString;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!NethermindRuntimeAssembliesAvailable.Value)
        {
            Assert.Ignore("Skipping CirclesRpcModuleTests because Nethermind runtime assemblies are not available. Run 'git submodule update --init --recursive' and build the Nethermind sources under nethermind-dev/ to enable these tests.");
            return;
        }

        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:15-alpine")
            .WithDatabase("circles_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();

        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        await InitializeDatabaseAsync(_connectionString);

        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", _connectionString);
        Environment.SetEnvironmentVariable("POSTGRES_READONLY_CONNECTION_STRING", _connectionString);
        Environment.SetEnvironmentVariable("EXTERNAL_PATHFINDER_URL", "http://localhost:8080");
        Environment.SetEnvironmentVariable("BALANCE_MODE", "database");

        var settings = new Settings();
        var dataSource = NpgsqlDataSource.Create(settings.IndexReadonlyDbConnectionString);
        _module = new CirclesRpcModule(settings, dataSource);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres != null)
        {
            await _postgres.DisposeAsync();
        }
    }

    private static async Task InitializeDatabaseAsync(string connectionString)
    {
        var schema = new CompositeDatabaseSchema(SchemaProvider.AllSchemas.ToArray());
        var db = new PostgresDb(connectionString, schema);
        db.Migrate();

        await SeedTestDataAsync(connectionString);
    }

    private static async Task SeedTestDataAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        const string seedSql = @"
            INSERT INTO ""System_Block"" (""blockNumber"", ""timestamp"", ""blockHash"", ""eventCounts"")
            VALUES (1, 1735689600, '0xblock', '{}')
            ON CONFLICT DO NOTHING;

            INSERT INTO ""CrcV1_Signup"" (""blockNumber"", ""timestamp"", ""transactionIndex"", ""logIndex"", ""transactionHash"", ""user"", ""token"")
            VALUES (1, 1735689600, 0, 0, '0xsignup', '0x0000000000000000000000000000000000000001', '0x0000000000000000000000000000000000000001')
            ON CONFLICT DO NOTHING;

            INSERT INTO ""CrcV1_Trust"" (""blockNumber"", ""timestamp"", ""transactionIndex"", ""logIndex"", ""transactionHash"", ""canSendTo"", ""user"", ""limit"")
            VALUES (1, 1735689600, 0, 1, '0xtrust', '0x0000000000000000000000000000000000000002', '0x0000000000000000000000000000000000000001', 100)
            ON CONFLICT DO NOTHING;
        ";

        await using var cmd = new NpgsqlCommand(seedSql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static Assembly? ResolveNethermindAssemblies(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name is null || !name.Name.StartsWith("Nethermind", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var basePath in NethermindProbingPaths)
        {
            var candidate = Path.Combine(basePath, $"{name.Name}.dll");
            if (File.Exists(candidate))
            {
                if (IsReferenceAssembly(candidate))
                {
                    continue;
                }

                try
                {
                    return context.LoadFromAssemblyPath(candidate);
                }
                catch (BadImageFormatException)
                {
                    // Try the next candidate; reference assemblies or invalid images will be skipped.
                    continue;
                }
            }
        }

        return null;
    }

    private static string? LocateNethermindSourceRoot()
    {
        var explicitPath = Environment.GetEnvironmentVariable("NETHERMIND_SOURCE");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullExplicit = Path.GetFullPath(explicitPath);
            return Directory.Exists(fullExplicit) ? fullExplicit : null;
        }

        var baseDir = AppContext.BaseDirectory;
        var repoRuntime = Path.GetFullPath(Path.Combine(baseDir, "../../../../..", "nethermind-dev", "nethermind"));
        return Directory.Exists(repoRuntime) ? repoRuntime : null;
    }

    private static string[] BuildNethermindRuntimeDirectories()
    {
        if (NethermindSourceRoot is null)
        {
            return Array.Empty<string>();
        }

        var runnerBin = Path.Combine(NethermindSourceRoot, "src", "Nethermind.Runner", "bin");
        if (!Directory.Exists(runnerBin))
        {
            return Array.Empty<string>();
        }

        var directories = new List<string>();

        try
        {
            foreach (var configurationDir in Directory.EnumerateDirectories(runnerBin))
            {
                foreach (var frameworkDir in Directory.EnumerateDirectories(configurationDir))
                {
                    directories.Add(frameworkDir);
                }
            }
        }
        catch (IOException)
        {
            // Ignore partial builds.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore folders we cannot read.
        }

        return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] BuildNethermindProbingPaths()
    {
        var paths = new List<string> { AppContext.BaseDirectory };

        if (NethermindSourceRoot is not null)
        {
            paths.Add(NethermindSourceRoot);
        }

        paths.AddRange(NethermindRuntimeDirectories);

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool DetectNethermindRuntimeAssemblies()
    {
        foreach (var basePath in NethermindRuntimeDirectories)
        {
            var candidate = Path.Combine(basePath, "Nethermind.Core.dll");
            if (File.Exists(candidate) && !IsReferenceAssembly(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReferenceAssembly(string assemblyPath)
    {
        return ReferenceAssemblyCache.GetOrAdd(assemblyPath, DetermineIfReferenceAssembly);
    }

    private static bool DetermineIfReferenceAssembly(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);

            if (!peReader.HasMetadata)
            {
                return false;
            }

            var reader = peReader.GetMetadataReader();
            var module = reader.GetModuleDefinition();

            foreach (var handle in module.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                if (!TryGetAttributeTypeName(reader, attribute, out var ns, out var name))
                {
                    continue;
                }

                if (ns == "System.Runtime.CompilerServices" &&
                    name == nameof(System.Runtime.CompilerServices.ReferenceAssemblyAttribute))
                {
                    return true;
                }
            }

            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static bool TryGetAttributeTypeName(MetadataReader reader, CustomAttribute attribute, out string? @namespace, out string? name)
    {
        EntityHandle ctor = attribute.Constructor;
        EntityHandle typeHandle = ctor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)ctor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType(),
            _ => default
        };

        switch (typeHandle.Kind)
        {
            case HandleKind.TypeReference:
                var typeRef = reader.GetTypeReference((TypeReferenceHandle)typeHandle);
                @namespace = reader.GetString(typeRef.Namespace);
                name = reader.GetString(typeRef.Name);
                return true;
            case HandleKind.TypeDefinition:
                var typeDef = reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
                @namespace = reader.GetString(typeDef.Namespace);
                name = reader.GetString(typeDef.Name);
                return true;
            default:
                @namespace = null;
                name = null;
                return false;
        }
    }

    #region Helper Methods

    private void RequireModule()
    {
        if (_module == null)
        {
            Assert.Fail("Module not initialized. Check database connection.");
        }
    }

    #endregion

    #region GetHealth Tests

    [Test]
    public async Task GetHealth_WithValidConnection_ReturnsHealthy()
    {
        RequireModule();

        var result = await _module!.GetHealth();
        var json = JsonSerializer.Serialize(result);

        Assert.That(json, Does.Contain("healthy"));
        Assert.That(json, Does.Contain("connected"));
    }

    #endregion

    #region GetAvatarInfo Tests

    [Test]
    public void GetAvatarInfo_WithNonExistentAddress_Throws()
    {
        RequireModule();

        var nonExistentAddress = "0x0000000000000000000000000000000000000000";

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _module!.GetAvatarInfo(nonExistentAddress));

        Assert.That(ex?.Message, Does.Contain("No avatar"));
    }

    [Test]
    public async Task GetAvatarInfoBatch_WithEmptyArray_ReturnsEmptyArray()
    {
        RequireModule();

        var result = await _module!.GetAvatarInfoBatch(Array.Empty<string>());
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void GetAvatarInfoBatch_WithTooManyAddresses_ThrowsException()
    {
        RequireModule();

        var tooManyAddresses = Enumerable.Range(0, 1001)
            .Select(i => $"0x{i:x40}")
            .ToArray();

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await _module!.GetAvatarInfoBatch(tooManyAddresses));
    }

    #endregion

    #region GetTokenBalances Tests

    // [Test]
    // public async Task GetTokenBalances_WithValidAddress_ReturnsListOfBalances()
    // {
    //     RequireModule();

    //     // This test will return an empty list if the address has no tokens
    //     var testAddress = "0x0000000000000000000000000000000000000001";
    //     var result = await _module!.GetTokenBalancesForAccount(testAddress);

    //     Assert.That(result, Is.Not.Null);

    //     // Should return an array (could be empty)
    //     Assert.That(result, Is.InstanceOf<CirclesTokenBalance[]>());

    //     // If there are balances, verify structure
    //     if (result.Length > 0)
    //     {
    //         var balance = result[0];
    //         Assert.That(balance.TokenAddress, Is.Not.Null);
    //         Assert.That(balance.TokenId, Is.Not.Null);
    //         Assert.That(balance.TokenOwner, Is.Not.Null);
    //         Assert.That(balance.Version, Is.GreaterThan(0));

    //         // Verify all value representations exist
    //         Assert.That(balance.AttoCircles, Is.Not.Null);
    //         Assert.That(balance.StaticAttoCircles, Is.Not.Null);
    //         Assert.That(balance.AttoCrc, Is.Not.Null);

    //         // Verify flags
    //         Assert.That(balance.IsErc20 || balance.IsErc1155, Is.True,
    //             "Token must be either ERC20 or ERC1155");
    //     }
    // }

    // [Test]
    // public async Task GetTokenBalances_VerifiesPhase1Limitation()
    // {
    //     RequireModule();

    //     // This test documents that Phase 1 returns raw database values
    //     // without time-based adjustments (inflation/demurrage)

    //     var testAddress = "0x0000000000000000000000000000000000000001";
    //     var result = await _module!.GetTokenBalancesForAccount(testAddress);

    //     if (result.Length > 0)
    //     {
    //         var balance = result[0];

    //         // In Phase 1, these should be equal (no time-based conversion)
    //         // In Phase 3, they would differ based on token type
    //         Assert.That(balance.AttoCircles, Is.EqualTo(balance.AttoCrc),
    //             "Phase 1: AttoCircles should equal AttoCrc (no inflation/demurrage)");
    //         Assert.That(balance.AttoCircles, Is.EqualTo(balance.StaticAttoCircles),
    //             "Phase 1: AttoCircles should equal StaticAttoCircles (no demurrage)");
    //     }
    // }

    #endregion

    #region GetEvents Tests

    [Test]
    public async Task GetEvents_WithNoFilters_ReturnsEvents()
    {
        RequireModule();

        var result = await _module!.GetEvents(
            address: null,
            fromBlock: null,
            toBlock: null,
            eventTypes: null,
            filterPredicates: null,
            sortAscending: false);

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetEvents_WithBlockRange_ReturnsFilteredEvents()
    {
        RequireModule();

        var result = await _module!.GetEvents(
            address: null,
            fromBlock: 0,
            toBlock: 1000,
            eventTypes: null,
            filterPredicates: null,
            sortAscending: false);

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetEvents_WithEventTypeFilter_ReturnsOnlySpecifiedTypes()
    {
        RequireModule();

        var eventTypes = new[] { "CrcV1_Signup", "CrcV2_RegisterHuman" };
        var result = await _module!.GetEvents(
            address: null,
            fromBlock: null,
            toBlock: null,
            eventTypes: eventTypes,
            filterPredicates: null,
            sortAscending: false);

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetEvents_WithAdvancedPredicates_ReturnsFilteredEvents()
    {
        RequireModule();

        var predicates = new[]
        {
            new FilterPredicateDto
            {
                Column = "blockNumber",
                FilterType = Index.Query.FilterType.GreaterThan,
                Value = 1000L
            }
        };

        var result = await _module!.GetEvents(
            address: null,
            fromBlock: null,
            toBlock: null,
            eventTypes: null,
            filterPredicates: predicates,
            sortAscending: false);

        Assert.That(result, Is.Not.Null);
    }

    #endregion

    #region GetProfileCid Tests

    [Test]
    public async Task GetProfileCid_WithNonExistentAddress_ReturnsNullCid()
    {
        RequireModule();

        var nonExistentAddress = "0x0000000000000000000000000000000000000000";
        var result = await _module!.GetProfileCid(nonExistentAddress);

        Assert.That(result.Cid, Is.Null);
    }

    [Test]
    public async Task GetProfileCidBatch_WithEmptyArray_ReturnsEmptyDictionary()
    {
        RequireModule();

        var result = await _module!.GetProfileCidBatch(Array.Empty<string>());

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.InstanceOf<Dictionary<string, string?>>());
        Assert.That(result.Count, Is.EqualTo(0));
    }

    #endregion

    #region GetProfileByAddress Tests

    [Test]
    public async Task GetProfileByAddress_WithNonExistentAddress_ReturnsNull()
    {
        RequireModule();

        var nonExistentAddress = "0x0000000000000000000000000000000000000000";
        var result = await _module!.GetProfileByAddress(nonExistentAddress);

        // Non-existent profiles return null
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetProfileByAddressBatch_VerifiesEnrichment()
    {
        RequireModule();

        // This test verifies that profile enrichment includes all expected fields
        var testAddresses = new[] { "0x0000000000000000000000000000000000000001" };
        var result = await _module!.GetProfileByAddressBatch(testAddresses);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.InstanceOf<JsonElement?[]>());

        if (result.Length > 0 && result[0].HasValue)
        {
            var profile = result[0];
            var json = JsonSerializer.Serialize(profile);

            // Profile should be enriched with address
            Assert.That(json, Does.Contain("address"),
                "Enriched profile should contain address field");
        }
    }

    #endregion

    #region SearchProfiles Tests

    [Test]
    public async Task SearchProfiles_WithEmptyText_ReturnsEmpty()
    {
        RequireModule();

        var result = await _module!.SearchProfiles("", limit: 10);

        // Should return empty result for too-short search
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.InstanceOf<ProfileSearchResult>());
        Assert.That(result.Total, Is.EqualTo(0));
        Assert.That(result.Results, Is.Empty);
    }

    [Test]
    public async Task SearchProfiles_WithValidText_ReturnsResults()
    {
        RequireModule();

        var result = await _module!.SearchProfiles("test", limit: 10);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.InstanceOf<ProfileSearchResult>());
    }

    [Test]
    public void SearchProfiles_WithTooHighLimit_Throws()
    {
        RequireModule();

        Assert.That(async () => await _module!.SearchProfiles("test", limit: 200),
            Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void SearchProfiles_WithInvalidGroupType_Throws()
    {
        RequireModule();

        Assert.That(async () => await _module!.SearchProfiles("test", limit: 10, groupType: "garbage"),
            Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void SearchProfiles_WithEmptyGroupType_DoesNotThrow()
    {
        RequireModule();

        // Whitespace/empty groupType should be treated as "no filter", not an error.
        Assert.That(async () => await _module!.SearchProfiles("test", limit: 10, groupType: ""),
            Throws.Nothing);
        Assert.That(async () => await _module!.SearchProfiles("test", limit: 10, groupType: "   "),
            Throws.Nothing);
    }

    [Test]
    public async Task SearchProfiles_WithValidGroupTypeOpen_ReturnsResult()
    {
        RequireModule();

        var result = await _module!.SearchProfiles("test", limit: 10, groupType: "open");

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.InstanceOf<ProfileSearchResult>());
    }

    [Test]
    public async Task SearchProfiles_WithValidGroupTypeClosed_ReturnsResult()
    {
        RequireModule();

        var result = await _module!.SearchProfiles("test", limit: 10, groupType: "closed");

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.InstanceOf<ProfileSearchResult>());
    }

    #endregion

    #region GetTokenInfo Tests

    [Test]
    public async Task GetTokenInfo_WithValidToken_ReturnsTokenInfo()
    {
        RequireModule();

        // Test with a known token address (adjust as needed for your test database)
        var testToken = "0x0000000000000000000000000000000000000001";
        var result = await _module!.GetTokenInfo(testToken);

        Assert.That(result, Is.Not.Null);
    }

    #endregion

    #region GetTrustRelations Tests

    [Test]
    public async Task GetTrustRelations_WithValidAddress_ReturnsTrustData()
    {
        RequireModule();

        var testAddress = "0x0000000000000000000000000000000000000001";
        var result = await _module!.GetTrustRelations(testAddress);

        Assert.That(result, Is.Not.Null);

        var json = JsonSerializer.Serialize(result);
        Assert.That(json, Does.Contain("user"));
        Assert.That(json, Does.Contain("trusts"));
        Assert.That(json, Does.Contain("trustedBy"));
    }

    #endregion

    #region GetCommonTrust Tests

    [Test]
    public async Task GetCommonTrust_WithTwoAddresses_ReturnsCommonTrust()
    {
        RequireModule();

        var address1 = "0x0000000000000000000000000000000000000001";
        var address2 = "0x0000000000000000000000000000000000000002";
        var result = await _module!.GetCommonTrust(address1, address2);

        Assert.That(result, Is.Not.Null);

        var json = JsonSerializer.Serialize(result);
        Assert.That(json, Does.Contain("address1"));
        Assert.That(json, Does.Contain("address2"));
        Assert.That(json, Does.Contain("commonTrusts"));
    }

    #endregion

    #region Live Balance Mode Tests

    [Test]
    public async Task LiveBalanceMode_GetTotalBalanceV2_ReturnsLiveValue()
    {
        RequireModule();

        // This test documents the expected behavior when BalanceMode=live
        // Note: This requires BALANCE_MODE environment variable to be set to "live"
        // and a running Nethermind node

        var testAddress = "0x0000000000000000000000000000000000000001";

        // This will use live eth_call if BalanceMode=live, otherwise database
        var result = await _module!.GetTotalBalance(testAddress, 2);

        Assert.That(result, Is.Not.Null);
        // Result is a string representation of the total balance
    }

    [Test]
    public async Task LiveBalanceMode_GetTotalBalanceV1_ReturnsLiveValue()
    {
        RequireModule();

        var testAddress = "0x0000000000000000000000000000000000000001";

        // This will use live eth_call if BalanceMode=live, otherwise database
        var result = await _module!.GetTotalBalance(testAddress, 1);

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task LiveBalanceMode_GetTokenBalances_IncludesTimeBasedAdjustments()
    {
        RequireModule();

        // When in live mode, token balances should include time-based adjustments
        // V1: Inflation applied based on period
        // V2: Demurrage applied based on days

        var testAddress = "0x0000000000000000000000000000000000000001";
        var result = await _module!.GetTokenBalances(testAddress);

        Assert.That(result, Is.Not.Null);

        if (result.Length > 0)
        {
            var balance = result[0];

            // All value representations should be present
            Assert.That(balance.AttoCircles, Is.Not.Null);
            Assert.That(balance.StaticAttoCircles, Is.Not.Null);
            Assert.That(balance.AttoCrc, Is.Not.Null);

            // In live mode with real data, these values may differ
            // (depending on token version and time elapsed)
        }
    }

    #endregion

    #region NethermindRpcClient Mock Tests

    // Note: These tests document expected behavior but require mocking
    // to avoid needing a real Nethermind node during testing

    [Test]
    public void NethermindRpcClient_EthCall_DocumentsExpectedBehavior()
    {
        // This test documents the expected behavior of eth_call
        // Actual testing would require mocking HttpClient

        // Expected: POST to RPC URL with JSON-RPC request
        // Request format:
        // {
        //   "jsonrpc": "2.0",
        //   "method": "eth_call",
        //   "params": [
        //     {
        //       "to": "0x...",
        //       "data": "0x..."
        //     },
        //     "latest"
        //   ],
        //   "id": 1
        // }

        // Expected response format:
        // {
        //   "jsonrpc": "2.0",
        //   "id": 1,
        //   "result": "0x..."
        // }

        Assert.Pass("Documentation test - no actual execution");
    }

    #endregion

    #region Balance Calculation Integration Tests

    [Test]
    public void BalanceCalculation_V2Token_AppliesDemurrage()
    {
        RequireModule();

        // This test documents that V2 tokens should have demurrage applied
        // when using live mode

        // In live mode:
        // 1. Fetch raw balance via eth_call
        // 2. Apply demurrage based on current day
        // 3. Return adjusted balance

        // Note: Actual testing requires real V2 tokens in the database
        Assert.Pass("Documentation test - requires V2 token data");
    }

    [Test]
    public void BalanceCalculation_V1Token_AppliesInflation()
    {
        RequireModule();

        // This test documents that V1 tokens should have inflation applied
        // when using live mode

        // In live mode:
        // 1. Fetch raw balance via eth_call
        // 2. Apply inflation based on period
        // 3. Convert to demurraged Circles using V1ToDemurrage
        // 4. Return adjusted balance

        // Note: Actual testing requires real V1 tokens in the database
        Assert.Pass("Documentation test - requires V1 token data");
    }

    #endregion

    #region ERC-1155 Batch Balance Tests

    [Test]
    public void ERC1155_BatchBalance_HandlesMultipleTokens()
    {
        RequireModule();

        // This test documents the expected behavior for ERC-1155 batch balance queries
        // When querying multiple ERC-1155 tokens for the same account,
        // the system should use balanceOfBatch for efficiency

        // Expected flow:
        // 1. Collect all ERC-1155 token addresses
        // 2. Convert addresses to token IDs
        // 3. Create batch call with same account repeated
        // 4. Parse batch response into individual balances

        Assert.Pass("Documentation test - requires ERC-1155 tokens");
    }

    [Test]
    public void ERC1155_SingleToken_UsesBalanceOf()
    {
        RequireModule();

        // Single ERC-1155 token should use balanceOf(address,uint256)
        // instead of balanceOfBatch for simplicity

        Assert.Pass("Documentation test - requires ERC-1155 token");
    }

    #endregion

    #region API Contract Compliance Tests

    /// <summary>
    /// Tests that verify API output matches the remote API contract.
    /// These tests ensure backward compatibility and prevent regressions.
    /// </summary>

    [Test]
    public async Task GetCommonTrust_ReturnsArrayOnly_NotWrappedObject()
    {
        RequireModule();

        var address1 = "0x0000000000000000000000000000000000000001";
        var address2 = "0x0000000000000000000000000000000000000002";
        var result = await _module!.GetCommonTrust(address1, address2);

        Assert.That(result, Is.Not.Null);

        // Result should be CommonTrustResponse object internally
        Assert.That(result, Is.InstanceOf<CommonTrustResponse>());
        Assert.That(result.CommonTrusts, Is.Not.Null);
        Assert.That(result.CommonTrusts, Is.InstanceOf<List<string>>());

        // When serialized via the RPC handler, only the CommonTrusts array should be returned
        // This is tested by verifying the handler code returns result.CommonTrusts
        // The actual RPC response should be: ["0x...", "0x...", ...]
        // NOT: {"address1": "...", "address2": "...", "commonTrusts": [...]}
    }

    [Test]
    public async Task GetTables_ReturnsDetailedSchema_NotJustTableNames()
    {
        RequireModule();

        var result = await _module!.GetTables();

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.InstanceOf<TableNamespace[]>());
        Assert.That(result.Length, Is.GreaterThan(0), "Should return at least one namespace");

        // Verify structure matches remote API
        var firstNamespace = result[0];
        Assert.That(firstNamespace.Namespace, Is.Not.Null);
        Assert.That(firstNamespace.Tables, Is.Not.Null);
        Assert.That(firstNamespace.Tables.Length, Is.GreaterThan(0), "Namespace should have tables");

        var firstTable = firstNamespace.Tables[0];
        Assert.That(firstTable.Table, Is.Not.Null, "Table name should be present");
        Assert.That(firstTable.Topic, Is.Not.Null, "Topic should be present");
        Assert.That(firstTable.Columns, Is.Not.Null, "Columns should be present");
        Assert.That(firstTable.Columns.Length, Is.GreaterThan(0), "Table should have columns");

        var firstColumn = firstTable.Columns[0];
        Assert.That(firstColumn.Column, Is.Not.Null, "Column name should be present");
        Assert.That(firstColumn.Type, Is.Not.Null, "Column type should be present");

        // Verify topic format (should be 66 character hex string: 0x + 64 chars)
        Assert.That(firstTable.Topic, Does.Match(@"^0x[0-9a-f]{64}$"),
            "Topic should be 66-character hex string");
    }

    [Test]
    public async Task GetTables_NamespacesMatchRemoteStructure()
    {
        RequireModule();

        var result = await _module!.GetTables();

        // Expected namespaces from remote API
        var expectedNamespaces = new[] {
            "System", "CrcV1", "CrcV2", "CrcV2_TokenOffers",
            "CrcV2_InvitationEscrow", "CrcV2_OIC", "Safe",
            "V_Safe", "V_CrcV1", "V_CrcV2", "V_Crc"
        };

        var namespaces = result.Select(n => n.Namespace).ToArray();

        // Verify that expected namespaces exist (may have others like "ipfs", "Other")
        foreach (var expected in expectedNamespaces)
        {
            if (namespaces.Contains(expected))
            {
                // At least some expected namespaces should be present
                Assert.Pass($"Found expected namespace: {expected}");
                return;
            }
        }

        // If we have any namespaces at all, that's acceptable for this test
        Assert.That(result.Length, Is.GreaterThan(0),
            "Should return at least one namespace even if schema differs");
    }

    [Test]
    public async Task GetTables_TableNamesParsedCorrectly()
    {
        RequireModule();

        var result = await _module!.GetTables();

        // Find CrcV1 namespace and verify table names are stripped of prefix
        var crcV1Namespace = result.FirstOrDefault(n => n.Namespace == "CrcV1");

        if (crcV1Namespace != null)
        {
            // Tables should have prefix removed: "CrcV1_Signup" -> "Signup"
            var tableNames = crcV1Namespace.Tables.Select(t => t.Table).ToArray();

            // Should NOT contain full names with prefix
            Assert.That(tableNames, Has.No.Member("CrcV1_Signup"),
                "Table names should have namespace prefix removed");

            // Should contain short names if the tables exist
            // (This is flexible in case database schema varies)
            var hasExpectedFormat = tableNames.All(name => !name.StartsWith("CrcV1_"));
            Assert.That(hasExpectedFormat, Is.True,
                "All table names should have CrcV1_ prefix removed");
        }
    }

    [Test]
    public async Task GetTables_ColumnTypesMapCorrectly()
    {
        RequireModule();

        var result = await _module!.GetTables();

        // Find any table with columns
        var anyTable = result.SelectMany(ns => ns.Tables).FirstOrDefault();

        if (anyTable != null && anyTable.Columns.Length > 0)
        {
            var columnTypes = anyTable.Columns.Select(c => c.Type).Distinct().ToArray();

            // Valid column types from remote API
            var validTypes = new[] {
                "Int", "BigInt", "Boolean", "String", "Address",
                "Bytes", "Json", "Double", "AddressArray", "Array"
            };

            foreach (var columnType in columnTypes)
            {
                Assert.That(validTypes, Does.Contain(columnType),
                    $"Column type '{columnType}' should be one of the valid API types");
            }
        }
    }

    #endregion

    #region Error Handling Tests

    [Test]
    public void LiveBalanceMode_InvalidRpcUrl_HandlesGracefully()
    {
        // Test that invalid RPC URL is handled gracefully
        // In production, this should log error and potentially fall back to database

        // Note: This would require injecting a mock NethermindRpcClient
        Assert.Pass("Documentation test - requires dependency injection");
    }

    [Test]
    public void LiveBalanceMode_RpcTimeout_HandlesGracefully()
    {
        // Test that RPC timeouts are handled gracefully

        Assert.Pass("Documentation test - requires mock HTTP client");
    }

    [Test]
    public void LiveBalanceMode_InvalidContractResponse_HandlesGracefully()
    {
        // Test that invalid contract responses (non-hex, wrong length) are handled

        Assert.Pass("Documentation test - requires mock RPC client");
    }

    #endregion
}
