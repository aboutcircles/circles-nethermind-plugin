using System.Text.Json;
using Circles.Index.Common;
using Circles.Index.Query.Dto;
using Circles.Pathfinder.DTOs;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Circles.Index.Rpc;

#region DTOs

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
    bool IsGroup);

public record CirclesTotalBalance(
    string Account,
    string AttoCircles,
    decimal Circles,
    string StaticAttoCircles,
    decimal StaticCircles,
    string AttoCrc,
    decimal Crc
);

public record CirclesTrustRelation(Address User, int limit);

public record CirclesTrustRelations(Address User, CirclesTrustRelation[] Trusts, CirclesTrustRelation[] TrustedBy);

public record CirclesEvent(string Event, IDictionary<string, object?> Values);

public record DatabaseColumn(string Column, string Type);

public class DatabaseTable(string table, string topic)
{
    public string Table { get; set; } = table;
    public string Topic { get; set; } = topic;
    public DatabaseColumn[] Columns { get; set; } = [];
}

public class DatabaseNamespace(string @namespace)
{
    public string Namespace { get; set; } = @namespace;
    public DatabaseTable[] Tables { get; set; } = [];
}

/**
 * Contains basic information about a Circles avatar.
 */
public record AvatarRow(
    /**
     * If the avatar is currently active in version 1 or 2.
     *
     * Note: An avatar that's active in v2 can still have a v1 token. See `hasV1` and `v1Token`.
     */
    int Version,
    /**
     * The type of the avatar.
     * 'CrcV2_RegisterHuman' | 'CrcV2_RegisterGroup' | 'CrcV2_RegisterOrganization' | 'CrcV1_Signup' | 'CrcV1_OrganizationSignup'
     */
    string Type,
    /**
     * The address of the avatar.
     */
    string Avatar,
    /**
     * The personal or group token address.
     *
     * Note: v1 tokens are erc20 and thus have a token address. v2 tokens are erc1155 and have a tokenId.
     *       The v2 tokenId is always an encoded version of the avatar address.
     */
    string TokenId,
    /**
     * If the avatar is signed up at v1.
     */
    bool HasV1,
    /**
     * If the avatar has a v1 token, this is the token address.
     */
    string? V1Token,
    /**
     * The bytes of the avatar's metadata cidv0.
     */
    string CidV0Digest,
    /**
     * The CIDv0 of the avatar's metadata (profile)
     */
    string CidV0,
    /**
     * Indicates whether the entity is a human.
     */
    bool IsHuman,
    /**
     * Groups have a name
     */
    string? Name,
    /**
     * Groups have a symbol
     */
    string? Symbol);

/*
 *
 * export interface IPFSDataProfile {
     name: string | null;
     description?: string;
     imageUrl?: string;
     previewImageUrl?: string;
     location?: string;
     geoLocation?: [number, number]; // [longitude, latitude]
   }
 */
public record IpfsDataProfile(
    string name,
    string? description,
    string? imageUrl,
    string? previewImageUrl,
    string? location,
    float[]? geoLocation);

/**
 * export interface Profile {
  address: string;
  CID: string;
  lastUpdatedAt: number;
  name: string | null;
  description?: string;
  registeredName: string | null;
  location?: string;
  geoLocation?: [number, number]; // [longitude, latitude]
  longitude?: number;
  latitude?: number;
}
 */
public record Profile(
    string address,
    string CID,
    long lastUpdatedAt,
    string name,
    string? description,
    string? registeredName,
    string? location,
    string? imageUrl,
    string? previewImageUrl,
    float[]? geoLocation,
    float? longitude,
    float? latitude);

#endregion

[RpcModule("Circles")]
public interface ICirclesRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Gets the V1 Circles balance of the specified address", IsImplemented = true)]
    Task<ResultWrapper<string>> circles_getTotalBalance(Address address, bool? asTimeCircles = true);

    [JsonRpcMethod(Description = "Gets the V2 Circles balance of the specified address", IsImplemented = true)]
    Task<ResultWrapper<string>> circlesV2_getTotalBalance(Address address, bool? asTimeCircles = true);

    [JsonRpcMethod(Description = "This method allows you to query all (v1) trust relations of an address",
        IsImplemented = true)]
    Task<ResultWrapper<CirclesTrustRelations>> circles_getTrustRelations(Address address);

    [JsonRpcMethod(Description = "Gets the balance of each V1 Circles token the specified address holds",
        IsImplemented = true)]
    Task<ResultWrapper<CirclesTokenBalance[]>> circles_getTokenBalances(Address address);

    [JsonRpcMethod(
        Description =
            "Gets the common trust between two addresses. If version is specified, it will only return trusts with the specified version. If version is not specified, it will return all trusts.",
        IsImplemented = true)]
    Task<ResultWrapper<Address[]>> circles_getCommonTrust(Address address1, Address address2, int? version = null);

    [JsonRpcMethod(Description = "Queries the data of one Circles index table",
        IsImplemented = true)]
    ResultWrapper<DatabaseQueryResult> circles_query(SelectDto query);

    [JsonRpcMethod(Description = "Returns all events affecting the specified account since block N",
        IsImplemented = true)]
    ResultWrapper<CirclesEvent[]> circles_events(Address? address, long? fromBlock, long? toBlock = null,
        string[]? eventTypes = null, FilterPredicateDto[]? filters = null, bool? sortAscending = false);

    [JsonRpcMethod(
        Description = "Tries to find a transitive transfer path between two addresses in the Circles V2 graph",
        IsImplemented = true)]
    Task<ResultWrapper<MaxFlowResponse>> circlesV2_findPath(FlowRequest flowRequest);

    [JsonRpcMethod(
        Description = "Checks if the database is available and indexing progresses as expected",
        IsImplemented = true)]
    ResultWrapper<string> circles_health();

    [JsonRpcMethod(
        Description = "Returns all indexed tables and columns grouped by namespace",
        IsImplemented = true)]
    Task<ResultWrapper<IEnumerable<DatabaseNamespace>>> circles_tables();


    [JsonRpcMethod(
        Description = "Queries the profile CID of a Circles avatar. Returns an error if the avatar is not found.",
        IsImplemented = true)]
    ResultWrapper<string> circles_getProfileCid(Address address);

    [JsonRpcMethod(
        Description = "Queries the profile CID of many Circles avatars. Returns 'null' for avatars that are not found.",
        IsImplemented = true)]
    ResultWrapper<List<string?>> circles_getProfileCidBatch(Address[] address);

    [JsonRpcMethod(Description = "Queries the balances of all Circles tokens an avatar has.",
        IsImplemented = true)]
    ResultWrapper<IEnumerable<CirclesTokenBalance>> circles_getBalanceBreakdown(Address address);

    [JsonRpcMethod(Description = "Queries essential information about an avatar.",
        IsImplemented = true)]
    ResultWrapper<AvatarRow> circles_getAvatarInfo(Address address);

    [JsonRpcMethod(Description = "Queries essential information about an avatar in batch.",
        IsImplemented = true)]
    ResultWrapper<AvatarRow?[]> circles_getAvatarInfoBatch(Address[] addresses);

    [JsonRpcMethod(Description = "",
        IsImplemented = true)]
    Task<ResultWrapper<Profile>> circles_getProfileByCid(string cid);

    [JsonRpcMethod(Description = "",
        IsImplemented = true)]
    Task<ResultWrapper<Profile?[]>> circles_getProfileByCidBatch(string[] cids);

    [JsonRpcMethod(Description = "",
        IsImplemented = true)]
    Task<ResultWrapper<Profile>> circles_getProfileByAddress(Address avatar);

    [JsonRpcMethod(Description = "",
        IsImplemented = true)]
    Task<ResultWrapper<Profile?[]>> circles_getProfileByAddressBatch(Address[] avatars);

    [JsonRpcMethod(Description = "", IsImplemented = true)]
    Task<ResultWrapper<TokenInfo>> circles_getTokenInfo(Address tokenAddress);

    [JsonRpcMethod(Description = "", IsImplemented = true)]
    Task<ResultWrapper<TokenInfo?[]>> circles_getTokenInfoBatch(Address[] tokenAddresses);

    [JsonRpcMethod(Description = "", IsImplemented = true)]
    public Task<ResultWrapper<JsonElement>> circles_getNetworkSnapshot();

    [JsonRpcMethod(
        Description = "Full-text search over avatar profiles (name & description)",
        IsImplemented = true)]
    Task<ResultWrapper<Profile[]>> circles_searchProfiles(
        string text, // search term(s)
        int? limit = 20, // default page size
        int? offset = 0 // pagination offset
    );
}