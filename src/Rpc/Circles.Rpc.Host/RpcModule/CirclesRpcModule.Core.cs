using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace Circles.Rpc.Host;

/// <summary>
/// Core setup and infrastructure for CirclesRpcModule.
/// Contains constructor, fields, and connection management.
///
/// The CirclesRpcModule is split into multiple partial class files for maintainability:
/// - CirclesRpcModule.Core.cs       - Constructor, fields, connection management
/// - CirclesRpcModule.Balances.cs   - Token balance queries (GetTotalBalance, GetTokenBalances)
/// - CirclesRpcModule.Tokens.cs     - Token info and exposure (GetTokenInfo, GetTokenHolders)
/// - CirclesRpcModule.Avatars.cs    - Avatar information (GetAvatarInfo, GetAvatarInfoBatch)
/// - CirclesRpcModule.Profiles.cs   - Profile CIDs and content (GetProfileByCid, SearchProfiles)
/// - CirclesRpcModule.Trust.cs      - Trust relations (GetTrustRelations, GetCommonTrust)
/// - CirclesRpcModule.Groups.cs     - Group operations (FindGroups, GetGroupMembers)
/// - CirclesRpcModule.Helpers.cs    - Utility methods (GetHealth, GetTables)
///
/// Remaining methods are in the main CirclesRpcModule.cs file:
/// - Transaction history (GetTransactionHistory, GetTransactionHistoryEnriched)
/// - Events (GetEvents)
/// - Pathfinder (GetNetworkSnapshot, FindPathV2)
/// - Query (Query method)
/// - SDK endpoints (GetProfileView, GetTrustNetworkSummary, etc.)
/// </summary>
public partial class CirclesRpcModule : ICirclesRpcModule
{
    private readonly Settings _settings;
    private readonly string _readOnlyDbConnectionString;
    private readonly MemoryCache _profileByCidCache;
    private readonly MemoryCache _tokenExposureCache;
    private static readonly HttpClient HttpClient = new();
    private readonly NethermindRpcClient? _nethermindRpcClient;
    private readonly ILogger<CirclesRpcModule>? _logger;
    private readonly CacheServiceClient.CacheServiceClient? _cacheServiceClient;

    // Snapshot cache - stores last known ETag and cached response
    private string? _snapshotETag;
    private JsonElement? _cachedSnapshot;
    private readonly object _snapshotLock = new();

    public CirclesRpcModule(
        Settings settings,
        IHttpClientFactory? httpClientFactory = null,
        ILogger<CirclesRpcModule>? logger = null,
        CacheServiceClient.CacheServiceClient? cacheServiceClient = null)
    {
        _settings = settings;
        _readOnlyDbConnectionString = settings.IndexReadonlyDbConnectionString;
        _profileByCidCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000 });
        _tokenExposureCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 50_000 });
        _logger = logger;
        _cacheServiceClient = cacheServiceClient;

        // Initialize Nethermind RPC client if BalanceMode is "live"
        if (_settings.BalanceMode.Equals("live", StringComparison.OrdinalIgnoreCase))
        {
            if (httpClientFactory != null)
            {
                _nethermindRpcClient = new NethermindRpcClient(httpClientFactory, _settings.NethermindRpcUrl ?? "http://localhost:8545");
            }
            else
            {
                _nethermindRpcClient = null; // HttpClientFactory not available, cannot create client
            }
        }
    }

    private async Task<NpgsqlConnection> CreateConnectionAsync()
    {
        var connection = new NpgsqlConnection(_readOnlyDbConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
