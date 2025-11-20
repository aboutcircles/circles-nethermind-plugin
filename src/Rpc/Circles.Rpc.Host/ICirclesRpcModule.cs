using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Index.Query.Dto;
using Circles.Pathfinder.DTOs;

namespace Circles.Rpc.Host;

/// <summary>
/// Interface for the Circles RPC module providing access to Circles protocol data.
/// </summary>
public interface ICirclesRpcModule
{
    // ========================================================================
    // Balance Queries
    // ========================================================================

    /// <summary>
    /// Gets the total V1 balance for an address.
    /// Supports both database mode (fast but stale) and live mode (accurate with eth_call).
    /// Mode is controlled by BALANCE_MODE environment variable.
    /// </summary>
    Task<string> GetTotalBalance(string address, int version, bool? asTimeCircles);

    /// <summary>
    /// Gets token balances for an address with full metadata.
    /// Returns V1 and V2 tokens with complete CirclesTokenBalance information.
    /// Supports both database mode (fast but stale) and live mode (accurate with eth_call).
    /// Mode is controlled by BALANCE_MODE environment variable.
    /// </summary>
    Task<CirclesTokenBalance[]> GetTokenBalances(string address);

    // ========================================================================
    // Token Information
    // ========================================================================

    /// <summary>
    /// Gets information about a specific token by its address.
    /// </summary>
    Task<TokenInfo> GetTokenInfo(string tokenAddress);

    /// <summary>
    /// Gets information about multiple tokens by their addresses.
    /// Returns array with same length as input, with null entries for tokens that don't exist.
    /// </summary>
    Task<TokenInfo?[]> GetTokenInfoBatch(string[] tokenAddresses);

    // ========================================================================
    // Avatar Information
    // ========================================================================

    /// <summary>
    /// Gets avatar information for a specific address.
    /// Returns both V1 and V2 avatars with merged data when both exist.
    /// </summary>
    Task<AvatarInfo> GetAvatarInfo(string address);

    /// <summary>
    /// Gets avatar information for multiple addresses.
    /// Returns both V1 and V2 avatars with merged data when both exist.
    /// </summary>
    Task<AvatarInfo[]> GetAvatarInfoBatch(string[] addresses);

    // ========================================================================
    // Profile Management
    // ========================================================================

    /// <summary>
    /// Gets the profile CID (Content Identifier) for an address.
    /// </summary>
    Task<string?> GetProfileCid(string address);

    /// <summary>
    /// Gets profile CIDs for multiple addresses.
    /// </summary>
    Task<Dictionary<string, string?>> GetProfileCidBatch(string[] addresses);

    /// <summary>
    /// Gets a profile by its CID from IPFS storage.
    /// Results are cached in memory.
    /// </summary>
    Task<JsonElement?> GetProfileByCid(string cid);

    /// <summary>
    /// Gets multiple profiles by their CIDs from IPFS storage.
    /// </summary>
    Task<Dictionary<string, JsonElement?>> GetProfileByCidBatch(string[] cids);

    /// <summary>
    /// Gets a profile by avatar address.
    /// Looks up the CID first, then retrieves the profile.
    /// Enriched with avatar type and short name from V2 registrations.
    /// </summary>
    Task<JsonElement?> GetProfileByAddress(string address);

    /// <summary>
    /// Gets multiple profiles by avatar addresses.
    /// Enriched with avatar type and short name from V2 registrations.
    /// </summary>
    Task<Dictionary<string, JsonElement?>> GetProfileByAddressBatch(string[] addresses);

    /// <summary>
    /// Searches for profiles using full-text search.
    /// </summary>
    /// <param name="text">Search text (max 3 tokens, each > 1 character)</param>
    /// <param name="limit">Maximum results to return (default: 20, max: 100)</param>
    /// <param name="offset">Number of results to skip</param>
    /// <param name="types">Filter by avatar types (e.g., "CrcV2_RegisterHuman", "CrcV2_RegisterGroup")</param>
    Task<ProfileSearchResult> SearchProfiles(string text, int limit = 20, int offset = 0, string[]? types = null);

    // ========================================================================
    // Trust Relations
    // ========================================================================

    /// <summary>
    /// Gets trust relations for an address.
    /// Returns both trusts (who this address trusts) and trustedBy (who trusts this address).
    /// Currently returns V1 trust relations only.
    /// </summary>
    Task<TrustRelationsResponse> GetTrustRelations(string address);

    /// <summary>
    /// Finds common trust connections between two addresses.
    /// For V2 humans, uses "safer" paths (outgoing → incoming).
    /// For V2 groups and V1, uses shared outgoing trusts.
    /// </summary>
    /// <param name="address1">First address</param>
    /// <param name="address2">Second address</param>
    /// <param name="version">Filter by version (1, 2, or null for both)</param>
    Task<CommonTrustResponse> GetCommonTrust(string address1, string address2, int? version = null);

    // ========================================================================
    // Events
    // ========================================================================

    /// <summary>
    /// Gets events from the blockchain, filtered by various criteria.
    /// Supports advanced filtering with FilterPredicateDto and ConjunctionDto for complex queries.
    /// </summary>
    /// <param name="address">Filter by address (null for all addresses)</param>
    /// <param name="fromBlock">Starting block number (inclusive)</param>
    /// <param name="toBlock">Ending block number (inclusive)</param>
    /// <param name="eventTypes">Filter by event types (null for all events)</param>
    /// <param name="filterPredicates">Advanced filter predicates for complex queries</param>
    /// <param name="sortAscending">Sort order (default: descending by block number)</param>
    Task<EventsResponse> GetEvents(
        string? address,
        long? fromBlock,
        long? toBlock,
        string[]? eventTypes,
        IFilterPredicateDto[]? filterPredicates = null,
        bool? sortAscending = false);

    // ========================================================================
    // Generic Database Query
    // ========================================================================

    /// <summary>
    /// Executes a generic database query using a structured DTO.
    /// Allows dynamic querying of indexed data.
    /// </summary>
    Task<QueryResponse> Query(SelectDto query);

    // ========================================================================
    // Pathfinder Integration (Proxy)
    // ========================================================================

    /// <summary>
    /// Finds a payment path through the Circles trust network.
    /// This method proxies the request to an external Pathfinder service.
    /// </summary>
    Task<JsonElement> FindPathV2(FlowRequest flowRequest);

    /// <summary>
    /// Gets a snapshot of the entire Circles trust network.
    /// This method proxies the request to an external Pathfinder service.
    /// </summary>
    Task<NetworkSnapshotResponse> GetNetworkSnapshot();

    // ========================================================================
    // System Information
    // ========================================================================

    /// <summary>
    /// Health check endpoint.
    /// Checks database connectivity and blockchain sync status (when live mode is enabled).
    /// </summary>
    Task<HealthResponse> GetHealth();

    /// <summary>
    /// Gets the list of available database tables/schemas.
    /// Useful for discovering what data is available for querying.
    /// </summary>
    Task<TableNamespace[]> GetTables();
}

// ========================================================================
// DTOs (Data Transfer Objects)
// ========================================================================

/// <summary>
/// Strongly-typed balance string (represents token balance in atto units).
/// </summary>
public readonly record struct BalanceString(string Value)
{
    public static implicit operator string(BalanceString balance) => balance.Value;
    public static implicit operator BalanceString(string value) => new(value);
    public override string ToString() => Value;
}

/// <summary>
/// Simple token balance (V1 implementation).
/// </summary>
public record SimpleTokenBalance(string Token, string Balance);

/// <summary>
/// Token information response.
/// </summary>
public record TokenInfo(
    string TokenAddress,
    string TokenOwner,
    string TokenType,
    int Version,
    bool IsErc20,
    bool IsErc1155,
    bool IsWrapped,
    bool IsInflationary,
    bool IsGroup
);

/// <summary>
/// Avatar information response (V2).
/// </summary>
public record AvatarInfo(
    int Version,
    string Type,
    string Avatar,
    string TokenId,
    bool HasV1,
    string? V1Token,
    string CidV0Digest,
    string? CidV0,
    bool IsHuman,
    string? Name,
    string Symbol
);

/// <summary>
/// Trust relation.
/// </summary>
public record TrustRelation(
    string User,
    int Limit
);

/// <summary>
/// Trust relations response.
/// </summary>
public record TrustRelationsResponse(
    string User,
    TrustRelation[] Trusts,
    TrustRelation[] TrustedBy
);

/// <summary>
/// Common trust response.
/// </summary>
public record CommonTrustResponse(
    string Address1,
    string Address2,
    List<string> CommonTrusts
);

/// <summary>
/// Event response.
/// </summary>
public record EventResponse(
    long BlockNumber,
    string TransactionHash,
    int LogIndex,
    string Event,
    object Payload
);

/// <summary>
/// Events collection response. Serializes as a plain array for backwards compatibility with remote API.
/// </summary>
[JsonConverter(typeof(EventsResponseJsonConverter))]
public class EventsResponse
{
    public object[] Events { get; }

    public EventsResponse(object[] events)
    {
        Events = events ?? Array.Empty<object>();
    }

    // Implicit conversion to make it easy to return
    public static implicit operator EventsResponse(object[] events) => new EventsResponse(events);
}

/// <summary>
/// JSON converter that serializes EventsResponse as a plain array (not wrapped in an object).
/// </summary>
public class EventsResponseJsonConverter : JsonConverter<EventsResponse>
{
    public override EventsResponse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var events = JsonSerializer.Deserialize<object[]>(ref reader, options);
        return new EventsResponse(events ?? Array.Empty<object>());
    }

    public override void Write(Utf8JsonWriter writer, EventsResponse value, JsonSerializerOptions options)
    {
        // Serialize as plain array, not as an object with "Events" property
        JsonSerializer.Serialize(writer, value.Events, options);
    }
}

/// <summary>
/// Query response with columns and rows.
/// </summary>
public record QueryResponse(List<string> Columns, List<Dictionary<string, object?>> Rows);

/// <summary>
/// Health check response.
/// </summary>
public record HealthResponse(
    string Status,
    long Timestamp,
    string Database,
    string Index
);

/// <summary>
/// Column definition in a table schema.
/// </summary>
public record TableColumn(string Column, string Type);

/// <summary>
/// Table definition with columns and topic.
/// </summary>
public record TableDefinition(string Table, string Topic, TableColumn[] Columns);

/// <summary>
/// Namespace containing multiple tables.
/// </summary>
public record TableNamespace(string Namespace, TableDefinition[] Tables);

/// <summary>
/// Network snapshot (proxied from pathfinder).
/// </summary>
public record NetworkSnapshotResponse(JsonElement BlockNumber, JsonElement Addresses);

/// <summary>
/// Rich token balance information with multiple value representations.
/// Matches the original implementation's CirclesTokenBalance.
/// </summary>
public record CirclesTokenBalance(
    string TokenAddress,
    string TokenId,
    string TokenOwner,
    string TokenType,
    int Version,
    string AttoCircles,
    decimal Circles,
    string StaticAttoCircles,
    decimal StaticCircles,
    string AttoCrc,
    decimal Crc,
    bool IsErc20,
    bool IsErc1155,
    bool IsWrapped,
    bool IsInflationary,
    bool IsGroup
);

/// <summary>
/// Profile search result with avatar information and profile data.
/// </summary>
public record ProfileSearchResult(
    int Total,
    ProfileSearchResultItem[] Results
);

/// <summary>
/// Individual profile search result item.
/// </summary>
public record ProfileSearchResultItem(
    string Avatar,
    AvatarInfo AvatarInfo,
    JsonElement? Profile
);
