using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Common.Dto;
using Circles.Index.Query.Dto;

namespace Circles.Rpc.Host.OpenRpc;

/// <summary>
/// Generates an OpenRPC 1.3.2 document by reflecting over ICirclesRpcModule.
/// The RPC method name → C# method mapping is maintained explicitly to match
/// the switch statement in Program.cs.
/// </summary>
public static class OpenRpcGenerator
{
    private const string AddrPattern = "^0x[0-9a-fA-F]{40}$";
    private const string AddrDesc = "Ethereum address, 0x-prefixed, 40 hex chars, checksummed or lowercase";
    private const string ExampleAddr1 = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
    private const string ExampleAddr2 = "0x42cEDde51198D1773590311E2A340DC06B24cB37";
    private const string ExampleGroup = "0x9b1BCe0E51F19B392e3F7a053e37930Bfa1e0B5A";
    private const string ExampleToken = "0xc5d024cb3218c4bfb3cdf1178e04c87742123708";

    // ─── Schema cache (must be declared before ParameterOverrides which calls ParamRef → EnsureSchemaByName) ──
    private static readonly Dictionary<string, JsonSchemaObject> SchemaCache = new();

    /// <summary>
    /// RPC method name → (C# method name, tag, summary, description).
    /// This is the single source of truth for the RPC → C# mapping.
    /// </summary>
    private static readonly (string RpcName, string CSharpName, string Tag, string Summary, string? Description)[] MethodMappings =
    [
        // ── Balance & Token Methods ──────────────────────────────────────────
        ("circles_getTotalBalance", "GetTotalBalance", "Balances",
            "Get total CRC balance for an address (V1)",
            "Returns the aggregated V1 Circles balance for an address. The balance can be returned in raw CRC wei or converted to TimeCircles format (which accounts for demurrage/inflation). Use `circlesV2_getTotalBalance` for V2 balances.\n\n**Common use case**: Display a user's total balance in a wallet UI. Call with `asTimeCircles: true` (default) for human-readable values."),

        ("circlesV2_getTotalBalance", "GetTotalBalance", "Balances",
            "Get total CRC balance for an address (V2)",
            "Returns the aggregated V2 Circles balance for an address. V2 uses ERC-1155 tokens with demurrage (value decreases ~7%/year). TimeCircles conversion adjusts for this decay.\n\n**Common use case**: Display V2 balance. Pair with `circles_getTokenBalances` to see individual token breakdowns.\n\n**Note**: V2 balances include both personal tokens and group tokens held by the address."),

        ("circles_getTokenBalances", "GetTokenBalances", "Balances",
            "Get all token balances for an address",
            "Returns every individual Circles token held by the address, including V1 CRC tokens, V2 personal tokens, V2 group tokens, and ERC-20 wrapped tokens. Each entry includes the token address, owner, type, version, balance amount, and whether it's wrapped/inflationary.\n\n**Common use case**: Build a token portfolio view. Use alongside `circles_getTokenInfo` to resolve token metadata.\n\n**Tip**: To get just the total, use `circles_getTotalBalance` or `circlesV2_getTotalBalance` instead."),

        ("circles_getTokenInfo", "GetTokenInfo", "Tokens",
            "Get information about a specific token",
            "Returns metadata for a single token: owner address, type (personal/group), version (1/2), whether it's an ERC-20 wrapper, inflationary, or a group token.\n\n**Common use case**: Resolve a token address encountered in a transfer event to understand what kind of token it is.\n\n**Returns null** if the token address is not known to the indexer."),

        ("circles_getTokenInfoBatch", "GetTokenInfoBatch", "Tokens",
            "Get information about multiple tokens",
            "Batch version of `circles_getTokenInfo`. Returns an array with the same length as the input; positions with unknown tokens contain null.\n\n**Common use case**: After fetching `circles_getTokenBalances`, resolve all token addresses in a single call instead of N individual calls."),

        // ── Avatar & Profile Methods ─────────────────────────────────────────
        ("circles_getAvatarInfo", "GetAvatarInfo", "Avatars",
            "Get avatar information for an address",
            "Returns registration info for a Circles avatar: version, type (Human/Organization/Group), associated token ID, V1 token (if migrated), IPFS profile CID, name, and symbol.\n\n**Common use case**: Check if an address is registered in Circles and what type of avatar it is. Essential before initiating trust or transfer operations.\n\n**Throws** if the address is not registered as a Circles avatar."),

        ("circles_getAvatarInfoBatch", "GetAvatarInfoBatch", "Avatars",
            "Get avatar information for multiple addresses",
            "Batch version of `circles_getAvatarInfo`. Efficient for resolving multiple avatars in a contact list or trust network view.\n\n**Common use case**: Given a list of trusted addresses, resolve all their avatar info in one call for rendering a contacts UI."),

        ("circles_getProfileCid", "GetProfileCid", "Profiles",
            "Get IPFS CID for an avatar's profile",
            "Returns the IPFS Content Identifier (CIDv0) pointing to the avatar's profile JSON. The profile is stored on IPFS and contains name, description, image URL, etc.\n\n**Common use case**: Check if an avatar has a profile set, or get the CID for direct IPFS retrieval. Use `circles_getProfileByAddress` instead if you want the full profile content."),

        ("circles_getProfileCidBatch", "GetProfileCidBatch", "Profiles",
            "Get IPFS CIDs for multiple avatars",
            "Batch version of `circles_getProfileCid`. Returns a map of address → CID (null if no profile).\n\n**Common use case**: Pre-fetch CIDs for a list of avatars before loading profiles."),

        ("circles_getProfileByCid", "GetProfileByCid", "Profiles",
            "Get profile content by IPFS CID",
            "Fetches and returns the profile JSON stored at the given IPFS CID. Results are cached server-side for performance.\n\n**Common use case**: Load a profile after obtaining its CID from `circles_getProfileCid` or `circles_getAvatarInfo`."),

        ("circles_getProfileByCidBatch", "GetProfileByCidBatch", "Profiles",
            "Get multiple profile contents by CIDs",
            "Batch version of `circles_getProfileByCid`. Returns array in same order as input CIDs."),

        ("circles_getProfileByAddress", "GetProfileByAddress", "Profiles",
            "Get profile by avatar address",
            "Convenience method that combines CID lookup + IPFS fetch in one call. Returns the full profile JSON enriched with avatar type and short name from V2 registrations.\n\n**Common use case**: Display a user profile card — this single call replaces `circles_getProfileCid` + `circles_getProfileByCid` + `circles_getAvatarInfo`.\n\n**Tip**: For rendering multiple profiles, use the batch variant or `circles_getProfileView` (which also includes balances and trust stats)."),

        ("circles_getProfileByAddressBatch", "GetProfileByAddressBatch", "Profiles",
            "Get profiles for multiple addresses",
            "Batch version of `circles_getProfileByAddress`. Each profile is enriched with avatar type and short name.\n\n**Common use case**: Render a list of user cards in a search result or trust list."),

        ("circles_searchProfiles", "SearchProfiles", "Profiles",
            "Full-text search across profiles",
            "Searches avatar profiles by name and description text. Supports up to 3 search tokens (each must be > 1 character). Can filter by avatar type.\n\n**Common use case**: User search bar — type a name, get matching profiles.\n\n**Tip**: For searching by address prefix OR name, use `circles_searchProfileByAddressOrName` which auto-detects the query type."),

        // ── Trust & Network Methods ──────────────────────────────────────────
        ("circles_getTrustRelations", "GetTrustRelations", "Trust",
            "Get trust relations for an address",
            "Returns two arrays: `trusts` (addresses this avatar trusts) and `trustedBy` (addresses that trust this avatar), each with the trust limit.\n\n**Common use case**: Display a user's trust network. The trust limit indicates the percentage of tokens accepted (100 = full trust, 0 = no trust).\n\n**Note**: Currently returns V1 trust relations. For V2, use `circles_getAggregatedTrustRelations`."),

        ("circles_getCommonTrust", "GetCommonTrust", "Trust",
            "Find common trust connections between two addresses",
            "Finds addresses that both users trust (or that trust both users), forming potential transfer intermediaries. For V2 humans, uses directional trust paths (outgoing → incoming). For V2 groups and V1, uses shared outgoing trusts.\n\n**Common use case**: Before a transfer, check if there's a common trust path. Also useful for 'mutual friends' UI features.\n\n**Tip**: Use together with `circlesV2_findPath` — if no common trust exists, a path likely won't be found either."),

        ("circles_getAggregatedTrustRelations", "GetAggregatedTrustRelations", "Trust",
            "Get trust relations grouped by type",
            "Returns trust relations categorized as 'mutuallyTrusts', 'trusts', or 'trustedBy', with expiry time and counterpart avatar type (Human/Group/Organization). SDK-compatible format.\n\n**Common use case**: Build a trust list UI with sections for mutual trusts vs one-way trusts. The `expiryTime` field is relevant for V2 trusts which can expire.\n\n**Tip**: For enriched results with profile data included, use `circles_getAggregatedTrustRelationsEnriched`."),

        ("circles_getNetworkSnapshot", "GetNetworkSnapshot", "Network",
            "Get a snapshot of the full trust network",
            "Returns the complete trust graph: all avatars, trust edges, and token balances. This is a large response (can be several MB) intended for offline analysis or graph visualization.\n\n**Warning**: This is an expensive call. Use sparingly and cache the result. For individual queries, use specific methods instead.\n\n**Common use case**: Graph visualization, analytics dashboards, or pre-loading data for offline pathfinding."),

        // ── Group Methods ────────────────────────────────────────────────────
        ("circles_findGroups", "FindGroups", "Groups",
            "Find groups with optional filters",
            "Discovers Circles groups with optional filtering by name prefix, symbol prefix, or owner addresses. Returns paginated results.\n\n**Common use case**: Group discovery UI — browse available groups to join.\n\n**Filters** (combine for AND logic):\n- `nameStartsWith`: Filter by group name prefix\n- `symbolStartsWith`: Filter by token symbol prefix\n- `ownerIn`: Only groups owned by specific addresses"),

        ("circles_getGroupMembers", "GetGroupMembers", "Groups",
            "Get members of a specific group",
            "Returns addresses that are members (trusted by) a specific group, with cursor-based pagination.\n\n**Common use case**: Display group member list. Members are avatars that the group trusts, meaning their personal tokens can be used to mint the group token."),

        ("circles_getGroupMemberships", "GetGroupMemberships", "Groups",
            "Get groups an avatar is a member of",
            "Inverse of `circles_getGroupMembers` — returns all groups that trust a given avatar address.\n\n**Common use case**: Show which groups a user belongs to in their profile view."),

        // ── Transaction Methods ──────────────────────────────────────────────
        ("circles_getTransactionHistory", "GetTransactionHistory", "Transactions",
            "Get transaction history for an avatar",
            "Returns Circles transfers involving an avatar (as sender or receiver) with cursor-based pagination. Each entry includes from/to addresses, token, amount in multiple formats (CRC wei, TimeCircles, static), block number, and timestamp.\n\n**Common use case**: Transaction history feed in a wallet.\n\n**Params**:\n- `version`: null = both V1+V2, 1 = V1 only, 2 = V2 only\n- `excludeIntermediary`: When true (default), uses TransferSummary which collapses multi-hop transfers into source→destination pairs, hiding intermediary routing hops.\n\n**Tip**: For transaction history with participant profiles pre-loaded, use `circles_getTransactionHistoryEnriched`."),

        ("circles_getTransferData", "GetTransferData", "Transactions",
            "Get ERC-1155 transfer calldata for an address",
            "Returns the raw calldata bytes for ERC-1155 transfers involving an address. Can filter by direction (sent/received) and counterparty.\n\n**Common use case**: Inspecting the low-level transfer data for debugging or advanced analysis.\n\n**Params used together**:\n- `direction` + `counterparty`: e.g., direction='sent', counterparty='0x...' to see all transfers sent to a specific address\n- `fromBlock` + `toBlock`: Time-range filtering\n- Use `limit` + `cursor` for pagination through large result sets"),

        ("circles_getTokenHolders", "GetTokenHolders", "Tokens",
            "Get all holders of a specific token",
            "Returns all addresses holding a specific token with their balances. Supports cursor-based pagination.\n\n**Common use case**: Token distribution analysis, or finding who holds a specific group's token.\n\n**Tip**: The `tokenAddress` is the token contract address (V1) or token ID (V2 ERC-1155)."),

        // ── Pathfinder ───────────────────────────────────────────────────────
        ("circlesV2_findPath", "FindPathV2", "Pathfinder",
            "Find a transitive transfer path through the trust network",
            "Computes a multi-hop transfer path from source to sink through the Circles trust network. Uses the Pathfinder service (Google OR-Tools max-flow solver) to find the optimal set of token transfers.\n\n**This is the primary method for executing Circles transfers.** The response contains the exact transfer steps to submit on-chain.\n\n**Key parameters**:\n- `source`/`sink`: Sender and receiver addresses\n- `targetFlow`: Amount to transfer in CRC wei (use max uint256 for 'send as much as possible')\n- `fromTokens`/`toTokens`: Restrict which tokens can be used at source/sink\n- `excludedFromTokens`/`excludedToTokens`: Exclude specific tokens\n- `withWrap`: Include ERC-20 wrapper paths\n- `quantizedMode`: Enforce 96 CRC quantization (for invitations)\n- `maxTransfers`: Limit number of transfer steps\n- `simulatedBalances`: Test with hypothetical balances\n- `debugShowIntermediateSteps`: Include all transformation stages in response\n\n**Common use cases**:\n1. **Simple transfer**: Set source, sink, targetFlow\n2. **Maximum possible transfer**: Set targetFlow to max uint256\n3. **Invitation flow**: Set quantizedMode=true, targetFlow = N * 96 CRC\n4. **Debugging**: Set debugShowIntermediateSteps=true to see rawPaths → collapsed → routerInserted → sorted stages"),

        // ── Events & Query ───────────────────────────────────────────────────
        ("circles_events", "GetEvents", "Events",
            "Query indexed blockchain events with filters",
            "Returns Circles protocol events (trust changes, transfers, registrations, group operations, etc.) with flexible filtering. Supports address filtering, block range, event type filtering, and advanced filter predicates for complex queries.\n\n**Common use case**: Real-time feed of protocol activity, or monitoring specific addresses for trust/transfer events.\n\n**Event types include**: CrcV1_Trust, CrcV1_Transfer, CrcV2_Trust, CrcV2_TransferSingle, CrcV2_TransferBatch, CrcV2_RegisterHuman, CrcV2_RegisterGroup, CrcV2_RegisterOrganization, and many more.\n\n**Params used together**:\n- `address` + `eventTypes`: Monitor specific events for an address\n- `fromBlock` + `toBlock`: Time-range queries\n- `filterPredicates`: Advanced SQL-like filtering (see FilterPredicateDto schema)\n- `sortAscending` + `limit` + `cursor`: Pagination control"),

        ("circles_query", "Query", "Query",
            "Execute a structured database query (non-paginated)",
            "Low-level query interface for direct access to indexed data tables. Uses a structured DTO (SelectDto) to specify namespace, table, columns, filters, ordering, and limits. Returns `{columns, rows}` format.\n\n**Use this for**: Custom queries not covered by specific methods. Discover available tables with `circles_tables`.\n\n**Warning**: This is the non-paginated version. For large result sets, use `circles_paginated_query` instead."),

        ("circles_paginated_query", "Query", "Query",
            "Execute a structured database query with cursor pagination",
            "Server-side cursor pagination version of `circles_query`. Returns `{columns, rows, hasMore, nextCursor}`. Pass `nextCursor` as the `cursor` parameter in subsequent calls.\n\n**Use this for**: Iterating through large query results without loading everything into memory.\n\n**Tip**: Pair with `circles_tables` to discover available tables and their columns."),

        // ── System ───────────────────────────────────────────────────────────
        ("circles_health", "GetHealth", "System",
            "Check service health and sync status",
            "Returns health information including database connectivity, blockchain sync status, current block number, and whether the service is fully caught up.\n\n**Common use case**: Health monitoring, load balancer health checks, or verifying the indexer is synced before querying."),

        ("circles_tables", "GetTables", "System",
            "List available database tables and schemas",
            "Returns all available table namespaces and their tables with column definitions. Essential reference for building `circles_query` / `circles_paginated_query` requests.\n\n**Common use case**: Discover what data is available for querying, including column names and types."),

        // ── SDK Enablement Methods ───────────────────────────────────────────
        ("circles_getProfileView", "GetProfileView", "SDK",
            "Get a complete profile view (avatar + profile + trust stats + balances)",
            "Single-call replacement for 6-7 separate RPC calls when displaying a user profile. Returns avatar info, IPFS profile, trust statistics (trusting count, trusted-by count, mutual count), and balance summary.\n\n**Common use case**: Profile page rendering. Instead of calling getAvatarInfo + getProfileByAddress + getTrustRelations + getTotalBalance separately, use this single method.\n\n**Performance**: Executes all sub-queries in parallel server-side, significantly faster than sequential client-side calls."),

        ("circles_getTrustNetworkSummary", "GetTrustNetworkSummary", "SDK",
            "Get aggregated trust network statistics",
            "Returns trust network metrics: total trusting, trusted-by, mutual trust count, and optional network reach at configurable depth.\n\n**Common use case**: Dashboard widgets showing network size, or determining how well-connected an avatar is.\n\n**Param**: `maxDepth` controls how many hops to traverse for network reach calculation (higher = more expensive)."),

        ("circles_getAggregatedTrustRelationsEnriched", "GetAggregatedTrustRelationsEnriched", "SDK",
            "Get trust relations with enriched avatar info and profiles",
            "Like `circles_getAggregatedTrustRelations` but each trust relation includes the counterpart's full profile (name, image, avatar type). Paginated with cursor.\n\n**Common use case**: Render a trust list where each entry shows the avatar's name and profile picture without additional API calls.\n\n**Tip**: Use `limit` to control page size (default 50, max 200)."),

        ("circles_getValidInviters", "GetValidInviters", "SDK",
            "Get addresses that trust the given address AND have sufficient balance to invite",
            "Finds potential sponsors for a new user: addresses that trust the target AND have enough CRC balance to fund an invitation.\n\n**Common use case**: Invitation flow — show the user who can invite them, sorted by balance.\n\n**Params used together**:\n- `minimumBalance`: Minimum CRC balance required (e.g., '96000000000000000000' for 96 CRC = 1 invitation unit)\n- `limit` + `cursor`: Pagination"),

        ("circles_getTransactionHistoryEnriched", "GetTransactionHistoryEnriched", "SDK",
            "Get transaction history with enriched participant profiles",
            "Like `circles_getTransactionHistory` but each transaction includes full profiles for all participants (sender, receiver). Eliminates the need for N additional profile lookups.\n\n**Common use case**: Transaction feed with avatars/names displayed inline.\n\n**Params used together**:\n- `fromBlock` (required): Starting block number\n- `toBlock` (optional): Ending block, null = latest\n- `version`: null = V2 only (default for backward compat), 1 = V1, 2 = V2\n- `excludeIntermediary`: true (default) collapses multi-hop transfers\n- `limit` + `cursor`: Pagination"),

        ("circles_searchProfileByAddressOrName", "SearchProfileByAddressOrName", "SDK",
            "Unified search by address prefix or name/description text",
            "Auto-detects search type: if query starts with '0x', searches by address prefix; otherwise, performs full-text search on name/description. Returns paginated results with full profiles.\n\n**Common use case**: Universal search bar — users can paste an address or type a name.\n\n**Tip**: Can filter by avatar types (Human, Group, Organization) using the `types` parameter."),

        ("circles_getInvitationOrigin", "GetInvitationOrigin", "SDK",
            "Get how an address was invited to Circles",
            "Reconstructs the invitation chain for an address. Returns the invitation type (V1 Signup, V2 Standard, V2 Escrow, V2 At Scale), the inviter address, and related transaction details.\n\n**Common use case**: Show 'Invited by...' in a profile view, or tracing invitation chains for analytics.\n\n**Returns null** if the address is not registered."),

        ("circles_getAllInvitations", "GetAllInvitations", "SDK",
            "Get all available invitations from all sources",
            "Aggregates invitations from all mechanisms: trust-based (addresses with sufficient balance), escrow-based (CRC escrowed for this address), and at-scale (pre-created unclaimed accounts). Single call replaces 3 separate invitation queries.\n\n**Common use case**: 'Accept invitation' screen showing all ways a user can join Circles.\n\n**Params used together**: `minimumBalance` filters trust-based invitations by the inviter's CRC balance."),

        ("circles_getTrustInvitations", "GetTrustInvitations", "SDK",
            "Get trust-based invitations",
            "Subset of `circles_getAllInvitations` — returns only trust-based invitations (addresses that trust the target and have sufficient balance).\n\n**Common use case**: When you only need trust-based invitation options."),

        ("circles_getEscrowInvitations", "GetEscrowInvitations", "SDK",
            "Get escrow-based invitations",
            "Returns CRC amounts escrowed for the target address. Filters out already redeemed, revoked, and refunded escrows server-side.\n\n**Common use case**: Check if someone has pre-funded an invitation for this address."),

        ("circles_getAtScaleInvitations", "GetAtScaleInvitations", "SDK",
            "Get at-scale invitations",
            "Returns pre-created accounts that haven't been claimed yet, associated with the target address.\n\n**Common use case**: Enterprise/campaign onboarding where accounts are pre-provisioned."),

        ("circles_getInvitationsFrom", "GetInvitationsFrom", "SDK",
            "Get accounts invited by a specific avatar",
            "Returns accounts that were invited by the given avatar. When `accepted=true`, shows registered accounts; when `accepted=false`, shows addresses this avatar trusts that are NOT yet registered (pending invitations).\n\n**Common use case**: 'My invitations' section showing who you've invited and whether they've joined.\n\n**Params used together**: `accepted=false` to see pending invitations, `accepted=true` to see confirmed ones."),
    ];

    /// <summary>
    /// Parameter overrides for methods where the RPC parameter extraction
    /// differs from the C# method signature (e.g., custom parsing in handlers).
    /// Key: RPC method name → explicit param list.
    /// </summary>
    private static readonly Dictionary<string, OpenRpcParam[]> ParameterOverrides = new()
    {
        ["circles_getTotalBalance"] =
        [
            Param("address", true, "string",
                "The Ethereum address to query the balance for. Must be a registered Circles V1 avatar.",
                pattern: AddrPattern),
            Param("asTimeCircles", false, "boolean",
                "When true (default), converts the balance to TimeCircles format which accounts for CRC inflation. When false, returns the raw CRC wei balance. TimeCircles is the human-readable format used in the Circles UI.")
        ],
        ["circlesV2_getTotalBalance"] =
        [
            Param("address", true, "string",
                "The Ethereum address to query the V2 balance for. Must be a registered Circles V2 avatar.",
                pattern: AddrPattern),
            Param("asTimeCircles", false, "boolean",
                "When true (default), converts the balance to TimeCircles format which accounts for V2 demurrage (~7%/year decay). When false, returns the raw CRC wei balance with demurrage already applied.")
        ],
        ["circles_getTokenBalances"] =
        [
            Param("address", true, "string",
                "The Ethereum address to query token balances for. Returns all V1 and V2 Circles tokens held by this address, including personal tokens, group tokens, and ERC-20 wrappers.",
                pattern: AddrPattern)
        ],
        ["circles_getTokenInfo"] =
        [
            Param("tokenAddress", true, "string",
                "The token contract address (V1 CRC token) or token ID (V2 ERC-1155). Returns metadata about the token including owner, type, version, and flags.",
                pattern: AddrPattern)
        ],
        ["circles_getTokenInfoBatch"] =
        [
            ParamArray("tokenAddresses", true, "string",
                "Array of token addresses to look up. Returns an array of the same length with null entries for unknown tokens. Max recommended batch size: 100.")
        ],
        ["circles_getAvatarInfo"] =
        [
            Param("address", true, "string",
                "The address to look up. Returns registration info including avatar type (Human/Organization/Group), V1/V2 status, token ID, IPFS profile CID, and on-chain name/symbol.",
                pattern: AddrPattern)
        ],
        ["circles_getAvatarInfoBatch"] =
        [
            ParamArray("addresses", true, "string",
                "Array of addresses to look up. Returns avatar info for each; throws for unregistered addresses. Max recommended batch size: 100.")
        ],
        ["circles_getProfileCid"] =
        [
            Param("address", true, "string",
                "The avatar address to get the profile CID for. The CID points to an IPFS document containing the avatar's profile (name, description, image URL).",
                pattern: AddrPattern)
        ],
        ["circles_getProfileCidBatch"] =
        [
            ParamArray("addresses", true, "string",
                "Array of avatar addresses. Returns a map of address → CIDv0 string (null if no profile set).")
        ],
        ["circles_getProfileByCid"] =
        [
            Param("cid", true, "string",
                "IPFS Content Identifier (CIDv0 format, e.g., 'QmYwAPJzv5CZsnA625s3Xf2nemtYgPpHdWEz79ojWnPbdG'). The server caches IPFS content for performance.")
        ],
        ["circles_getProfileByCidBatch"] =
        [
            ParamArray("cids", true, "string",
                "Array of IPFS CIDs to fetch. Returns profile JSON objects in the same order, with null for CIDs that couldn't be resolved.")
        ],
        ["circles_getProfileByAddress"] =
        [
            Param("address", true, "string",
                "Avatar address. Combines CID lookup + IPFS fetch + avatar enrichment in one call. The returned profile JSON includes injected 'avatarType' and 'name' fields from V2 registration data.",
                pattern: AddrPattern)
        ],
        ["circles_getProfileByAddressBatch"] =
        [
            ParamArray("addresses", true, "string",
                "Array of avatar addresses. Each profile is enriched with avatarType and name. More efficient than N individual calls. Max recommended batch size: 50.")
        ],
        ["circles_searchProfiles"] =
        [
            Param("text", true, "string",
                "Search query text. Split into tokens by whitespace; each token must be > 1 character; max 3 tokens. Searches across profile name and description fields using full-text search."),
            Param("limit", false, "integer",
                "Maximum number of results to return. Default: 20, maximum: 100. Use with offset for pagination."),
            Param("offset", false, "integer",
                "Number of results to skip. Use with limit for offset-based pagination (e.g., offset=20, limit=20 for page 2)."),
            ParamArray("types", false, "string",
                "Filter results by avatar type. Valid values: 'CrcV2_RegisterHuman', 'CrcV2_RegisterGroup', 'CrcV2_RegisterOrganization'. Omit to search all types.")
        ],
        ["circles_getTrustRelations"] =
        [
            Param("address", true, "string",
                "The avatar address to query trust relations for. Returns bidirectional trust data: who this avatar trusts (outgoing) and who trusts this avatar (incoming).",
                pattern: AddrPattern)
        ],
        ["circles_getCommonTrust"] =
        [
            Param("address1", true, "string",
                "First address in the pair. For V2 humans, this is the 'outgoing trust' side (address1's trusts → common → address2's trustedBy).",
                pattern: AddrPattern),
            Param("address2", true, "string",
                "Second address in the pair. For V2 humans, this is the 'incoming trust' side.",
                pattern: AddrPattern),
            Param("version", false, "integer",
                "Filter by protocol version: 1 = V1 only, 2 = V2 only, null/omit = both versions. V1 and V2 trust graphs are independent.")
        ],
        ["circles_events"] =
        [
            Param("address", false, "string",
                "Filter events by this address (as sender, receiver, or involved party). Null = all addresses. Case-insensitive.",
                pattern: AddrPattern),
            Param("fromBlock", false, "integer",
                "Starting block number (inclusive). Combine with toBlock for time-range queries. Gnosis Chain produces ~1 block per 5 seconds."),
            Param("toBlock", false, "integer",
                "Ending block number (inclusive). Null = up to the latest indexed block."),
            ParamArray("eventTypes", false, "string",
                "Filter by event types. Examples: ['CrcV2_TransferSingle','CrcV2_Trust'], ['CrcV1_Transfer'], ['CrcV2_RegisterHuman']. Null = all event types. Use circles_tables to discover all event types."),
            ParamRef("filterPredicates", false, "FilterPredicateDto",
                "Advanced filter predicates for SQL-like filtering. Supports AND/OR conjunctions, column-level filters with operators (equals, greaterThan, lessThan, like, in). See FilterPredicateDto schema."),
            Param("sortAscending", false, "boolean",
                "Sort order by block number. Default: false (newest first). Set true for chronological order."),
            Param("limit", false, "integer",
                "Maximum events to return per page. Default: 100, maximum: 1000."),
            Param("cursor", false, "string",
                "Opaque cursor from a previous response's nextCursor field for pagination. Do not construct manually.")
        ],
        ["circles_query"] =
        [
            ParamRef("query", true, "SelectDto",
                "Structured query definition specifying namespace, table, columns, filter conditions, ordering, and limit. Use circles_tables to discover available namespaces and tables."),
            Param("cursor", false, "string",
                "Cursor from a previous response for pagination. Only works with circles_paginated_query.")
        ],
        ["circles_paginated_query"] =
        [
            ParamRef("query", true, "SelectDto",
                "Structured query definition. Same format as circles_query but returns paginated results with cursor-based navigation."),
            Param("cursor", false, "string",
                "Opaque cursor from the previous response's nextCursor field. Pass this to get the next page of results.")
        ],
        ["circles_getAggregatedTrustRelations"] =
        [
            Param("avatar", true, "string",
                "Avatar address to query aggregated trust relations for. Returns trust relations grouped by counterpart with relation type (mutuallyTrusts/trusts/trustedBy) and expiry times.",
                pattern: AddrPattern)
        ],
        ["circles_getNetworkSnapshot"] =
        [
            // No parameters — returns the entire trust network snapshot from the Pathfinder
        ],
        ["circles_findGroups"] =
        [
            Param("limit", false, "integer",
                "Maximum number of groups to return. Default: 50."),
            Param("queryParams", false, "object",
                "Optional filter object with fields: nameStartsWith (string), symbolStartsWith (string), ownerIn (string[] of addresses)."),
            Param("cursor", false, "string",
                "Cursor from a previous response's nextCursor field for pagination.")
        ],
        ["circles_getGroupMembers"] =
        [
            Param("groupAddress", true, "string",
                "The group address to query members for. Returns all avatars that are members of this group with membership metadata.",
                pattern: AddrPattern),
            Param("limit", false, "integer",
                "Maximum number of members to return. Default: 100, max: 200."),
            Param("cursor", false, "string",
                "Cursor from a previous response's nextCursor field for pagination.")
        ],
        ["circles_getGroupMemberships"] =
        [
            Param("memberAddress", true, "string",
                "Avatar address to query group memberships for. Returns all groups this avatar belongs to (inverse of getGroupMembers).",
                pattern: AddrPattern),
            Param("limit", false, "integer",
                "Maximum number of memberships to return. Default: 50, max: 200."),
            Param("cursor", false, "string",
                "Cursor from a previous response's nextCursor field for pagination.")
        ],
        ["circles_getTransactionHistory"] =
        [
            Param("avatarAddress", true, "string",
                "Avatar address to query transaction history for. Returns transfers where this avatar is sender or receiver.",
                pattern: AddrPattern),
            Param("limit", false, "integer",
                "Maximum number of transactions to return. Default: 50, max: 200."),
            Param("cursor", false, "string",
                "Cursor from a previous response's nextCursor field for pagination."),
            Param("version", false, "integer",
                "Filter by protocol version: 1 = V1 only, 2 = V2 only, null = both V1+V2."),
            Param("excludeIntermediary", false, "boolean",
                "When true (default), uses TransferSummary which excludes intermediary hop transfers. When false, includes all individual hops.")
        ],
        ["circles_getTransferData"] =
        [
            Param("address", true, "string",
                "Primary address to filter transfer data for. Returns ERC-1155 transfer calldata bytes.",
                pattern: AddrPattern),
            Param("direction", false, "string",
                "Filter by direction: 'sent' (from=address), 'received' (to=address), or null/omit for both."),
            Param("counterparty", false, "string",
                "If set, additionally filters by this specific counterparty address.",
                pattern: AddrPattern),
            Param("fromBlock", false, "integer",
                "Start block number (inclusive). Null for no lower bound."),
            Param("toBlock", false, "integer",
                "End block number (inclusive). Null for no upper bound."),
            Param("limit", false, "integer",
                "Maximum results. Default: 50, max: 1000."),
            Param("cursor", false, "string",
                "Cursor from a previous response's nextCursor field for pagination.")
        ],
        ["circles_getTokenHolders"] =
        [
            Param("tokenAddress", true, "string",
                "Token address to query holders for. Returns all addresses holding this token with their balances.",
                pattern: AddrPattern),
            Param("limit", false, "integer",
                "Maximum number of holders to return. Default: 100, max: 200."),
            Param("cursor", false, "string",
                "Cursor from a previous response's nextCursor field for pagination (account address).")
        ],
        ["circles_health"] =
        [
            // No parameters — returns database connectivity and sync status
        ],
        ["circles_tables"] =
        [
            // No parameters — returns all available database tables/schemas for use with circles_query
        ],
        ["circles_getProfileView"] =
        [
            Param("address", true, "string",
                "Avatar address to get the consolidated profile view for. Returns avatar info, IPFS profile, trust stats, and V1/V2 balances in a single call (replaces 6-7 separate RPC calls).",
                pattern: AddrPattern)
        ],
        ["circles_getTrustNetworkSummary"] =
        [
            Param("address", true, "string",
                "Avatar address to query trust network summary for. Returns trust counts, mutual trust count, and network reach statistics.",
                pattern: AddrPattern),
            Param("maxDepth", false, "integer",
                "Maximum depth for network traversal. Limits how far the trust graph is explored. Optional.")
        ],
        ["circles_getAggregatedTrustRelationsEnriched"] =
        [
            Param("address", true, "string",
                "Avatar address to query enriched trust relations for. Returns trust relations categorized by type (mutual, one-way) with enriched avatar info.",
                pattern: AddrPattern),
            Param("limit", false, "integer",
                "Maximum results per page. Default: 50, max: 200."),
            Param("cursor", false, "string",
                "Cursor from a previous response's nextCursor field for pagination.")
        ],
        ["circles_getValidInviters"] =
        [
            Param("address", true, "string",
                "Avatar address to find valid inviters for. Returns addresses that trust this user AND have sufficient balance to sponsor an invitation.",
                pattern: AddrPattern),
            Param("minimumBalance", false, "string",
                "Minimum CRC balance required for an inviter (as string). Default: 96 CRC (1 invitation unit). Example: '96000000000000000000'."),
            Param("limit", false, "integer",
                "Maximum results per page. Default: 50, max: 200."),
            Param("cursor", false, "string",
                "Cursor from a previous response's nextCursor field for pagination.")
        ],
        ["circles_getTransactionHistoryEnriched"] =
        [
            Param("address", true, "string",
                "Avatar address to query enriched transaction history for. Returns transactions with participant profiles and metadata (replaces circles_events + multiple getProfileByAddress calls).",
                pattern: AddrPattern),
            Param("fromBlock", true, "integer",
                "Starting block number (inclusive). Required."),
            Param("toBlock", false, "integer",
                "Ending block number (inclusive). Null = up to the latest indexed block."),
            Param("limit", false, "integer",
                "Maximum transactions to return. Default: 20."),
            Param("cursor", false, "string",
                "Cursor from a previous response's nextCursor field for pagination."),
            Param("version", false, "integer",
                "Filter by version: null = V2 only (backward compat), 1 = V1 only, 2 = V2 only."),
            Param("excludeIntermediary", false, "boolean",
                "When true (default), uses TransferSummary which excludes intermediary hop transfers.")
        ],
        ["circles_searchProfileByAddressOrName"] =
        [
            Param("query", true, "string",
                "Search query. If starts with '0x', searches by address prefix. Otherwise performs full-text search across profile name and description."),
            Param("limit", false, "integer",
                "Maximum results per page. Default: 20, max: 100."),
            Param("cursor", false, "string",
                "Cursor from a previous response's nextCursor field for pagination."),
            ParamArray("types", false, "string",
                "Filter by avatar types: 'CrcV2_RegisterHuman', 'CrcV2_RegisterGroup', 'CrcV2_RegisterOrganization'. Omit for all types.")
        ],
        ["circles_getInvitationOrigin"] =
        [
            Param("address", true, "string",
                "Avatar address to look up the invitation origin for. Returns how this address was invited (V1 Signup, V2 Standard, V2 Escrow, V2 At Scale) with inviter details.",
                pattern: AddrPattern)
        ],
        ["circles_getAllInvitations"] =
        [
            Param("address", true, "string",
                "Avatar address to query all available invitations for. Returns trust-based, escrow-based, and at-scale invitations in a single response.",
                pattern: AddrPattern),
            Param("minimumBalance", false, "string",
                "Minimum CRC balance required for trust-based invitations (as string). Example: '96000000000000000000' = 96 CRC.")
        ],
        ["circles_getTrustInvitations"] =
        [
            Param("address", true, "string",
                "Avatar address to query trust-based invitations for. Returns addresses that trust this user and have sufficient balance. Subset of getAllInvitations.",
                pattern: AddrPattern),
            Param("minimumBalance", false, "string",
                "Minimum CRC balance required (as string). Default: 96 CRC. Example: '96000000000000000000'.")
        ],
        ["circles_getEscrowInvitations"] =
        [
            Param("address", true, "string",
                "Avatar address to query escrow-based invitations for. Returns CRC escrowed for this address. Filters out redeemed, revoked, and refunded escrows.",
                pattern: AddrPattern)
        ],
        ["circles_getAtScaleInvitations"] =
        [
            Param("address", true, "string",
                "Avatar address to query at-scale invitations for. Returns pre-created accounts that haven't been claimed yet.",
                pattern: AddrPattern)
        ],
        ["circles_getInvitationsFrom"] =
        [
            Param("address", true, "string",
                "Inviter avatar address. When accepted=true: returns accounts that registered using this avatar as inviter. When accepted=false: returns addresses this avatar trusts that are NOT yet registered.",
                pattern: AddrPattern),
            Param("accepted", false, "boolean",
                "When true, returns registered (accepted) invitations. When false (default), returns pending (not yet registered) invitations.")
        ],
        ["circlesV2_findPath"] =
        [
            ParamRef("flowRequest", true, "FlowRequest",
                "Path computation request. Contains source (sender), sink (receiver), targetFlow (amount in CRC wei), and optional filters for tokens, wrapping, quantization, simulation, and debugging. See FlowRequest schema for all fields.")
        ],
    };

    /// <summary>
    /// Example pairings for methods. Each entry shows a realistic request and expected response shape.
    /// </summary>
    private static readonly Dictionary<string, List<OpenRpcExamplePairing>> MethodExamples = new()
    {
        ["circles_getTotalBalance"] =
        [
            new()
            {
                Name = "Get V1 balance in TimeCircles",
                Description = "Query a user's total V1 CRC balance, returned in TimeCircles format",
                Params =
                [
                    new() { Name = "address", Value = ExampleAddr1 },
                    new() { Name = "asTimeCircles", Value = true }
                ],
                Result = new()
                {
                    Name = "TotalBalanceResponse",
                    Value = new { totalBalance = "142.5678", version = 1, asTimeCircles = true }
                }
            }
        ],
        ["circlesV2_getTotalBalance"] =
        [
            new()
            {
                Name = "Get V2 balance in raw CRC",
                Description = "Query a user's total V2 CRC balance in raw wei",
                Params =
                [
                    new() { Name = "address", Value = ExampleAddr1 },
                    new() { Name = "asTimeCircles", Value = false }
                ],
                Result = new()
                {
                    Name = "TotalBalanceResponse",
                    Value = new { totalBalance = "142567800000000000000", version = 2, asTimeCircles = false }
                }
            }
        ],
        ["circles_getTokenBalances"] =
        [
            new()
            {
                Name = "List all token balances",
                Description = "Get every Circles token held by an address",
                Params = [new() { Name = "address", Value = ExampleAddr1 }],
            }
        ],
        ["circles_getAvatarInfo"] =
        [
            new()
            {
                Name = "Check if an address is registered",
                Description = "Look up avatar registration info — throws if not registered",
                Params = [new() { Name = "address", Value = ExampleAddr1 }],
                Result = new()
                {
                    Name = "AvatarInfo",
                    Value = new
                    {
                        version = 2, type = "CrcV2_RegisterHuman",
                        avatar = ExampleAddr1,
                        tokenId = ExampleToken,
                        hasV1 = true, v1Token = ExampleToken,
                        cidV0Digest = "0x1234...",
                        cidV0 = "QmYwAPJzv5CZsnA625s3Xf2nemtYgPpHdWEz79ojWnPbdG",
                        isHuman = true, name = "Alice", symbol = "ALICE"
                    }
                }
            }
        ],
        ["circles_searchProfiles"] =
        [
            new()
            {
                Name = "Search by name",
                Description = "Find profiles matching 'alice' with max 10 results",
                Params =
                [
                    new() { Name = "text", Value = "alice" },
                    new() { Name = "limit", Value = 10 },
                    new() { Name = "offset", Value = 0 },
                ],
            },
            new()
            {
                Name = "Search groups only",
                Description = "Find group profiles matching 'community'",
                Params =
                [
                    new() { Name = "text", Value = "community" },
                    new() { Name = "limit", Value = 20 },
                    new() { Name = "offset", Value = 0 },
                    new() { Name = "types", Value = new[] { "CrcV2_RegisterGroup" } },
                ],
            }
        ],
        ["circles_getCommonTrust"] =
        [
            new()
            {
                Name = "Find mutual connections (V2)",
                Description = "Find addresses trusted by both parties in the V2 network",
                Params =
                [
                    new() { Name = "address1", Value = ExampleAddr1 },
                    new() { Name = "address2", Value = ExampleAddr2 },
                    new() { Name = "version", Value = 2 },
                ],
            }
        ],
        ["circles_findGroups"] =
        [
            new()
            {
                Name = "Browse all groups",
                Description = "List first 50 groups with default sorting",
                Params =
                [
                    new() { Name = "limit", Value = 50 },
                ],
            },
            new()
            {
                Name = "Search groups by name prefix",
                Description = "Find groups whose name starts with 'Berlin'",
                Params =
                [
                    new() { Name = "limit", Value = 20 },
                    new() { Name = "queryParams", Value = new { nameStartsWith = "Berlin" } },
                ],
            }
        ],
        ["circles_getGroupMembers"] =
        [
            new()
            {
                Name = "List group members",
                Description = "Get the first 100 members of a group",
                Params =
                [
                    new() { Name = "groupAddress", Value = ExampleGroup },
                    new() { Name = "limit", Value = 100 },
                ],
            }
        ],
        ["circles_events"] =
        [
            new()
            {
                Name = "Recent transfers for an address",
                Description = "Get the 50 most recent V2 transfers involving an address",
                Params =
                [
                    new() { Name = "address", Value = ExampleAddr1 },
                    new() { Name = "eventTypes", Value = new[] { "CrcV2_TransferSingle" } },
                    new() { Name = "sortAscending", Value = false },
                    new() { Name = "limit", Value = 50 },
                ],
            },
            new()
            {
                Name = "New registrations in a block range",
                Description = "Find all V2 human registrations between two blocks",
                Params =
                [
                    new() { Name = "fromBlock", Value = 35000000 },
                    new() { Name = "toBlock", Value = 35100000 },
                    new() { Name = "eventTypes", Value = new[] { "CrcV2_RegisterHuman" } },
                ],
            }
        ],
        ["circlesV2_findPath"] =
        [
            new()
            {
                Name = "Simple transfer",
                Description = "Find a path to transfer 10 CRC from sender to receiver",
                Params =
                [
                    new()
                    {
                        Name = "flowRequest", Value = new
                        {
                            source = ExampleAddr1,
                            sink = ExampleAddr2,
                            targetFlow = "10000000000000000000"
                        }
                    }
                ],
            },
            new()
            {
                Name = "Maximum possible transfer",
                Description = "Discover the maximum amount transferable between two addresses",
                Params =
                [
                    new()
                    {
                        Name = "flowRequest", Value = new
                        {
                            source = ExampleAddr1,
                            sink = ExampleAddr2,
                            targetFlow = "115792089237316195423570985008687907853269984665640564039457584007913129639935"
                        }
                    }
                ],
            },
            new()
            {
                Name = "Quantized invitation transfer",
                Description = "Transfer exactly 2 invitation units (2 x 96 CRC = 192 CRC) with quantization",
                Params =
                [
                    new()
                    {
                        Name = "flowRequest", Value = new
                        {
                            source = ExampleAddr1,
                            sink = ExampleAddr2,
                            targetFlow = "192000000000000000000",
                            quantizedMode = true
                        }
                    }
                ],
            },
            new()
            {
                Name = "Debug transfer path",
                Description = "Find a path with all intermediate transformation stages visible",
                Params =
                [
                    new()
                    {
                        Name = "flowRequest", Value = new
                        {
                            source = ExampleAddr1,
                            sink = ExampleAddr2,
                            targetFlow = "10000000000000000000",
                            debugShowIntermediateSteps = true,
                            maxTransfers = 5
                        }
                    }
                ],
            }
        ],
        ["circles_getProfileView"] =
        [
            new()
            {
                Name = "Load a profile page",
                Description = "Get all data needed to render a profile page in one call",
                Params = [new() { Name = "address", Value = ExampleAddr1 }],
            }
        ],
        ["circles_getTransactionHistoryEnriched"] =
        [
            new()
            {
                Name = "Recent transactions with profiles",
                Description = "Get the 20 most recent V2 transactions with participant profiles",
                Params =
                [
                    new() { Name = "address", Value = ExampleAddr1 },
                    new() { Name = "fromBlock", Value = 35000000 },
                    new() { Name = "limit", Value = 20 },
                    new() { Name = "version", Value = 2 },
                    new() { Name = "excludeIntermediary", Value = true },
                ],
            }
        ],
        ["circles_searchProfileByAddressOrName"] =
        [
            new()
            {
                Name = "Search by address prefix",
                Description = "Find profiles matching an address prefix (auto-detected by 0x prefix)",
                Params =
                [
                    new() { Name = "query", Value = "0xde374ece" },
                    new() { Name = "limit", Value = 10 },
                ],
            },
            new()
            {
                Name = "Search by name",
                Description = "Full-text search (auto-detected, no 0x prefix)",
                Params =
                [
                    new() { Name = "query", Value = "alice" },
                    new() { Name = "limit", Value = 10 },
                ],
            }
        ],
        ["circles_getAllInvitations"] =
        [
            new()
            {
                Name = "Get all invitation options",
                Description = "Find all ways a user can be invited (trust, escrow, at-scale) with minimum 96 CRC balance",
                Params =
                [
                    new() { Name = "address", Value = ExampleAddr1 },
                    new() { Name = "minimumBalance", Value = "96000000000000000000" },
                ],
            }
        ],
        ["circles_getInvitationsFrom"] =
        [
            new()
            {
                Name = "Pending invitations",
                Description = "Get addresses trusted by this avatar that haven't registered yet",
                Params =
                [
                    new() { Name = "address", Value = ExampleAddr1 },
                    new() { Name = "accepted", Value = false },
                ],
            },
            new()
            {
                Name = "Accepted invitations",
                Description = "Get addresses that registered using this avatar as inviter",
                Params =
                [
                    new() { Name = "address", Value = ExampleAddr1 },
                    new() { Name = "accepted", Value = true },
                ],
            }
        ],
        ["circles_getTransferData"] =
        [
            new()
            {
                Name = "Sent transfers to specific address",
                Description = "Get transfer calldata for all tokens sent from one address to another",
                Params =
                [
                    new() { Name = "address", Value = ExampleAddr1 },
                    new() { Name = "direction", Value = "sent" },
                    new() { Name = "counterparty", Value = ExampleAddr2 },
                    new() { Name = "limit", Value = 50 },
                ],
            }
        ],
        ["circles_getValidInviters"] =
        [
            new()
            {
                Name = "Find sponsors with sufficient balance",
                Description = "Find addresses that trust this user and have at least 96 CRC (1 invitation unit)",
                Params =
                [
                    new() { Name = "address", Value = ExampleAddr1 },
                    new() { Name = "minimumBalance", Value = "96000000000000000000" },
                    new() { Name = "limit", Value = 20 },
                ],
            }
        ],
        ["circles_getTokenInfo"] =
        [
            new()
            {
                Name = "Look up a token",
                Description = "Get metadata for a specific token address",
                Params = [new() { Name = "tokenAddress", Value = ExampleToken }],
            }
        ],
        ["circles_getTokenInfoBatch"] =
        [
            new()
            {
                Name = "Batch token lookup",
                Description = "Resolve multiple token addresses at once",
                Params = [new() { Name = "tokenAddresses", Value = new[] { ExampleToken, ExampleAddr1 } }],
            }
        ],
        ["circles_getAvatarInfoBatch"] =
        [
            new()
            {
                Name = "Batch avatar lookup",
                Description = "Get avatar info for a contact list",
                Params = [new() { Name = "addresses", Value = new[] { ExampleAddr1, ExampleAddr2 } }],
            }
        ],
        ["circles_getProfileCid"] =
        [
            new()
            {
                Name = "Get IPFS CID for a profile",
                Description = "Get the IPFS content identifier for an avatar's profile",
                Params = [new() { Name = "address", Value = ExampleAddr1 }],
            }
        ],
        ["circles_getProfileCidBatch"] =
        [
            new()
            {
                Name = "Batch CID lookup",
                Description = "Get IPFS CIDs for multiple avatars at once",
                Params = [new() { Name = "addresses", Value = new[] { ExampleAddr1, ExampleAddr2 } }],
            }
        ],
        ["circles_getProfileByCid"] =
        [
            new()
            {
                Name = "Fetch profile by IPFS CID",
                Description = "Load profile JSON from IPFS via the server-side cache",
                Params = [new() { Name = "cid", Value = "QmYwAPJzv5CZsnA625s3Xf2nemtYgPpHdWEz79ojWnPbdG" }],
            }
        ],
        ["circles_getProfileByCidBatch"] =
        [
            new()
            {
                Name = "Batch profile fetch",
                Description = "Load multiple profiles by their IPFS CIDs",
                Params = [new() { Name = "cids", Value = new[] { "QmYwAPJzv5CZsnA625s3Xf2nemtYgPpHdWEz79ojWnPbdG", "QmPbxeGcXhYQoAkwH5LRZSxvAXKTDJoNTJHVLHXHFkN3aR" } }],
            }
        ],
        ["circles_getProfileByAddress"] =
        [
            new()
            {
                Name = "Get enriched profile",
                Description = "Fetch a profile with avatar type and name injected",
                Params = [new() { Name = "address", Value = ExampleAddr1 }],
            }
        ],
        ["circles_getProfileByAddressBatch"] =
        [
            new()
            {
                Name = "Batch enriched profiles",
                Description = "Fetch profiles for a list of addresses with avatar enrichment",
                Params = [new() { Name = "addresses", Value = new[] { ExampleAddr1, ExampleAddr2 } }],
            }
        ],
        ["circles_getTrustRelations"] =
        [
            new()
            {
                Name = "Get trust network for an address",
                Description = "View who this address trusts and who trusts it",
                Params = [new() { Name = "address", Value = ExampleAddr1 }],
            }
        ],
        ["circles_getAggregatedTrustRelations"] =
        [
            new()
            {
                Name = "Get categorized trust relations",
                Description = "Trust relations grouped as mutuallyTrusts/trusts/trustedBy",
                Params = [new() { Name = "avatar", Value = ExampleAddr1 }],
            }
        ],
        ["circles_getNetworkSnapshot"] =
        [
            new()
            {
                Name = "Full network snapshot",
                Description = "Download the entire trust network (large response, use sparingly)",
                Params = [],
            }
        ],
        ["circles_getGroupMemberships"] =
        [
            new()
            {
                Name = "Groups I belong to",
                Description = "Find all groups that trust this avatar",
                Params =
                [
                    new() { Name = "memberAddress", Value = ExampleAddr1 },
                    new() { Name = "limit", Value = 50 },
                ],
            }
        ],
        ["circles_getTransactionHistory"] =
        [
            new()
            {
                Name = "Recent V2 transactions",
                Description = "Get the 50 most recent V2 transfers for an avatar",
                Params =
                [
                    new() { Name = "avatarAddress", Value = ExampleAddr1 },
                    new() { Name = "limit", Value = 50 },
                    new() { Name = "version", Value = 2 },
                    new() { Name = "excludeIntermediary", Value = true },
                ],
            }
        ],
        ["circles_getTokenHolders"] =
        [
            new()
            {
                Name = "Token distribution",
                Description = "Get all holders of a specific token",
                Params =
                [
                    new() { Name = "tokenAddress", Value = ExampleToken },
                    new() { Name = "limit", Value = 100 },
                ],
            }
        ],
        ["circles_health"] =
        [
            new()
            {
                Name = "Check health",
                Description = "Verify indexer is healthy and synced",
                Params = [],
            }
        ],
        ["circles_tables"] =
        [
            new()
            {
                Name = "Discover available tables",
                Description = "List all queryable tables and their columns",
                Params = [],
            }
        ],
        ["circles_getTrustNetworkSummary"] =
        [
            new()
            {
                Name = "Trust network stats",
                Description = "Get trust counts and network reach for an avatar",
                Params =
                [
                    new() { Name = "address", Value = ExampleAddr1 },
                    new() { Name = "maxDepth", Value = 3 },
                ],
            }
        ],
        ["circles_getAggregatedTrustRelationsEnriched"] =
        [
            new()
            {
                Name = "Trust list with profiles",
                Description = "Get trust relations with enriched avatar info (names, images)",
                Params =
                [
                    new() { Name = "address", Value = ExampleAddr1 },
                    new() { Name = "limit", Value = 50 },
                ],
            }
        ],
        ["circles_getInvitationOrigin"] =
        [
            new()
            {
                Name = "How was this user invited?",
                Description = "Trace the invitation chain for an address",
                Params = [new() { Name = "address", Value = ExampleAddr1 }],
            }
        ],
        ["circles_getTrustInvitations"] =
        [
            new()
            {
                Name = "Trust-based invitations",
                Description = "Find who can invite this address via trust + balance",
                Params =
                [
                    new() { Name = "address", Value = ExampleAddr1 },
                    new() { Name = "minimumBalance", Value = "96000000000000000000" },
                ],
            }
        ],
        ["circles_getEscrowInvitations"] =
        [
            new()
            {
                Name = "Escrow invitations",
                Description = "Find CRC escrowed for this address (active only)",
                Params = [new() { Name = "address", Value = ExampleAddr1 }],
            }
        ],
        ["circles_getAtScaleInvitations"] =
        [
            new()
            {
                Name = "At-scale invitations",
                Description = "Find pre-created accounts available for this address",
                Params = [new() { Name = "address", Value = ExampleAddr1 }],
            }
        ],
        ["circles_query"] =
        [
            new()
            {
                Name = "Query V2 trusts for an address",
                Description = "Low-level query to get V2 trust relations where the address is the truster",
                Params =
                [
                    new()
                    {
                        Name = "query", Value = new
                        {
                            @namespace = "CrcV2",
                            table = "CrcV2_Trust",
                            columns = new[] { "truster", "trustee", "expiryTime" },
                            filter = new[]
                            {
                                new { Type = "FilterPredicate", Column = "truster", FilterType = "Equals", Value = ExampleAddr1 }
                            },
                            limit = 100
                        }
                    }
                ],
            }
        ],
        ["circles_paginated_query"] =
        [
            new()
            {
                Name = "Paginated transfer query",
                Description = "Iterate through V2 transfers with cursor pagination",
                Params =
                [
                    new()
                    {
                        Name = "query", Value = new
                        {
                            @namespace = "CrcV2",
                            table = "CrcV2_TransferSingle",
                            columns = new[] { "from", "to", "id", "value", "blockNumber" },
                            limit = 50
                        }
                    }
                ],
            }
        ],
    };

    public static OpenRpcDocument Generate()
    {
        SchemaCache.Clear();

        var doc = new OpenRpcDocument
        {
            Info = new OpenRpcInfo
            {
                Title = "Circles RPC API",
                Version = "1.0.0",
                Description =
                    "JSON-RPC 2.0 API for the Circles protocol on Gnosis Chain.\n\n" +
                    "## Overview\n" +
                    "This API provides access to all indexed Circles protocol data: balances, avatars, profiles, trust relations, " +
                    "events, groups, invitations, and transitive transfer path computation.\n\n" +
                    "## Transport\n" +
                    "All methods use JSON-RPC 2.0 over HTTP POST. Send requests to the root endpoint with " +
                    "`Content-Type: application/json`.\n\n" +
                    "```json\n{\"jsonrpc\": \"2.0\", \"id\": 1, \"method\": \"circles_getTotalBalance\", \"params\": [\"0xde374ece6fa50e781e81aac78e811b33d16912c7\", true]}\n```\n\n" +
                    "## Pagination\n" +
                    "Methods returning large result sets use cursor-based pagination. The response includes `hasMore` (boolean) and " +
                    "`nextCursor` (opaque string). Pass `nextCursor` as the last parameter to get the next page.\n\n" +
                    "## WebSocket Subscriptions\n" +
                    "Connect via WebSocket to `/ws/subscribe` and send `{\"jsonrpc\": \"2.0\", \"method\": \"circles_subscribe\", \"params\": [\"circles\", {\"address\": \"0x...\"}]}` " +
                    "to receive real-time event notifications.\n\n" +
                    "## Ethereum RPC Proxy\n" +
                    "Standard Ethereum methods (eth_*, net_*, web3_*) are proxied to the underlying Nethermind node.\n\n" +
                    "## Related APIs\n" +
                    "- **Pathfinder REST API**: `/pathfinder/scalar/v1` — direct access to path computation\n" +
                    "- **Authentication**: `/auth/docs` — SIWE authentication and passkey management\n" +
                    "- **All docs**: `/docs` — unified documentation portal"
            }
        };

        // Servers array tells the OpenRPC playground where to POST JSON-RPC requests
        doc.Servers =
        [
            new OpenRpcServer { Name = "Staging", Url = "https://staging.circlesubi.network" },
            new OpenRpcServer { Name = "Production", Url = "https://rpc.aboutcircles.com" }
        ];

        var interfaceType = typeof(ICirclesRpcModule);

        foreach (var (rpcName, csharpName, tag, summary, description) in MethodMappings)
        {
            var method = interfaceType.GetMethod(csharpName);
            if (method == null) continue;

            var rpcMethod = new OpenRpcMethod
            {
                Name = rpcName,
                Summary = summary,
                Description = description,
                Tags = [new OpenRpcTag { Name = tag }]
            };

            // Use parameter overrides if available, otherwise reflect
            if (ParameterOverrides.TryGetValue(rpcName, out var overrides))
            {
                rpcMethod.Params.AddRange(overrides);
            }
            else
            {
                rpcMethod.Params.AddRange(ReflectParams(method));
            }

            // Build result schema — use override if available, otherwise reflect from return type
            if (ResultOverrides.TryGetValue(rpcName, out var resultSchema))
            {
                rpcMethod.Result = new OpenRpcResult
                {
                    Name = $"{rpcName}Result",
                    Schema = resultSchema
                };
            }
            else
            {
                var returnType = UnwrapTaskType(method.ReturnType);
                rpcMethod.Result = new OpenRpcResult
                {
                    Name = $"{rpcName}Result",
                    Schema = BuildSchema(returnType)
                };
            }

            // Add examples if available
            if (MethodExamples.TryGetValue(rpcName, out var examples))
            {
                rpcMethod.Examples = examples;
            }

            doc.Methods.Add(rpcMethod);
        }

        // Re-ensure schemas referenced via $ref that were wiped by SchemaCache.Clear().
        // ParamRef() calls EnsureSchemaByName() during static init, but Clear() at the
        // top of Generate() removes those entries. Re-add them now.
        foreach (var method in doc.Methods)
        {
            foreach (var p in method.Params)
                if (p.Schema?.Ref != null)
                    EnsureSchemaByName(p.Schema.Ref.Replace("#/components/schemas/", ""));

            if (method.Result?.Schema?.Ref != null)
                EnsureSchemaByName(method.Result.Schema.Ref.Replace("#/components/schemas/", ""));
        }

        // Apply property descriptions to generated schemas
        ApplyPropertyDescriptions();

        // Add component schemas
        if (SchemaCache.Count > 0)
        {
            doc.Components = new OpenRpcComponents { Schemas = new(SchemaCache) };
        }

        return doc;
    }

    // ─── Param helpers ───────────────────────────────────────────────────────

    private static OpenRpcParam Param(string name, bool required, string type, string? desc = null, string? pattern = null) =>
        new()
        {
            Name = name,
            Required = required,
            Description = desc,
            Schema = new JsonSchemaObject { Type = type, Pattern = pattern }
        };

    private static OpenRpcParam ParamArray(string name, bool required, string itemType, string? desc = null) =>
        new()
        {
            Name = name,
            Required = required,
            Description = desc,
            Schema = new JsonSchemaObject
            {
                Type = "array",
                Items = new JsonSchemaObject { Type = itemType }
            }
        };

    private static OpenRpcParam ParamRef(string name, bool required, string schemaName, string? desc = null)
    {
        // Ensure the referenced schema exists in the cache
        EnsureSchemaByName(schemaName);
        return new OpenRpcParam
        {
            Name = name,
            Required = required,
            Description = desc,
            Schema = new JsonSchemaObject { Ref = $"#/components/schemas/{schemaName}" }
        };
    }

    // ─── Reflection-based param extraction ───────────────────────────────────

    private static List<OpenRpcParam> ReflectParams(MethodInfo method)
    {
        var result = new List<OpenRpcParam>();
        foreach (var p in method.GetParameters())
        {
            var paramType = p.ParameterType;
            var isNullable = Nullable.GetUnderlyingType(paramType) != null
                             || (paramType.IsClass && p.HasDefaultValue);
            var required = !p.HasDefaultValue && !isNullable;

            result.Add(new OpenRpcParam
            {
                Name = p.Name ?? "param",
                Required = required,
                Schema = BuildSchema(Nullable.GetUnderlyingType(paramType) ?? paramType),
                Description = BuildReflectedParamDescription(p)
            });
        }
        return result;
    }

    private static string? BuildReflectedParamDescription(ParameterInfo p)
    {
        var name = p.Name?.ToLowerInvariant() ?? "";

        if (name.Contains("address") || name.Contains("avatar"))
            return $"Ethereum address (0x-prefixed, 40 hex chars). {(p.HasDefaultValue ? "Optional." : "Required.")}";
        if (name == "limit")
            return $"Maximum number of results to return. Default: {p.DefaultValue ?? 50}, max: 200.";
        if (name == "cursor")
            return "Opaque pagination cursor from a previous response's nextCursor field. Omit for the first page.";
        if (name == "version")
            return "Protocol version filter: 1 = V1 only, 2 = V2 only, null = both. V1 and V2 are independent systems.";
        if (name.Contains("block"))
            return "Block number on Gnosis Chain (~1 block per 5 seconds).";
        if (name == "accepted")
            return "When true, returns registered (accepted) invitations. When false, returns pending (not yet registered) invitations.";
        if (name.Contains("balance"))
            return "Balance amount as a string in CRC wei (1 CRC = 10^18 wei). Example: '96000000000000000000' = 96 CRC.";

        return null;
    }

    // ─── Schema builder ──────────────────────────────────────────────────────

    private static JsonSchemaObject BuildSchema(Type type)
    {
        // Unwrap nullable
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            var inner = BuildSchema(underlying);
            inner.Nullable = true;
            return inner;
        }

        // Primitives
        if (type == typeof(string)) return new JsonSchemaObject { Type = "string" };
        if (type == typeof(bool)) return new JsonSchemaObject { Type = "boolean" };
        if (type == typeof(int) || type == typeof(long) || type == typeof(short))
            return new JsonSchemaObject { Type = "integer" };
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return new JsonSchemaObject { Type = "number" };

        // JsonElement — opaque object
        if (type == typeof(JsonElement))
            return new JsonSchemaObject { Type = "object", Description = "Arbitrary JSON value" };

        // Arrays / Lists
        if (type.IsArray)
        {
            var elemType = type.GetElementType()!;
            return new JsonSchemaObject { Type = "array", Items = BuildSchema(elemType) };
        }
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(List<>)
                                || type.GetGenericTypeDefinition() == typeof(IList<>)
                                || type.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        {
            return new JsonSchemaObject { Type = "array", Items = BuildSchema(type.GetGenericArguments()[0]) };
        }

        // Dictionary<string, T>
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            && type.GetGenericArguments()[0] == typeof(string))
        {
            return new JsonSchemaObject
            {
                Type = "object",
                AdditionalProperties = BuildSchema(type.GetGenericArguments()[1])
            };
        }

        // Complex type → $ref
        var schemaName = GetSchemaName(type);
        if (!SchemaCache.ContainsKey(schemaName))
        {
            // Add placeholder to prevent infinite recursion
            SchemaCache[schemaName] = new JsonSchemaObject { Type = "object" };
            SchemaCache[schemaName] = BuildObjectSchema(type);
        }

        return new JsonSchemaObject { Ref = $"#/components/schemas/{schemaName}" };
    }

    private static JsonSchemaObject BuildObjectSchema(Type type)
    {
        var schema = new JsonSchemaObject
        {
            Type = "object",
            Properties = new Dictionary<string, JsonSchemaObject>(),
            Required = new List<string>()
        };

        // Handle records/classes with constructor parameters (positional records)
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var primaryCtor = constructors.MaxBy(c => c.GetParameters().Length);
        var ctorParams = primaryCtor?.GetParameters() ?? [];

        // Use public properties for schema generation
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead);

        foreach (var prop in props)
        {
            // Use JsonPropertyName attribute if present
            var jsonName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                        ?? CamelCase(prop.Name);

            // Skip JsonIgnore properties
            if (prop.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                continue;

            var propSchema = BuildSchema(prop.PropertyType);
            schema.Properties[jsonName] = propSchema;

            // Check if required (non-nullable, no default in ctor)
            var ctorParam = ctorParams.FirstOrDefault(p =>
                string.Equals(p.Name, prop.Name, StringComparison.OrdinalIgnoreCase));

            var isNullable = Nullable.GetUnderlyingType(prop.PropertyType) != null
                          || (prop.PropertyType.IsClass && ctorParam?.HasDefaultValue == true);

            if (!isNullable && ctorParam is { HasDefaultValue: false })
            {
                schema.Required.Add(jsonName);
            }
        }

        if (schema.Required.Count == 0) schema.Required = null;
        if (schema.Properties.Count == 0) schema.Properties = null;

        return schema;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static Type UnwrapTaskType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            return type.GetGenericArguments()[0];
        return type;
    }

    private static string GetSchemaName(Type type)
    {
        if (type.IsGenericType)
        {
            var baseName = type.Name[..type.Name.IndexOf('`')];
            var args = string.Join("", type.GetGenericArguments().Select(a => a.Name));
            return $"{baseName}_{args}";
        }
        return type.Name;
    }

    private static string CamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static void EnsureSchemaByName(string schemaName)
    {
        if (SchemaCache.ContainsKey(schemaName)) return;

        // Map known schema names to types
        var typeMap = new Dictionary<string, Type>
        {
            ["FlowRequest"] = typeof(FlowRequest),
            ["SimulatedBalance"] = typeof(SimulatedBalance),
            ["SimulatedTrust"] = typeof(SimulatedTrust),
            ["MaxFlowResponse"] = typeof(MaxFlowResponse),
            ["TransferPathStep"] = typeof(TransferPathStep),
            ["DebugPipelineStages"] = typeof(DebugPipelineStages),
            ["SelectDto"] = typeof(SelectDto),
            ["FilterPredicateDto"] = typeof(IFilterPredicateDto),
        };

        if (typeMap.TryGetValue(schemaName, out var type))
        {
            SchemaCache[schemaName] = type.IsInterface
                ? new JsonSchemaObject { Type = "object", Description = $"See {type.Name} for structure" }
                : BuildObjectSchema(type);
        }
        else
        {
            SchemaCache[schemaName] = new JsonSchemaObject { Type = "object" };
        }
    }

    // ─── Result schema overrides ────────────────────────────────────────────
    // For methods returning JsonElement (opaque proxy), provide explicit result schemas
    // so LLMs and codegen tools know the actual response shape.

    private static readonly Dictionary<string, JsonSchemaObject> ResultOverrides = new()
    {
        ["circlesV2_findPath"] = new JsonSchemaObject
        {
            Ref = "#/components/schemas/MaxFlowResponse"
        },
        ["circles_getProfileByCid"] = new JsonSchemaObject
        {
            Type = "object",
            Description = "Profile JSON object from IPFS. Schema varies by profile version — typically contains name, description, imageUrl, and other user-defined fields."
        },
        ["circles_getProfileByAddress"] = new JsonSchemaObject
        {
            Type = "object",
            Description = "Profile JSON from IPFS, enriched with avatar type and short name from V2 registration. Schema varies by profile version."
        },
        ["circles_getNetworkSnapshot"] = new JsonSchemaObject
        {
            Type = "object",
            Description = "Network state snapshot containing trust graph edges, token balances, and avatar registrations. Internal format — structure may change between versions."
        },
    };

    // ─── Property descriptions ──────────────────────────────────────────────
    // BuildObjectSchema reflects property names/types but not XML doc comments (those
    // aren't available at runtime without shipping the XML file). This dictionary
    // provides descriptions keyed by (schemaName, jsonPropertyName).

    private static readonly Dictionary<(string Schema, string Prop), string> PropertyDescriptions = new()
    {
        // ── FlowRequest ─────────────────────────────────────────────────────
        { ("FlowRequest", "source"), "Sender address (0x-prefixed, 40 hex chars). Must be a registered Circles V2 avatar." },
        { ("FlowRequest", "sink"), "Receiver address (0x-prefixed, 40 hex chars). Must be a registered Circles V2 avatar." },
        { ("FlowRequest", "targetFlow"), "Amount to transfer in CRC wei (1 CRC = 10^18 wei). Use max uint256 to discover maximum possible flow." },
        { ("FlowRequest", "toTokens"), "Restrict which tokens the sink can receive. Array of token-owner addresses." },
        { ("FlowRequest", "fromTokens"), "Restrict which tokens the source can send. Array of token-owner addresses." },
        { ("FlowRequest", "excludedFromTokens"), "Exclude specific tokens from the source side. Array of token-owner addresses." },
        { ("FlowRequest", "excludedToTokens"), "Exclude specific tokens from the sink side. Array of token-owner addresses." },
        { ("FlowRequest", "withWrap"), "When true, includes ERC-20 wrapper token paths in addition to native ERC-1155 paths." },
        { ("FlowRequest", "simulatedBalances"), "Hypothetical token balances to inject into the graph before path computation." },
        { ("FlowRequest", "simulatedTrusts"), "Hypothetical trust relations to inject into the graph before path computation." },
        { ("FlowRequest", "simulatedConsentedAvatars"), "Addresses to treat as having consented to advanced usage (ERC-1155 operator approval)." },
        { ("FlowRequest", "maxTransfers"), "Maximum number of transfer steps in the result. Limits path complexity for on-chain gas cost control." },
        { ("FlowRequest", "quantizedMode"), "When true, enforces 96 CRC quantization for sink-bound transfers (invitation module). Each transfer = N × 96 CRC." },
        { ("FlowRequest", "debugShowIntermediateSteps"), "When true, includes debug info showing all transformation stages: rawPaths, collapsed, routerInserted, sorted." },

        // ── SimulatedBalance ────────────────────────────────────────────────
        { ("SimulatedBalance", "holder"), "Holder address — the avatar that holds the tokens (0x-prefixed)." },
        { ("SimulatedBalance", "token"), "Token identifier — the token-owner avatar address, or ERC-20 wrapper address." },
        { ("SimulatedBalance", "amount"), "Balance amount as uint256 string in CRC wei. Example: \"96000000000000000000\" = 96 CRC." },
        { ("SimulatedBalance", "isWrapped"), "When true, treat as an ERC-20 wrapped token balance instead of native ERC-1155." },
        { ("SimulatedBalance", "isStatic"), "When true, this balance is not subject to demurrage decay." },

        // ── SimulatedTrust ──────────────────────────────────────────────────
        { ("SimulatedTrust", "truster"), "The address that grants trust (0x-prefixed, 40 hex chars)." },
        { ("SimulatedTrust", "trustee"), "The address that receives trust (0x-prefixed, 40 hex chars)." },

        // ── MaxFlowResponse ─────────────────────────────────────────────────
        { ("MaxFlowResponse", "maxFlow"), "Maximum achievable flow in CRC wei (uint256 as decimal string)." },
        { ("MaxFlowResponse", "transfers"), "Ordered list of individual token transfer steps to submit on-chain via Hub.sol operateFlowMatrix()." },
        { ("MaxFlowResponse", "debug"), "Debug information showing transformation stages (only present if debugShowIntermediateSteps=true)." },

        // ── TransferPathStep ────────────────────────────────────────────────
        { ("TransferPathStep", "from"), "Sender address for this transfer step (0x-prefixed, lowercase)." },
        { ("TransferPathStep", "to"), "Receiver address for this transfer step (0x-prefixed, lowercase)." },
        { ("TransferPathStep", "tokenOwner"), "Token owner address identifying which Circles token is transferred." },
        { ("TransferPathStep", "value"), "Transfer amount in CRC wei (uint256 as decimal string)." },

        // ── DebugPipelineStages ─────────────────────────────────────────────
        { ("DebugPipelineStages", "rawPaths"), "Stage 1: Raw paths from MaxFlowSolver with token pools (tpool-0x...)." },
        { ("DebugPipelineStages", "collapsed"), "Stage 2: Token pools collapsed, showing Avatar→Avatar flows." },
        { ("DebugPipelineStages", "routerInserted"), "Stage 3: Router inserted for group mints (Avatar→Group becomes Avatar→Router→Group)." },
        { ("DebugPipelineStages", "sorted"), "Stage 4: Final sorted order for contract execution (collateral before mints)." },

        // ── TokenInfo ───────────────────────────────────────────────────────
        { ("TokenInfo", "tokenAddress"), "On-chain address of the token contract (ERC-1155 token ID or ERC-20 wrapper address)." },
        { ("TokenInfo", "tokenOwner"), "Avatar address that owns/minted this token." },
        { ("TokenInfo", "tokenType"), "Token classification: 'RegisterHuman', 'RegisterGroup', or 'RegisterOrganization'." },
        { ("TokenInfo", "version"), "Circles protocol version: 1 = V1 CRC token, 2 = V2 ERC-1155/ERC-20 token." },
        { ("TokenInfo", "isErc20"), "True if this is an ERC-20 token (V1 CRC or V2 wrapper)." },
        { ("TokenInfo", "isErc1155"), "True if this is a native V2 ERC-1155 token." },
        { ("TokenInfo", "isWrapped"), "True if this is an ERC-20 wrapper around a V2 ERC-1155 token." },
        { ("TokenInfo", "isInflationary"), "True if the token uses inflationary (TimeCircles) denomination." },
        { ("TokenInfo", "isGroup"), "True if this token belongs to a Circles group (minted via group trust)." },

        // ── CirclesTokenBalance ─────────────────────────────────────────────
        { ("CirclesTokenBalance", "tokenAddress"), "On-chain address of the token contract." },
        { ("CirclesTokenBalance", "tokenId"), "ERC-1155 token ID (same as token owner address for personal tokens)." },
        { ("CirclesTokenBalance", "tokenOwner"), "Avatar address that owns/minted this token." },
        { ("CirclesTokenBalance", "tokenType"), "Token classification: 'RegisterHuman', 'RegisterGroup', or 'RegisterOrganization'." },
        { ("CirclesTokenBalance", "version"), "Circles protocol version: 1 or 2." },
        { ("CirclesTokenBalance", "attoCircles"), "Balance in atto-Circles (10^-18 CRC) as string, inflationary/TimeCircles denomination." },
        { ("CirclesTokenBalance", "circles"), "Balance in Circles (human-readable), inflationary/TimeCircles denomination." },
        { ("CirclesTokenBalance", "staticAttoCircles"), "Balance in atto-Circles (10^-18 CRC) as string, demurrage-adjusted static denomination." },
        { ("CirclesTokenBalance", "staticCircles"), "Balance in Circles (human-readable), demurrage-adjusted static denomination." },
        { ("CirclesTokenBalance", "attoCrc"), "Balance in atto-CRC as string (alias for attoCircles in V2 context)." },
        { ("CirclesTokenBalance", "crc"), "Balance in CRC (human-readable, alias for circles in V2 context)." },
        { ("CirclesTokenBalance", "isErc20"), "True if this is an ERC-20 token." },
        { ("CirclesTokenBalance", "isErc1155"), "True if this is a native V2 ERC-1155 token." },
        { ("CirclesTokenBalance", "isWrapped"), "True if this is an ERC-20 wrapper around a V2 ERC-1155 token." },
        { ("CirclesTokenBalance", "isInflationary"), "True if the token uses inflationary denomination." },
        { ("CirclesTokenBalance", "isGroup"), "True if this token belongs to a Circles group." },

        // ── AvatarInfo ──────────────────────────────────────────────────────
        { ("AvatarInfo", "version"), "Circles protocol version the avatar is registered under: 1 or 2." },
        { ("AvatarInfo", "type"), "Avatar type: 'RegisterHuman', 'RegisterGroup', or 'RegisterOrganization'." },
        { ("AvatarInfo", "avatar"), "Ethereum address of the avatar (0x-prefixed, lowercase)." },
        { ("AvatarInfo", "tokenId"), "Token ID associated with this avatar (personal token address)." },
        { ("AvatarInfo", "hasV1"), "True if this avatar also has a V1 registration (migrated or dual-registered)." },
        { ("AvatarInfo", "v1Token"), "V1 CRC token address, if the avatar has a V1 registration." },
        { ("AvatarInfo", "cidV0Digest"), "Raw digest bytes of the IPFS CIDv0 for the avatar's profile (hex-encoded)." },
        { ("AvatarInfo", "cidV0"), "IPFS CIDv0 string pointing to the avatar's profile JSON." },
        { ("AvatarInfo", "isHuman"), "True if the avatar is registered as a human (not a group or organization)." },
        { ("AvatarInfo", "name"), "Human-readable name from V2 registration (on-chain, not from IPFS profile)." },
        { ("AvatarInfo", "symbol"), "Token symbol from V2 registration." },

        // ── TrustRelation ───────────────────────────────────────────────────
        { ("TrustRelation", "user"), "Address of the trusted/trusting avatar." },
        { ("TrustRelation", "limit"), "Trust limit percentage (0-100). 100 = full trust, 0 = no trust." },

        // ── TrustRelationsResponse ──────────────────────────────────────────
        { ("TrustRelationsResponse", "user"), "Address of the queried avatar." },
        { ("TrustRelationsResponse", "trusts"), "Addresses this avatar trusts (outgoing trust edges)." },
        { ("TrustRelationsResponse", "trustedBy"), "Addresses that trust this avatar (incoming trust edges)." },

        // ── CommonTrustResponse ─────────────────────────────────────────────
        { ("CommonTrustResponse", "address1"), "First address in the common trust query." },
        { ("CommonTrustResponse", "address2"), "Second address in the common trust query." },
        { ("CommonTrustResponse", "commonTrusts"), "Array of addresses that both queried addresses have a trust relationship with." },

        // ── AggregatedTrustRelation ─────────────────────────────────────────
        { ("AggregatedTrustRelation", "subjectAvatar"), "Avatar address that is the subject of this trust relation." },
        { ("AggregatedTrustRelation", "relation"), "Trust relation type: 'mutuallyTrusts', 'trusts', or 'trustedBy'." },
        { ("AggregatedTrustRelation", "objectAvatar"), "Avatar address that is the object of this trust relation." },
        { ("AggregatedTrustRelation", "timestamp"), "Unix timestamp when this trust relation was established." },
        { ("AggregatedTrustRelation", "expiryTime"), "Unix timestamp when this trust expires (0 = no expiry)." },
        { ("AggregatedTrustRelation", "objectAvatarType"), "Type of the object avatar: 'Human', 'Group', or 'Organization'." },

        // ── TrustStats ──────────────────────────────────────────────────────
        { ("TrustStats", "trustsCount"), "Number of avatars this address trusts (outgoing)." },
        { ("TrustStats", "trustedByCount"), "Number of avatars that trust this address (incoming)." },

        // ── TrustRelationInfo ───────────────────────────────────────────────
        { ("TrustRelationInfo", "address"), "Ethereum address of the related avatar." },
        { ("TrustRelationInfo", "avatarInfo"), "Full avatar registration info for the related address." },
        { ("TrustRelationInfo", "relationType"), "Relation type: 'mutual', 'trusts', or 'trustedBy'." },

        // ── TrustRelationCounts ─────────────────────────────────────────────
        { ("TrustRelationCounts", "mutual"), "Number of mutual trust relationships." },
        { ("TrustRelationCounts", "trusts"), "Number of outgoing trust relationships (this avatar trusts them)." },
        { ("TrustRelationCounts", "trustedBy"), "Number of incoming trust relationships (they trust this avatar)." },
        { ("TrustRelationCounts", "total"), "Total trust relationships across all types." },

        // ── TransactionHistoryRow ───────────────────────────────────────────
        { ("TransactionHistoryRow", "blockNumber"), "Gnosis Chain block number containing this transaction." },
        { ("TransactionHistoryRow", "timestamp"), "Unix timestamp of the block." },
        { ("TransactionHistoryRow", "transactionIndex"), "Position of the transaction within the block." },
        { ("TransactionHistoryRow", "logIndex"), "Position of the log entry within the transaction receipt." },
        { ("TransactionHistoryRow", "transactionHash"), "Keccak-256 hash of the transaction (0x-prefixed, 64 hex chars)." },
        { ("TransactionHistoryRow", "version"), "Circles protocol version: 1 or 2." },
        { ("TransactionHistoryRow", "from"), "Sender address of the transfer." },
        { ("TransactionHistoryRow", "to"), "Receiver address of the transfer." },
        { ("TransactionHistoryRow", "operator"), "ERC-1155 operator address (V2 only, null for V1)." },
        { ("TransactionHistoryRow", "id"), "ERC-1155 token ID (V2 only, null for V1)." },
        { ("TransactionHistoryRow", "value"), "Transfer amount in CRC wei as string." },
        { ("TransactionHistoryRow", "circles"), "Transfer amount in Circles (inflationary/TimeCircles denomination)." },
        { ("TransactionHistoryRow", "attoCircles"), "Transfer amount in atto-Circles (inflationary denomination) as string." },
        { ("TransactionHistoryRow", "crc"), "Transfer amount in CRC (V2 static denomination)." },
        { ("TransactionHistoryRow", "attoCrc"), "Transfer amount in atto-CRC (V2 static denomination) as string." },
        { ("TransactionHistoryRow", "staticCircles"), "Transfer amount in static Circles (demurrage-adjusted)." },
        { ("TransactionHistoryRow", "staticAttoCircles"), "Transfer amount in static atto-Circles (demurrage-adjusted) as string." },

        // ── TransferDataRow ─────────────────────────────────────────────────
        { ("TransferDataRow", "blockNumber"), "Gnosis Chain block number." },
        { ("TransferDataRow", "timestamp"), "Unix timestamp of the block." },
        { ("TransferDataRow", "transactionIndex"), "Position of the transaction within the block." },
        { ("TransferDataRow", "logIndex"), "Position of the log entry within the transaction receipt." },
        { ("TransferDataRow", "transactionHash"), "Keccak-256 hash of the transaction." },
        { ("TransferDataRow", "from"), "Sender address." },
        { ("TransferDataRow", "to"), "Receiver address." },
        { ("TransferDataRow", "data"), "Hex-encoded bytes of the transfer data payload." },

        // ── TokenHolderRow ──────────────────────────────────────────────────
        { ("TokenHolderRow", "account"), "Ethereum address of the token holder." },
        { ("TokenHolderRow", "balance"), "Token balance in CRC wei as string." },
        { ("TokenHolderRow", "tokenAddress"), "Address of the held token contract." },
        { ("TokenHolderRow", "version"), "Circles protocol version: 1 or 2." },

        // ── EnrichedTransaction ─────────────────────────────────────────────
        { ("EnrichedTransaction", "blockNumber"), "Gnosis Chain block number." },
        { ("EnrichedTransaction", "timestamp"), "Unix timestamp of the block." },
        { ("EnrichedTransaction", "transactionHash"), "Keccak-256 hash of the transaction." },
        { ("EnrichedTransaction", "transactionIndex"), "Position of the transaction within the block." },
        { ("EnrichedTransaction", "logIndex"), "Position of the log entry within the transaction receipt." },
        { ("EnrichedTransaction", "event"), "Raw event data as JSON object." },
        { ("EnrichedTransaction", "participants"), "Map of participant address → profile/avatar info for all addresses involved in this event." },
        // RpcResponses.cs variant has extra fields
        { ("EnrichedTransaction", "version"), "Circles protocol version: 1 or 2." },
        { ("EnrichedTransaction", "from"), "Sender address of the transfer." },
        { ("EnrichedTransaction", "to"), "Receiver address of the transfer." },
        { ("EnrichedTransaction", "operator"), "ERC-1155 operator address (V2 only)." },
        { ("EnrichedTransaction", "id"), "ERC-1155 token ID (V2 only)." },
        { ("EnrichedTransaction", "value"), "Transfer amount in CRC wei as string." },
        { ("EnrichedTransaction", "circles"), "Transfer amount in Circles (inflationary denomination)." },
        { ("EnrichedTransaction", "attoCircles"), "Transfer amount in atto-Circles as string." },
        { ("EnrichedTransaction", "crc"), "Transfer amount in CRC (static denomination)." },
        { ("EnrichedTransaction", "attoCrc"), "Transfer amount in atto-CRC as string." },
        { ("EnrichedTransaction", "staticCircles"), "Transfer amount in static Circles." },
        { ("EnrichedTransaction", "staticAttoCircles"), "Transfer amount in static atto-Circles as string." },
        { ("EnrichedTransaction", "fromProfile"), "IPFS profile JSON of the sender (if available)." },
        { ("EnrichedTransaction", "toProfile"), "IPFS profile JSON of the receiver (if available)." },

        // ── ParticipantInfo ─────────────────────────────────────────────────
        { ("ParticipantInfo", "avatarInfo"), "Avatar registration info for the participant." },
        { ("ParticipantInfo", "profile"), "IPFS profile JSON for the participant (if available)." },

        // ── GroupRow ────────────────────────────────────────────────────────
        { ("GroupRow", "group"), "Ethereum address of the group." },
        { ("GroupRow", "name"), "On-chain name of the group." },
        { ("GroupRow", "symbol"), "On-chain token symbol of the group." },
        { ("GroupRow", "mint"), "Mint policy contract address controlling who can mint group tokens." },
        { ("GroupRow", "treasury"), "Treasury contract address holding group collateral." },
        { ("GroupRow", "blockNumber"), "Block number when the group was registered." },
        { ("GroupRow", "timestamp"), "Unix timestamp when the group was registered." },

        // ── GroupMembershipRow ──────────────────────────────────────────────
        { ("GroupMembershipRow", "blockNumber"), "Block number when the membership was created." },
        { ("GroupMembershipRow", "timestamp"), "Unix timestamp when the membership was created." },
        { ("GroupMembershipRow", "transactionIndex"), "Position of the transaction within the block." },
        { ("GroupMembershipRow", "logIndex"), "Position of the log entry within the transaction receipt." },
        { ("GroupMembershipRow", "transactionHash"), "Keccak-256 hash of the membership transaction." },
        { ("GroupMembershipRow", "group"), "Ethereum address of the group." },
        { ("GroupMembershipRow", "member"), "Ethereum address of the group member." },
        { ("GroupMembershipRow", "expiryTime"), "Unix timestamp when the membership expires (0 = no expiry)." },

        // ── InvitationOriginResponse ────────────────────────────────────────
        { ("InvitationOriginResponse", "address"), "Address of the invited avatar." },
        { ("InvitationOriginResponse", "invitationType"), "How the avatar was invited: 'v1_signup', 'v2_standard', 'v2_escrow', or 'v2_at_scale'." },
        { ("InvitationOriginResponse", "inviter"), "Address of the direct inviter (null if unknown)." },
        { ("InvitationOriginResponse", "proxyInviter"), "Address of the proxy inviter for escrow invitations." },
        { ("InvitationOriginResponse", "escrowAmount"), "Amount escrowed for escrow invitations (CRC wei string)." },
        { ("InvitationOriginResponse", "blockNumber"), "Block number when the invitation occurred." },
        { ("InvitationOriginResponse", "timestamp"), "Unix timestamp of the invitation." },
        { ("InvitationOriginResponse", "transactionHash"), "Transaction hash of the invitation event." },
        { ("InvitationOriginResponse", "version"), "Circles protocol version: 1 or 2." },

        // ── AllInvitationsResponse ──────────────────────────────────────────
        { ("AllInvitationsResponse", "address"), "Address of the queried inviter." },
        { ("AllInvitationsResponse", "trustInvitations"), "Invitations via direct trust (the inviter trusts the invitee)." },
        { ("AllInvitationsResponse", "escrowInvitations"), "Invitations via CRC escrow deposit." },
        { ("AllInvitationsResponse", "atScaleInvitations"), "Invitations via the at-scale invitation mechanism." },

        // ── TrustInvitation ─────────────────────────────────────────────────
        { ("TrustInvitation", "address"), "Address of the invited avatar." },
        { ("TrustInvitation", "source"), "Invitation source type (always 'trust')." },
        { ("TrustInvitation", "balance"), "Current CRC balance of the inviter relevant to this invitation (CRC wei string)." },
        { ("TrustInvitation", "avatarInfo"), "Avatar info of the invited account (if registered)." },

        // ── EscrowInvitation ────────────────────────────────────────────────
        { ("EscrowInvitation", "address"), "Address of the invited avatar." },
        { ("EscrowInvitation", "source"), "Invitation source type (always 'escrow')." },
        { ("EscrowInvitation", "escrowedAmount"), "Amount of CRC escrowed for this invitation (CRC wei string)." },
        { ("EscrowInvitation", "escrowDays"), "Number of days the CRC is escrowed before release." },
        { ("EscrowInvitation", "blockNumber"), "Block number when the escrow was created." },
        { ("EscrowInvitation", "timestamp"), "Unix timestamp when the escrow was created." },
        { ("EscrowInvitation", "avatarInfo"), "Avatar info of the invited account (if registered)." },

        // ── AtScaleInvitation ───────────────────────────────────────────────
        { ("AtScaleInvitation", "address"), "Address of the invited avatar." },
        { ("AtScaleInvitation", "source"), "Invitation source type (always 'atScale')." },
        { ("AtScaleInvitation", "blockNumber"), "Block number of the at-scale invitation event." },
        { ("AtScaleInvitation", "timestamp"), "Unix timestamp of the at-scale invitation event." },
        { ("AtScaleInvitation", "originInviter"), "Original inviter address in the invitation chain." },

        // ── InvitationsFromResponse ─────────────────────────────────────────
        { ("InvitationsFromResponse", "address"), "Address of the queried inviter." },
        { ("InvitationsFromResponse", "accepted"), "Filter applied: true = registered invitees, false = pending invitees." },
        { ("InvitationsFromResponse", "results"), "List of invited accounts matching the filter." },

        // ── InvitedAccountInfo ──────────────────────────────────────────────
        { ("InvitedAccountInfo", "address"), "Address of the invited account." },
        { ("InvitedAccountInfo", "status"), "Invitation status: 'accepted' (registered) or 'pending' (not yet registered)." },
        { ("InvitedAccountInfo", "blockNumber"), "Block number of the invitation event." },
        { ("InvitedAccountInfo", "timestamp"), "Unix timestamp of the invitation event." },
        { ("InvitedAccountInfo", "avatarInfo"), "Avatar info if the invited account has registered." },

        // ── PagedResponse (generic — applied to all PagedResponse_* variants) ──
        { ("PagedResponse_TrustRelationInfo", "results"), "Array of trust relation info objects for the current page." },
        { ("PagedResponse_TrustRelationInfo", "hasMore"), "True if more results exist beyond this page." },
        { ("PagedResponse_TrustRelationInfo", "nextCursor"), "Opaque cursor to pass as 'cursor' parameter for the next page. Null if no more results." },
        { ("PagedResponse_AggregatedTrustRelation", "results"), "Array of aggregated trust relations for the current page." },
        { ("PagedResponse_AggregatedTrustRelation", "hasMore"), "True if more results exist beyond this page." },
        { ("PagedResponse_AggregatedTrustRelation", "nextCursor"), "Opaque cursor for the next page." },
        { ("PagedResponse_GroupRow", "results"), "Array of group rows for the current page." },
        { ("PagedResponse_GroupRow", "hasMore"), "True if more results exist beyond this page." },
        { ("PagedResponse_GroupRow", "nextCursor"), "Opaque cursor for the next page." },
        { ("PagedResponse_GroupMembershipRow", "results"), "Array of group membership rows for the current page." },
        { ("PagedResponse_GroupMembershipRow", "hasMore"), "True if more results exist beyond this page." },
        { ("PagedResponse_GroupMembershipRow", "nextCursor"), "Opaque cursor for the next page." },
        { ("PagedResponse_TransactionHistoryRow", "results"), "Array of transaction history rows for the current page." },
        { ("PagedResponse_TransactionHistoryRow", "hasMore"), "True if more results exist beyond this page." },
        { ("PagedResponse_TransactionHistoryRow", "nextCursor"), "Opaque cursor for the next page." },
        { ("PagedResponse_TransferDataRow", "results"), "Array of transfer data rows for the current page." },
        { ("PagedResponse_TransferDataRow", "hasMore"), "True if more results exist beyond this page." },
        { ("PagedResponse_TransferDataRow", "nextCursor"), "Opaque cursor for the next page." },
        { ("PagedResponse_TokenHolderRow", "results"), "Array of token holder rows for the current page." },
        { ("PagedResponse_TokenHolderRow", "hasMore"), "True if more results exist beyond this page." },
        { ("PagedResponse_TokenHolderRow", "nextCursor"), "Opaque cursor for the next page." },
        { ("PagedResponse_InvitedAccountInfo", "results"), "Array of invited account info objects for the current page." },
        { ("PagedResponse_InvitedAccountInfo", "hasMore"), "True if more results exist beyond this page." },
        { ("PagedResponse_InvitedAccountInfo", "nextCursor"), "Opaque cursor for the next page." },

        // ── PagedEventsResponse ─────────────────────────────────────────────
        { ("PagedEventsResponse", "events"), "Array of event objects for the current page." },
        { ("PagedEventsResponse", "hasMore"), "True if more events exist beyond this page." },
        { ("PagedEventsResponse", "nextCursor"), "Opaque cursor for the next page." },

        // ── PagedQueryResponse ──────────────────────────────────────────────
        { ("PagedQueryResponse", "columns"), "Column names for the result set." },
        { ("PagedQueryResponse", "rows"), "Array of row arrays, each containing values in column order." },
        { ("PagedQueryResponse", "hasMore"), "True if more rows exist beyond this page." },
        { ("PagedQueryResponse", "nextCursor"), "Opaque cursor for the next page." },

        // ── PagedAggregatedTrustRelationsResponse ───────────────────────────
        { ("PagedAggregatedTrustRelationsResponse", "address"), "Address of the queried avatar." },
        { ("PagedAggregatedTrustRelationsResponse", "results"), "Array of trust relation info objects for the current page." },
        { ("PagedAggregatedTrustRelationsResponse", "counts"), "Summary counts of trust relations by type." },
        { ("PagedAggregatedTrustRelationsResponse", "hasMore"), "True if more results exist beyond this page." },
        { ("PagedAggregatedTrustRelationsResponse", "nextCursor"), "Opaque cursor for the next page." },

        // ── PagedValidInvitersResponse ──────────────────────────────────────
        { ("PagedValidInvitersResponse", "address"), "Address of the account being checked for valid inviters." },
        { ("PagedValidInvitersResponse", "results"), "Array of valid inviter info objects for the current page." },
        { ("PagedValidInvitersResponse", "hasMore"), "True if more results exist beyond this page." },
        { ("PagedValidInvitersResponse", "nextCursor"), "Opaque cursor for the next page." },

        // ── PagedProfileSearchResponse ──────────────────────────────────────
        { ("PagedProfileSearchResponse", "query"), "The search query that was executed." },
        { ("PagedProfileSearchResponse", "searchType"), "Search type used: 'address' (prefix match) or 'text' (name/description search)." },
        { ("PagedProfileSearchResponse", "results"), "Array of matching profile JSON objects." },
        { ("PagedProfileSearchResponse", "hasMore"), "True if more results exist beyond this page." },
        { ("PagedProfileSearchResponse", "nextCursor"), "Opaque cursor for the next page." },

        // ── HealthResponse ──────────────────────────────────────────────────
        { ("HealthResponse", "status"), "Overall health status: 'healthy', 'degraded', or 'unhealthy'." },
        { ("HealthResponse", "timestamp"), "Unix timestamp of the health check." },
        { ("HealthResponse", "database"), "Database connectivity status." },
        { ("HealthResponse", "index"), "Index synchronization status." },

        // ── TableNamespace ──────────────────────────────────────────────────
        { ("TableNamespace", "namespace"), "Schema namespace grouping related tables (e.g., 'CrcV1', 'CrcV2')." },
        { ("TableNamespace", "tables"), "Array of table definitions within this namespace." },

        // ── TableDefinition ─────────────────────────────────────────────────
        { ("TableDefinition", "table"), "Table name within the namespace." },
        { ("TableDefinition", "topic"), "Event topic this table indexes." },
        { ("TableDefinition", "columns"), "Array of column definitions for this table." },

        // ── TableColumn ─────────────────────────────────────────────────────
        { ("TableColumn", "column"), "Column name." },
        { ("TableColumn", "type"), "PostgreSQL column type (e.g., 'text', 'bigint', 'boolean')." },

        // ── TotalBalanceResponse ────────────────────────────────────────────
        { ("TotalBalanceResponse", "balance"), "Aggregated total balance in CRC wei as string." },

        // ── ProfileCidResponse ──────────────────────────────────────────────
        { ("ProfileCidResponse", "cid"), "IPFS CIDv0 string pointing to the avatar's profile JSON (null if no profile set)." },

        // ── ProfileSearchResult ─────────────────────────────────────────────
        { ("ProfileSearchResult", "total"), "Total number of matching profiles." },
        { ("ProfileSearchResult", "results"), "Array of profile search result items." },

        // ── ProfileSearchResultItem ─────────────────────────────────────────
        { ("ProfileSearchResultItem", "avatar"), "Ethereum address of the matched avatar." },
        { ("ProfileSearchResultItem", "avatarInfo"), "Avatar registration info." },
        { ("ProfileSearchResultItem", "profile"), "IPFS profile JSON (if available)." },

        // ── ProfileViewResponse ─────────────────────────────────────────────
        { ("ProfileViewResponse", "address"), "Ethereum address of the avatar." },
        { ("ProfileViewResponse", "avatarInfo"), "Full avatar registration info." },
        { ("ProfileViewResponse", "profile"), "IPFS profile JSON (if available)." },
        { ("ProfileViewResponse", "trustStats"), "Trust relationship counts (trusts/trustedBy)." },
        { ("ProfileViewResponse", "v1Balance"), "V1 CRC balance as string (null if no V1 registration)." },
        { ("ProfileViewResponse", "v2Balance"), "V2 CRC balance as string (null if no V2 registration)." },

        // ── TrustNetworkSummaryResponse ─────────────────────────────────────
        { ("TrustNetworkSummaryResponse", "address"), "Ethereum address of the queried avatar." },
        { ("TrustNetworkSummaryResponse", "directTrustsCount"), "Number of avatars this address directly trusts." },
        { ("TrustNetworkSummaryResponse", "directTrustedByCount"), "Number of avatars that directly trust this address." },
        { ("TrustNetworkSummaryResponse", "mutualTrustsCount"), "Number of mutual trust relationships." },
        { ("TrustNetworkSummaryResponse", "mutualTrusts"), "Array of addresses with mutual trust." },
        { ("TrustNetworkSummaryResponse", "networkReach"), "Estimated number of avatars reachable via transitive trust." },

        // ── AggregatedTrustRelationsResponse ────────────────────────────────
        { ("AggregatedTrustRelationsResponse", "address"), "Ethereum address of the queried avatar." },
        { ("AggregatedTrustRelationsResponse", "mutual"), "Array of mutual trust relations." },
        { ("AggregatedTrustRelationsResponse", "trusts"), "Array of outgoing trust relations." },
        { ("AggregatedTrustRelationsResponse", "trustedBy"), "Array of incoming trust relations." },

        // ── ValidInvitersResponse ───────────────────────────────────────────
        { ("ValidInvitersResponse", "address"), "Address of the account being checked." },
        { ("ValidInvitersResponse", "validInviters"), "Array of avatars that can validly invite this address." },

        // ── InviterInfo ─────────────────────────────────────────────────────
        { ("InviterInfo", "address"), "Ethereum address of the potential inviter." },
        { ("InviterInfo", "balance"), "Inviter's CRC balance relevant to invitation capability (CRC wei string)." },
        { ("InviterInfo", "avatarInfo"), "Avatar registration info for the inviter." },

        // ── EnrichedTransactionHistoryResponse ──────────────────────────────
        { ("EnrichedTransactionHistoryResponse", "address"), "Address whose transaction history was queried." },
        { ("EnrichedTransactionHistoryResponse", "transactions"), "Array of enriched transaction objects." },
        { ("EnrichedTransactionHistoryResponse", "totalCount"), "Total number of transactions matching the query." },

        // ── ProfileSearchResponse (class variant) ───────────────────────────
        { ("ProfileSearchResponse", "query"), "The search query that was executed." },
        { ("ProfileSearchResponse", "searchType"), "Search type: 'address' or 'text'." },
        { ("ProfileSearchResponse", "results"), "Array of matching profile JSON objects." },
        { ("ProfileSearchResponse", "totalCount"), "Total number of matching profiles." },

        // ── SelectDto ───────────────────────────────────────────────────────
        { ("SelectDto", "namespace"), "Database namespace to query (e.g., 'CrcV1', 'CrcV2', 'V_CrcV1', 'V_CrcV2')." },
        { ("SelectDto", "table"), "Table name within the namespace." },
        { ("SelectDto", "columns"), "Column names to return. Omit or empty for all columns." },
        { ("SelectDto", "filter"), "Array of filter predicates to apply (AND logic)." },
        { ("SelectDto", "order"), "Array of ordering directives." },
        { ("SelectDto", "limit"), "Maximum rows to return (default: 50, max: 200)." },
        { ("SelectDto", "distinct"), "When true, return only distinct rows." },

        // ── OrderByDto ──────────────────────────────────────────────────────
        { ("OrderByDto", "column"), "Column name to sort by." },
        { ("OrderByDto", "sortOrder"), "Sort direction: 'ASC' (ascending) or 'DESC' (descending)." },

        // ── FilterPredicateDto ──────────────────────────────────────────────
        { ("FilterPredicateDto", "type"), "Discriminator: always 'FilterPredicate'." },
        { ("FilterPredicateDto", "column"), "Column name to filter on." },
        { ("FilterPredicateDto", "filterType"), "Comparison operator: 'Equals', 'NotEquals', 'GreaterThan', 'LessThan', 'GreaterThanOrEquals', 'LessThanOrEquals', 'Like', 'NotLike', 'In', 'NotIn'." },
        { ("FilterPredicateDto", "value"), "Value to compare against. Type must match the column type." },

        // ── GroupQueryParams ────────────────────────────────────────────────
        { ("GroupQueryParams", "nameStartsWith"), "Filter groups whose name starts with this prefix (case-insensitive)." },
        { ("GroupQueryParams", "symbolStartsWith"), "Filter groups whose symbol starts with this prefix (case-insensitive)." },
        { ("GroupQueryParams", "ownerIn"), "Filter groups owned by any of these addresses." },

        // ── EventResponse ───────────────────────────────────────────────────
        { ("EventResponse", "blockNumber"), "Gnosis Chain block number." },
        { ("EventResponse", "transactionHash"), "Transaction hash of the event." },
        { ("EventResponse", "logIndex"), "Log index within the transaction." },
        { ("EventResponse", "event"), "Event type name." },
        { ("EventResponse", "payload"), "Event-specific payload data." },

        // ── QueryResponse ───────────────────────────────────────────────────
        { ("QueryResponse", "columns"), "Column names for the result set." },
        { ("QueryResponse", "rows"), "Array of row arrays, each containing values in column order." },
    };

    /// <summary>
    /// Post-process schemas in the cache to apply property descriptions from the static dictionary.
    /// </summary>
    private static void ApplyPropertyDescriptions()
    {
        foreach (var (schemaName, schema) in SchemaCache)
        {
            if (schema.Properties == null) continue;

            foreach (var (propName, propSchema) in schema.Properties)
            {
                if (PropertyDescriptions.TryGetValue((schemaName, propName), out var desc))
                {
                    propSchema.Description = desc;
                }
            }
        }
    }
}
