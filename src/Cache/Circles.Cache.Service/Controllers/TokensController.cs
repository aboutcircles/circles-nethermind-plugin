using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Models;
using Microsoft.AspNetCore.Mvc;

namespace Circles.Cache.Service.Controllers;

/// <summary>
/// API controller for token info queries
/// </summary>
[ApiController]
[Route("api/tokens")]
public class TokensController : ControllerBase
{
    private readonly CacheContainer _caches;
    private readonly CacheServiceState _state;
    private readonly ILogger<TokensController> _logger;
    private const int MaxBatchSize = 1000;

    public TokensController(CacheContainer caches, CacheServiceState state, ILogger<TokensController> logger)
    {
        _caches = caches;
        _state = state;
        _logger = logger;
    }

    private string GetRemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Get token info for a single token address
    /// </summary>
    [HttpGet("{tokenAddress}")]
    public ActionResult<TokenInfoResponse> GetTokenInfo(string tokenAddress)
    {
        _logger.LogDebug("Token info lookup requested for {TokenAddress} from {RemoteIp}", tokenAddress, GetRemoteIp());
        try
        {
            var tokenAddressLower = tokenAddress.ToLowerInvariant();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var tokenInfo = LookupTokenInfo(tokenAddressLower, lastBlock, timestamp);
            if (tokenInfo == null)
            {
                return NotFound(new { error = $"No token info found for address {tokenAddress}" });
            }

            return Ok(tokenInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting token info for {TokenAddress}", tokenAddress);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get token info for multiple token addresses in batch
    /// </summary>
    [HttpPost("batch")]
    public ActionResult<TokenInfoResponse?[]> GetTokenInfoBatch([FromBody] TokenInfoBatchRequest request)
    {
        if (request.TokenAddresses.Length > MaxBatchSize)
        {
            return BadRequest(new { error = $"Batch size exceeds limit of {MaxBatchSize} addresses" });
        }

        _logger.LogDebug(
            "Token info batch lookup requested for {Count} addresses from {RemoteIp}",
            request.TokenAddresses.Length,
            GetRemoteIp());

        try
        {
            var results = new List<TokenInfoResponse?>();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var tokenAddress in request.TokenAddresses)
            {
                var tokenAddressLower = tokenAddress.ToLowerInvariant();
                results.Add(LookupTokenInfo(tokenAddressLower, lastBlock, timestamp));
            }

            return Ok(results.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting batch token info");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Lookup token info from the cache.
    /// Token can be:
    /// - V1 token (ERC20): lookup in V1TokenOwnerByToken
    /// - V2 avatar/group token (ERC1155): lookup in V2Avatars
    /// - V2 ERC20 wrapper: lookup in Erc20WrapperAddresses
    /// </summary>
    private TokenInfoResponse? LookupTokenInfo(string tokenAddressLower, long lastBlock, long timestamp)
    {
        // 1. Check V1 token (ERC20) - key is token address, value is owner address
        if (_caches.V1TokenOwnerByToken.TryGetValue(tokenAddressLower, out var v1TokenOwner))
        {
            return new TokenInfoResponse(
                TokenAddress: tokenAddressLower,
                TokenOwner: v1TokenOwner,
                TokenType: "CrcV1_Signup",
                Version: 1,
                IsErc20: true,
                IsErc1155: false,
                IsWrapped: false,
                IsInflationary: true,
                IsGroup: false,
                LastProcessedBlock: lastBlock,
                Timestamp: timestamp
            );
        }

        // 2. Check V2 Avatar token (ERC1155) - avatar address IS the token address
        // V2Avatars stores full type names (CrcV2_RegisterHuman / CrcV2_RegisterOrganization)
        if (_caches.V2Avatars.TryGetValue(tokenAddressLower, out var v2Avatar))
        {
            var isGroup = false; // V2Avatars only contains humans and organizations, not groups
            var tokenType = v2Avatar.Type;
            return new TokenInfoResponse(
                TokenAddress: tokenAddressLower,
                TokenOwner: tokenAddressLower, // For V2, token owner is the avatar itself
                TokenType: tokenType,
                Version: 2,
                IsErc20: false,
                IsErc1155: true,
                IsWrapped: false,
                IsInflationary: false,
                IsGroup: isGroup,
                LastProcessedBlock: lastBlock,
                Timestamp: timestamp
            );
        }

        // 3. Check V2 ERC20 Wrapper
        if (_caches.Erc20WrapperAddresses.TryGetValue(tokenAddressLower, out var wrapperInfo))
        {
            // circlesType: 0 = demurraged, 1 = inflationary
            var isInflationary = wrapperInfo.CirclesType == 1;
            var tokenType = isInflationary
                ? "CrcV2_ERC20WrapperDeployed_Inflationary"
                : "CrcV2_ERC20WrapperDeployed_Demurraged";

            // Check if the underlying token owner is a group
            var isGroup = _caches.Groups.ContainsKey(wrapperInfo.Avatar.ToLowerInvariant());

            return new TokenInfoResponse(
                TokenAddress: tokenAddressLower,
                TokenOwner: wrapperInfo.Avatar,
                TokenType: tokenType,
                Version: 2,
                IsErc20: true,
                IsErc1155: false,
                IsWrapped: true,
                IsInflationary: isInflationary,
                IsGroup: isGroup,
                LastProcessedBlock: lastBlock,
                Timestamp: timestamp
            );
        }

        // 4. Check Groups directly (in case it's not in V2Avatars)
        if (_caches.Groups.TryGetValue(tokenAddressLower, out _))
        {
            return new TokenInfoResponse(
                TokenAddress: tokenAddressLower,
                TokenOwner: tokenAddressLower,
                TokenType: "CrcV2_RegisterGroup",
                Version: 2,
                IsErc20: false,
                IsErc1155: true,
                IsWrapped: false,
                IsInflationary: false,
                IsGroup: true,
                LastProcessedBlock: lastBlock,
                Timestamp: timestamp
            );
        }

        return null;
    }
}
