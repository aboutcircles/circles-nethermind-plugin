using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Index.Query.Dto;
using Circles.Common.Dto;

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
    Task<TotalBalanceResponse> GetTotalBalance(string address, int version, bool? asTimeCircles);

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
    Task<TokenInfo?> GetTokenInfo(string tokenAddress);

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
    Task<ProfileCidResponse> GetProfileCid(string address);

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
    Task<JsonElement?[]> GetProfileByCidBatch(string[] cids);

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
    Task<JsonElement?[]> GetProfileByAddressBatch(string[] addresses);

    /// <summary>
    /// Searches for profiles using full-text search.
    /// </summary>
    /// <param name="text">Search text (max 3 tokens, each > 1 character)</param>
    /// <param name="limit">Maximum results to return (default: 20, max: 100)</param>
    /// <param name="offset">Number of results to skip</param>
    /// <param name="types">Filter by avatar types (e.g., "CrcV2_RegisterHuman", "CrcV2_RegisterGroup")</param>
    Task<ProfileSearchResult> SearchProfiles(string text, int limit = 20, int offset = 0, string[]? types = null);

    // ========================================================================
    // SDK Enablement Methods
    // ========================================================================

    /// <summary>
    /// Gets a complete profile view combining avatar, profile, trust stats, and balances.
    /// Replaces 6-7 separate RPC calls for displaying a user profile.
    /// </summary>
    /// <param name="address">Avatar address to query</param>
    Task<ProfileViewResponse> GetProfileView(string address);

    /// <summary>
    /// Gets aggregated trust network statistics including mutual trusts and network reach.
    /// </summary>
    /// <param name="address">Avatar address to query</param>
    /// <param name="maxDepth">Maximum depth for network traversal (optional)</param>
    Task<TrustNetworkSummaryResponse> GetTrustNetworkSummary(string address, int? maxDepth = null);

    /// <summary>
    /// Gets trust relations categorized by type (mutual, one-way) with enriched avatar info.
    /// Server-side categorization + batch avatar lookup.
    /// Returns paginated results with cursor-based navigation.
    /// </summary>
    /// <param name="address">Avatar address to query</param>
    /// <param name="limit">Maximum results per page (default: 50, max: 200)</param>
    /// <param name="cursor">Cursor for pagination (from previous response's nextCursor)</param>
    Task<PagedAggregatedTrustRelationsResponse> GetAggregatedTrustRelationsEnriched(
        string address,
        int? limit = null,
        string? cursor = null);

    /// <summary>
    /// Gets addresses that trust the given address AND have sufficient balance to invite.
    /// Useful for invitation flows and sponsor discovery.
    /// Returns paginated results with cursor-based navigation.
    /// </summary>
    /// <param name="address">Avatar address to query</param>
    /// <param name="minimumBalance">Minimum balance required (in CRC, optional)</param>
    /// <param name="limit">Maximum results per page (default: 50, max: 200)</param>
    /// <param name="cursor">Cursor for pagination (from previous response's nextCursor)</param>
    Task<PagedValidInvitersResponse> GetValidInviters(
        string address,
        string? minimumBalance = null,
        int? limit = null,
        string? cursor = null);

    /// <summary>
    /// Gets transaction history with enriched participant profiles and metadata.
    /// Replaces circles_events + multiple getProfileByAddress calls.
    /// </summary>
    /// <param name="address">Avatar address to query</param>
    /// <param name="fromBlock">Starting block number</param>
    /// <param name="toBlock">Ending block number (optional)</param>
    /// <param name="limit">Maximum transactions to return (optional, default 20)</param>
    /// <param name="cursor">Cursor for pagination (base64 encoded block:tx:log)</param>
    /// <param name="version">Filter by version (null = V2 only for backward compat, 1 = V1 only, 2 = V2 only)</param>
    /// <param name="excludeIntermediary">If true, uses TransferSummary which excludes intermediary hop transfers (default: true)</param>
    Task<PagedResponse<EnrichedTransaction>> GetTransactionHistoryEnriched(
        string address,
        long fromBlock,
        long? toBlock = null,
        int? limit = null,
        string? cursor = null,
        int? version = null,
        bool excludeIntermediary = true);

    /// <summary>
    /// Unified search across profiles by address prefix OR name/description text.
    /// Automatically detects search type (0x prefix = address, otherwise = text).
    /// Returns paginated results with cursor-based navigation.
    /// </summary>
    /// <param name="query">Search query (address or text)</param>
    /// <param name="limit">Maximum results per page (default: 20, max: 100)</param>
    /// <param name="cursor">Cursor for pagination (from previous response's nextCursor)</param>
    /// <param name="types">Filter by avatar types (optional)</param>
    Task<PagedProfileSearchResponse> SearchProfileByAddressOrName(
        string query,
        int? limit = null,
        string? cursor = null,
        string[]? types = null);

    /// <summary>
    /// Gets the invitation origin for an address, reconstructing how they were invited to Circles.
    /// Returns information about the invitation type, inviter, and related transaction details.
    /// Supports multiple invitation mechanisms: V1 Signup, V2 Standard, V2 Escrow, and V2 At Scale.
    /// </summary>
    /// <param name="address">Avatar address to query</param>
    /// <returns>Invitation origin details or null if address is not registered</returns>
    Task<InvitationOriginResponse?> GetInvitationOrigin(string address);

    /// <summary>
    /// Gets all available invitations for an address from all sources (trust, escrow, at-scale).
    /// Combines multiple invitation mechanisms into a single response for efficient client-side rendering.
    /// </summary>
    /// <param name="address">Avatar address to query for available invitations</param>
    /// <param name="minimumBalance">Minimum balance required for trust-based invitations (in CRC, optional)</param>
    /// <returns>All available invitations grouped by source type</returns>
    Task<AllInvitationsResponse> GetAllInvitations(string address, string? minimumBalance = null);

    /// <summary>
    /// Gets trust-based invitations (addresses that trust the invitee and have sufficient balance).
    /// Subset of GetAllInvitations — use when only trust invitations are needed.
    /// </summary>
    Task<TrustInvitation[]> GetTrustInvitations(string address, string? minimumBalance = null);

    /// <summary>
    /// Gets escrow-based invitations (CRC escrowed for the address).
    /// Filters out redeemed, revoked, and refunded escrows server-side.
    /// </summary>
    Task<EscrowInvitation[]> GetEscrowInvitations(string address);

    /// <summary>
    /// Gets at-scale invitations (pre-created accounts that haven't been claimed).
    /// </summary>
    Task<AtScaleInvitation[]> GetAtScaleInvitations(string address);

    /// <summary>
    /// Gets accounts invited by a specific avatar.
    /// When accepted=true: accounts that registered using this avatar as inviter.
    /// When accepted=false: accounts this avatar trusts that are NOT yet registered (pending).
    /// </summary>
    Task<InvitationsFromResponse> GetInvitationsFrom(string address, bool accepted = false);

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
    /// Gets aggregated trust relations for an address with SDK-compatible format.
    /// Returns trust relations grouped by counterpart with relation type (mutuallyTrusts/trusts/trustedBy).
    /// Includes expiryTime and objectAvatarType (Group/Human/Organization) fields.
    /// </summary>
    /// <param name="avatar">Avatar address to query</param>
    Task<AggregatedTrustRelation[]> GetAggregatedTrustRelations(string avatar);

    /// <summary>
    /// Finds groups with optional filters and cursor-based pagination.
    /// SDK-compatible method for group discovery.
    /// </summary>
    /// <param name="limit">Maximum number of groups to return (default: 50)</param>
    /// <param name="queryParams">Optional filter parameters (nameStartsWith, symbolStartsWith, ownerIn)</param>
    /// <param name="cursor">Cursor for pagination (base64 encoded block:tx:log)</param>
    Task<PagedResponse<GroupRow>> FindGroups(int limit = 50, GroupQueryParams? queryParams = null, string? cursor = null);

    /// <summary>
    /// Gets members of a specific group with cursor-based pagination.
    /// SDK-compatible method for group membership queries.
    /// </summary>
    /// <param name="groupAddress">Group address to query members for</param>
    /// <param name="limit">Maximum number of members to return (default: 100)</param>
    /// <param name="cursor">Cursor for pagination (base64 encoded block:tx:log)</param>
    Task<PagedResponse<GroupMembershipRow>> GetGroupMembers(string groupAddress, int limit = 100, string? cursor = null);

    /// <summary>
    /// Gets groups that an avatar is a member of with cursor-based pagination.
    /// SDK-compatible method (inverse of GetGroupMembers).
    /// </summary>
    /// <param name="memberAddress">Member address to query group memberships for</param>
    /// <param name="limit">Maximum number of memberships to return (default: 50)</param>
    /// <param name="cursor">Cursor for pagination (base64 encoded block:tx:log)</param>
    Task<PagedResponse<GroupMembershipRow>> GetGroupMemberships(string memberAddress, int limit = 50, string? cursor = null);

    /// <summary>
    /// Gets transaction history for an avatar with cursor-based pagination.
    /// SDK-compatible method with all circle amount formats calculated.
    /// Queries transfers where avatar is either sender or receiver.
    /// </summary>
    /// <param name="avatarAddress">Avatar address to query transaction history for</param>
    /// <param name="limit">Maximum number of transactions to return (default: 50)</param>
    /// <param name="cursor">Cursor for pagination (base64 encoded block:tx:log)</param>
    /// <param name="version">Filter by version (null = both V1+V2, 1 = V1 only, 2 = V2 only)</param>
    /// <param name="excludeIntermediary">If true, uses TransferSummary which excludes intermediary hop transfers (default: true)</param>
    Task<PagedResponse<TransactionHistoryRow>> GetTransactionHistory(string avatarAddress, int limit = 50, string? cursor = null, int? version = null, bool excludeIntermediary = true);

    /// <summary>
    /// Gets all holders of a specific token with cursor-based pagination.
    /// SDK-compatible method to query token distribution.
    /// </summary>
    /// <param name="tokenAddress">Token address to query holders for</param>
    /// <param name="limit">Maximum number of holders to return (default: 100)</param>
    /// <param name="cursor">Cursor for pagination (account address)</param>
    Task<PagedResponse<TokenHolderRow>> GetTokenHolders(string tokenAddress, int limit = 100, string? cursor = null);

    /// <summary>
    /// Gets transfer data (calldata bytes) for ERC-1155 transfers involving an address.
    /// Convenience method for querying CrcV2_TransferData without raw filterPredicates.
    /// </summary>
    /// <param name="address">Primary address to filter</param>
    /// <param name="direction">"sent" (from=addr), "received" (to=addr), or null (both)</param>
    /// <param name="counterparty">If set, AND with specific counterparty</param>
    /// <param name="fromBlock">Start block (inclusive), null for no lower bound</param>
    /// <param name="toBlock">End block (inclusive), null for no upper bound</param>
    /// <param name="limit">Max results (default 50, max 1000)</param>
    /// <param name="cursor">Cursor for pagination (base64 encoded block:tx:log)</param>
    Task<PagedResponse<TransferDataRow>> GetTransferData(
        string address,
        string? direction = null,
        string? counterparty = null,
        long? fromBlock = null,
        long? toBlock = null,
        int limit = 50,
        string? cursor = null);

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
    /// Returns paginated results with cursor-based navigation.
    /// </summary>
    /// <param name="address">Filter by address (null for all addresses)</param>
    /// <param name="fromBlock">Starting block number (inclusive)</param>
    /// <param name="toBlock">Ending block number (inclusive)</param>
    /// <param name="eventTypes">Filter by event types (null for all events)</param>
    /// <param name="filterPredicates">Advanced filter predicates for complex queries</param>
    /// <param name="sortAscending">Sort order (default: descending by block number)</param>
    /// <param name="limit">Maximum number of events to return (default: 100, max: 1000)</param>
    /// <param name="cursor">Cursor for pagination (from previous response's nextCursor)</param>
    Task<PagedEventsResponse> GetEvents(
        string? address,
        long? fromBlock,
        long? toBlock,
        string[]? eventTypes,
        IFilterPredicateDto[]? filterPredicates = null,
        bool? sortAscending = false,
        int? limit = null,
        string? cursor = null);

    // ========================================================================
    // Generic Database Query
    // ========================================================================

    /// <summary>
    /// Executes a generic database query using a structured DTO.
    /// Allows dynamic querying of indexed data.
    /// Returns paginated results with cursor-based navigation when querying tables with event columns.
    /// </summary>
    /// <param name="query">The query definition with namespace, table, columns, filter, order, and limit</param>
    /// <param name="cursor">Cursor for pagination (from previous response's nextCursor)</param>
    Task<PagedQueryResponse> Query(SelectDto query, string? cursor = null);

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
    /// Returns the raw pathfinder response to match production behavior.
    /// </summary>
    Task<JsonElement> GetNetworkSnapshot();

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
/// SDK-compatible aggregated trust relation.
/// Matches SDK AggregatedTrustRelation type with additional fields.
/// </summary>
public record AggregatedTrustRelation(
    [property: JsonPropertyName("subjectAvatar")] string SubjectAvatar,
    [property: JsonPropertyName("relation")] string Relation,  // "mutuallyTrusts" | "trusts" | "trustedBy"
    [property: JsonPropertyName("objectAvatar")] string ObjectAvatar,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("expiryTime")] long ExpiryTime,
    [property: JsonPropertyName("objectAvatarType")] string? ObjectAvatarType  // "Human" | "Group" | "Organization"
);

/// <summary>
/// SDK-compatible paged response with cursor-based pagination.
/// </summary>
public record PagedResponse<T>(
    [property: JsonPropertyName("results")] T[] Results,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("nextCursor")] string? NextCursor
);

/// <summary>
/// SDK-compatible GroupRow type.
/// </summary>
public record GroupRow(
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("mint")] string Mint,
    [property: JsonPropertyName("treasury")] string Treasury,
    [property: JsonPropertyName("blockNumber")] long BlockNumber,
    [property: JsonPropertyName("timestamp")] long Timestamp
);

/// <summary>
/// Group query parameters for filtering.
/// </summary>
public record GroupQueryParams(
    [property: JsonPropertyName("nameStartsWith")] string? NameStartsWith = null,
    [property: JsonPropertyName("symbolStartsWith")] string? SymbolStartsWith = null,
    [property: JsonPropertyName("ownerIn")] string[]? OwnerIn = null
);

/// <summary>
/// SDK-compatible GroupMembershipRow type.
/// </summary>
public record GroupMembershipRow(
    [property: JsonPropertyName("blockNumber")] long BlockNumber,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("transactionIndex")] int TransactionIndex,
    [property: JsonPropertyName("logIndex")] int LogIndex,
    [property: JsonPropertyName("transactionHash")] string TransactionHash,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("member")] string Member,
    [property: JsonPropertyName("expiryTime")] long ExpiryTime
);

/// <summary>
/// SDK-compatible TransactionHistoryRow type with all circle amount formats.
/// </summary>
public record TransactionHistoryRow(
    [property: JsonPropertyName("blockNumber")] long BlockNumber,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("transactionIndex")] int TransactionIndex,
    [property: JsonPropertyName("logIndex")] int LogIndex,
    [property: JsonPropertyName("transactionHash")] string TransactionHash,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("operator")] string? Operator,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("value")] string Value, // Raw demurraged attoCircles as string
                                                        // Calculated circle amounts (6 formats as per SDK calculateCircleAmounts)
    [property: JsonPropertyName("circles")] string Circles,
    [property: JsonPropertyName("attoCircles")] string AttoCircles,
    [property: JsonPropertyName("crc")] string Crc,
    [property: JsonPropertyName("attoCrc")] string AttoCrc,
    [property: JsonPropertyName("staticCircles")] string StaticCircles,
    [property: JsonPropertyName("staticAttoCircles")] string StaticAttoCircles
);

/// <summary>
/// Transfer data row containing calldata bytes from ERC-1155 transfers.
/// </summary>
public record TransferDataRow(
    [property: JsonPropertyName("blockNumber")] long BlockNumber,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("transactionIndex")] int TransactionIndex,
    [property: JsonPropertyName("logIndex")] int LogIndex,
    [property: JsonPropertyName("transactionHash")] string TransactionHash,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("data")] string Data  // hex-encoded bytes
);

/// <summary>
/// SDK-compatible TokenHolderRow type representing a token holder.
/// </summary>
public record TokenHolderRow(
    [property: JsonPropertyName("account")] string Account,
    [property: JsonPropertyName("balance")] string Balance,
    [property: JsonPropertyName("tokenAddress")] string TokenAddress,
    [property: JsonPropertyName("version")] int Version
);

/// <summary>
/// Common trust response.
/// </summary>
public record CommonTrustResponse(
    string Address1,
    string Address2,
    string[] CommonTrusts
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
/// Uses arrays for rows to match production format.
/// </summary>
public record QueryResponse(List<string> Columns, List<object?[]> Rows);

/// <summary>
/// Paginated query response with columns, rows, and cursor-based pagination.
/// </summary>
public record PagedQueryResponse(
    [property: JsonPropertyName("columns")] List<string> Columns,
    [property: JsonPropertyName("rows")] List<object?[]> Rows,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("nextCursor")] string? NextCursor
);

/// <summary>
/// Paginated events response with cursor-based navigation.
/// </summary>
public record PagedEventsResponse(
    [property: JsonPropertyName("events")] object[] Events,
    [property: JsonPropertyName("hasMore")] bool HasMore,
    [property: JsonPropertyName("nextCursor")] string? NextCursor
);

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

/// <summary>
/// Total balance response. Serializes as a plain string value.
/// </summary>
[JsonConverter(typeof(TotalBalanceResponseJsonConverter))]
public record TotalBalanceResponse(string Balance)
{
    public static implicit operator string(TotalBalanceResponse response) => response.Balance;
    public static implicit operator TotalBalanceResponse(string value) => new(value);
    public override string ToString() => Balance;
}

/// <summary>
/// Profile CID response. Serializes as a plain string value or null.
/// </summary>
[JsonConverter(typeof(ProfileCidResponseJsonConverter))]
public record ProfileCidResponse(string? Cid)
{
    public static implicit operator string?(ProfileCidResponse response) => response.Cid;
    public static implicit operator ProfileCidResponse(string? value) => new(value);
    public override string? ToString() => Cid;
}

/// <summary>
/// JSON converter that serializes TotalBalanceResponse as a plain string value.
/// </summary>
public class TotalBalanceResponseJsonConverter : JsonConverter<TotalBalanceResponse>
{
    public override TotalBalanceResponse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var balance = reader.GetString();
        return new TotalBalanceResponse(balance ?? "");
    }

    public override void Write(Utf8JsonWriter writer, TotalBalanceResponse value, JsonSerializerOptions options)
    {
        // Serialize as plain string value, not as an object
        writer.WriteStringValue(value.Balance);
    }
}

/// <summary>
/// JSON converter that serializes ProfileCidResponse as a plain string value or null.
/// </summary>
public class ProfileCidResponseJsonConverter : JsonConverter<ProfileCidResponse>
{
    public override ProfileCidResponse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new ProfileCidResponse(null);
        }

        var cid = reader.GetString();
        return new ProfileCidResponse(cid);
    }

    public override void Write(Utf8JsonWriter writer, ProfileCidResponse value, JsonSerializerOptions options)
    {
        // Serialize as plain string value or null, not as an object
        if (value.Cid == null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.Cid);
        }
    }
}

// ============================================================================
// SDK Enablement Response Types
// ============================================================================

/// <summary>
/// Consolidated profile view combining avatar, profile, trust stats, and balances.
/// </summary>
public record ProfileViewResponse
{
    public string Address { get; init; } = string.Empty;
    public AvatarInfo? AvatarInfo { get; init; }
    public JsonElement? Profile { get; init; }
    public TrustStats TrustStats { get; init; } = new();
    public string? V1Balance { get; init; }
    public string? V2Balance { get; init; }
}

/// <summary>
/// Trust statistics for an avatar.
/// </summary>
public record TrustStats
{
    public int TrustsCount { get; init; }
    public int TrustedByCount { get; init; }
}

/// <summary>
/// Aggregated trust network summary with network reach metrics.
/// </summary>
public record TrustNetworkSummaryResponse
{
    public string Address { get; init; } = string.Empty;
    public int DirectTrustsCount { get; init; }
    public int DirectTrustedByCount { get; init; }
    public int MutualTrustsCount { get; init; }
    public string[] MutualTrusts { get; init; } = Array.Empty<string>();
    public int NetworkReach { get; init; }
}

/// <summary>
/// Aggregated trust relations categorized by type.
/// </summary>
public record AggregatedTrustRelationsResponse
{
    public string Address { get; init; } = string.Empty;
    public TrustRelationInfo[] Mutual { get; init; } = Array.Empty<TrustRelationInfo>();
    public TrustRelationInfo[] Trusts { get; init; } = Array.Empty<TrustRelationInfo>();
    public TrustRelationInfo[] TrustedBy { get; init; } = Array.Empty<TrustRelationInfo>();
}

/// <summary>
/// Trust relation with enriched avatar info.
/// </summary>
public record TrustRelationInfo
{
    public string Address { get; init; } = string.Empty;
    public AvatarInfo? AvatarInfo { get; init; }
    public string RelationType { get; init; } = string.Empty;
}

/// <summary>
/// Valid inviters with balance information.
/// </summary>
public record ValidInvitersResponse
{
    public string Address { get; init; } = string.Empty;
    public InviterInfo[] ValidInviters { get; init; } = Array.Empty<InviterInfo>();
}

/// <summary>
/// Inviter information with balance and avatar data.
/// </summary>
public record InviterInfo
{
    public string Address { get; init; } = string.Empty;
    public string Balance { get; init; } = string.Empty;
    public AvatarInfo? AvatarInfo { get; init; }
}

/// <summary>
/// Enriched transaction history with participant profiles.
/// </summary>
public record EnrichedTransactionHistoryResponse
{
    public string Address { get; init; } = string.Empty;
    public EnrichedTransaction[] Transactions { get; init; } = Array.Empty<EnrichedTransaction>();
    public int TotalCount { get; init; }
}

/// <summary>
/// Transaction with enriched participant information.
/// </summary>
public record EnrichedTransaction
{
    public long BlockNumber { get; init; }
    public long Timestamp { get; init; }
    public string TransactionHash { get; init; } = string.Empty;
    public int TransactionIndex { get; init; }
    public int LogIndex { get; init; }
    public JsonElement Event { get; init; }
    public Dictionary<string, ParticipantInfo> Participants { get; init; } = new();
}

/// <summary>
/// Participant information in a transaction.
/// </summary>
public record ParticipantInfo
{
    public AvatarInfo? AvatarInfo { get; init; }
    public JsonElement? Profile { get; init; }
}

/// <summary>
/// Unified profile search response.
/// </summary>
public record ProfileSearchResponse
{
    public string Query { get; init; } = string.Empty;
    public string SearchType { get; init; } = string.Empty; // "address" or "text"
    public JsonElement[] Results { get; init; } = Array.Empty<JsonElement>();
    public int TotalCount { get; init; }
}

/// <summary>
/// Paginated aggregated trust relations response with cursor-based navigation.
/// </summary>
public record PagedAggregatedTrustRelationsResponse
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("results")]
    public TrustRelationInfo[] Results { get; init; } = Array.Empty<TrustRelationInfo>();

    [JsonPropertyName("counts")]
    public TrustRelationCounts Counts { get; init; } = new();

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

/// <summary>
/// Counts of trust relations by type.
/// </summary>
public record TrustRelationCounts
{
    [JsonPropertyName("mutual")]
    public int Mutual { get; init; }

    [JsonPropertyName("trusts")]
    public int Trusts { get; init; }

    [JsonPropertyName("trustedBy")]
    public int TrustedBy { get; init; }

    [JsonPropertyName("total")]
    public int Total { get; init; }
}

/// <summary>
/// Paginated valid inviters response with cursor-based navigation.
/// </summary>
public record PagedValidInvitersResponse
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("results")]
    public InviterInfo[] Results { get; init; } = Array.Empty<InviterInfo>();

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

/// <summary>
/// Paginated profile search response with cursor-based navigation.
/// </summary>
public record PagedProfileSearchResponse
{
    [JsonPropertyName("query")]
    public string Query { get; init; } = string.Empty;

    [JsonPropertyName("searchType")]
    public string SearchType { get; init; } = string.Empty; // "address" or "text"

    [JsonPropertyName("results")]
    public JsonElement[] Results { get; init; } = Array.Empty<JsonElement>();

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

/// <summary>
/// Invitation origin information showing how a user joined Circles.
/// Different invitation mechanisms are unified into this response format.
/// </summary>
public record InvitationOriginResponse(
    /// <summary>The avatar address that was queried</summary>
    [property: JsonPropertyName("address")] string Address,

    /// <summary>
    /// The type of invitation: "v1_signup", "v2_standard", "v2_escrow", or "v2_at_scale"
    /// </summary>
    [property: JsonPropertyName("invitationType")] string InvitationType,

    /// <summary>The address of the inviter (null for v1_signup)</summary>
    [property: JsonPropertyName("inviter")] string? Inviter,

    /// <summary>The proxy inviter address (only set for v2_at_scale)</summary>
    [property: JsonPropertyName("proxyInviter")] string? ProxyInviter,

    /// <summary>The escrowed CRC amount in atto-circles (only set for v2_escrow)</summary>
    [property: JsonPropertyName("escrowAmount")] string? EscrowAmount,

    /// <summary>Block number when the invitation was recorded</summary>
    [property: JsonPropertyName("blockNumber")] long BlockNumber,

    /// <summary>Unix timestamp of the invitation</summary>
    [property: JsonPropertyName("timestamp")] long Timestamp,

    /// <summary>Transaction hash of the invitation event</summary>
    [property: JsonPropertyName("transactionHash")] string TransactionHash,

    /// <summary>Circles version: 1 for V1, 2 for V2</summary>
    [property: JsonPropertyName("version")] int Version
);

/// <summary>
/// Trust-based invitation information (someone who trusts the address and has sufficient balance).
/// </summary>
public record TrustInvitation
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = "trust";

    [JsonPropertyName("balance")]
    public string Balance { get; init; } = string.Empty;

    [JsonPropertyName("avatarInfo")]
    public AvatarInfo? AvatarInfo { get; init; }
}

/// <summary>
/// Escrow-based invitation information (CRC escrowed for the address).
/// </summary>
public record EscrowInvitation
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = "escrow";

    [JsonPropertyName("escrowedAmount")]
    public string EscrowedAmount { get; init; } = string.Empty;

    [JsonPropertyName("escrowDays")]
    public int EscrowDays { get; init; }

    [JsonPropertyName("blockNumber")]
    public long BlockNumber { get; init; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    [JsonPropertyName("avatarInfo")]
    public AvatarInfo? AvatarInfo { get; init; }
}

/// <summary>
/// At-scale invitation information (pre-created account for the address).
/// </summary>
public record AtScaleInvitation
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = "atScale";

    [JsonPropertyName("blockNumber")]
    public long BlockNumber { get; init; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    [JsonPropertyName("originInviter")]
    public string? OriginInviter { get; init; }
}

/// <summary>
/// Response containing all available invitations for an address from all sources.
/// </summary>
public record AllInvitationsResponse
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("trustInvitations")]
    public TrustInvitation[] TrustInvitations { get; init; } = Array.Empty<TrustInvitation>();

    [JsonPropertyName("escrowInvitations")]
    public EscrowInvitation[] EscrowInvitations { get; init; } = Array.Empty<EscrowInvitation>();

    [JsonPropertyName("atScaleInvitations")]
    public AtScaleInvitation[] AtScaleInvitations { get; init; } = Array.Empty<AtScaleInvitation>();
}

/// <summary>
/// Information about an account that was invited by a specific avatar.
/// </summary>
public record InvitedAccountInfo
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("blockNumber")]
    public long BlockNumber { get; init; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    [JsonPropertyName("avatarInfo")]
    public AvatarInfo? AvatarInfo { get; init; }
}

/// <summary>
/// Response for GetInvitationsFrom — accounts invited by a specific avatar.
/// </summary>
public record InvitationsFromResponse
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("results")]
    public InvitedAccountInfo[] Results { get; init; } = Array.Empty<InvitedAccountInfo>();
}
