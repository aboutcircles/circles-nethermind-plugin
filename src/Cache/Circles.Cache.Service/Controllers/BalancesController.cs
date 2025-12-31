using Microsoft.AspNetCore.Mvc;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Models;
using Circles.Common;
using System.Text.RegularExpressions;

namespace Circles.Cache.Service.Controllers;

/// <summary>
/// API controller for balance queries
/// </summary>
[ApiController]
[Route("api/balances")]
public class BalancesController : ControllerBase
{
    private readonly CacheContainer _caches;
    private readonly CacheServiceState _state;
    private readonly ILogger<BalancesController> _logger;
    private static readonly Regex EthereumAddressRegex = new Regex(@"^0x[a-fA-F0-9]{40}$", RegexOptions.Compiled);

    public BalancesController(CacheContainer caches, CacheServiceState state, ILogger<BalancesController> logger)
    {
        _caches = caches;
        _state = state;
        _logger = logger;
    }

    private string GetRemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Validates that the address is a valid Ethereum address format
    /// </summary>
    private bool IsValidEthereumAddress(string address)
    {
        return !string.IsNullOrWhiteSpace(address) && EthereumAddressRegex.IsMatch(address);
    }

    /// <summary>
    /// Converts a numeric ERC1155 token ID to a lowercase hex address.
    /// </summary>
    private static string TokenIdToAddress(string tokenId)
    {
        // If it's already a hex address, just lowercase it
        if (tokenId.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return tokenId.ToLowerInvariant();
        }

        // Parse as BigInteger and convert to hex address
        // Note: BigInteger.ToString("x") may add a leading '0' when the MSB nibble >= 8
        // to avoid interpreting the number as negative. We need exactly 40 hex chars
        // for a valid Ethereum address, so we take the rightmost 40 characters.
        var bigInt = System.Numerics.BigInteger.Parse(tokenId);
        var hex = bigInt.ToString("x");
        if (hex.Length > 40)
        {
            hex = hex.Substring(hex.Length - 40);
        }
        else
        {
            hex = hex.PadLeft(40, '0');
        }
        return "0x" + hex;
    }

    /// <summary>
    /// Get all token balances for an address
    /// </summary>
    [HttpGet("{address}")]
    public ActionResult<TokenBalanceResponse[]> GetTokenBalances(string address)
    {
        // Validate address format
        if (!IsValidEthereumAddress(address))
        {
            return BadRequest(new { error = "Invalid Ethereum address format. Expected: 0x followed by 40 hex characters" });
        }

        _logger.LogInformation("Token balance lookup requested for {Address} from {RemoteIp}", address, GetRemoteIp());

        try
        {
            var balances = new List<TokenBalanceResponse>();
            var addressLower = address.ToLowerInvariant();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Query V1 balances - use secondary index for O(1) lookup
            foreach (var tokenId in _caches.GetTokenIdsForAddress(addressLower, isV1: true))
            {
                var key = $"{addressLower}:{tokenId}";
                if (_caches.V1BalancesByAccountAndToken.TryGetValue(key, out var balance))
                {
                    // Try to get token owner
                    var tokenOwner = _caches.V1TokenOwnerByToken.TryGetValue(tokenId, out var owner)
                        ? owner
                        : null;

                    balances.Add(new TokenBalanceResponse(
                        TokenId: tokenId,
                        Balance: balance.ToString(),
                        TokenOwner: tokenOwner,
                        Version: 1,
                        TokenType: "CrcV1_Signup",
                        IsGroup: false,
                        IsErc20: true,
                        IsErc1155: false,
                        IsWrapped: false,
                        IsInflationary: true,
                        LastProcessedBlock: lastBlock,
                        Timestamp: timestamp
                    ));
                }
            }

            // Query V2 balances - use secondary index for O(1) lookup
            foreach (var tokenId in _caches.GetTokenIdsForAddress(addressLower, isV1: false))
            {
                var key = $"{addressLower}:{tokenId}";
                if (_caches.V2BalancesByAccountAndToken.TryGetValue(key, out var balance))
                {
                    // Determine token type from cached metadata
                    // V2 tokenId can be:
                    // 1. A numeric ID (ERC1155 - human or group token)
                    // 2. A hex address (ERC20 wrapper)
                    var isHexAddress = tokenId.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

                    string? tokenType = null;
                    string? tokenOwner = null;
                    bool isGroup = false;
                    bool isErc20 = false;
                    bool isErc1155 = false;
                    bool isWrapped = false;
                    bool isInflationary = false;

                    if (isHexAddress)
                    {
                        // ERC20 wrapper token - find the avatar that owns this wrapper
                        isErc20 = true;
                        isWrapped = true;

                        // O(1) reverse lookup using the pre-built index (includes circlesType)
                        var wrapperInfo = _caches.GetWrapperInfo(tokenId);

                        if (wrapperInfo.HasValue)
                        {
                            tokenOwner = wrapperInfo.Value.Avatar;
                            // circlesType: 0 = demurraged, 1 = inflationary
                            isInflationary = wrapperInfo.Value.CirclesType == 1;
                            tokenType = isInflationary
                                ? "CrcV2_ERC20WrapperDeployed_Inflationary"
                                : "CrcV2_ERC20WrapperDeployed_Demurraged";
                        }
                        else
                        {
                            // Fallback: if not found in cache, use the wrapper address itself
                            // and default to demurraged (safer assumption for balance calculations)
                            tokenOwner = tokenId.ToLowerInvariant();
                            isInflationary = false;
                            tokenType = "CrcV2_ERC20WrapperDeployed_Demurraged";
                        }
                    }
                    else
                    {
                        // ERC1155 token - numeric tokenId represents avatar address
                        // Convert tokenId to address and check if it's a human or group
                        isErc1155 = true;

                        // Convert numeric tokenId to address for lookup
                        var tokenAddress = TokenIdToAddress(tokenId);

                        // Check V2Avatars cache for Human
                        if (_caches.V2Avatars.TryGetValue(tokenAddress, out var avatarInfo))
                        {
                            isGroup = avatarInfo.Type != "Human";
                            tokenType = isGroup ? "CrcV2_RegisterGroup" : "CrcV2_RegisterHuman";
                        }
                        else if (_caches.Groups.TryGetValue(tokenAddress, out _))
                        {
                            // It's a group
                            isGroup = true;
                            tokenType = "CrcV2_RegisterGroup";
                        }
                        else
                        {
                            // Default to group if not found (safer assumption)
                            isGroup = true;
                            tokenType = "CrcV2_RegisterGroup";
                        }

                        tokenOwner = tokenAddress;
                    }

                    balances.Add(new TokenBalanceResponse(
                        TokenId: tokenId,
                        Balance: balance.ToString(),
                        TokenOwner: tokenOwner,
                        Version: 2,
                        TokenType: tokenType,
                        IsGroup: isGroup,
                        IsErc20: isErc20,
                        IsErc1155: isErc1155,
                        IsWrapped: isWrapped,
                        IsInflationary: isInflationary,
                        LastProcessedBlock: lastBlock,
                        Timestamp: timestamp
                    ));
                }
            }

            return Ok(balances.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting token balances for {Address}", address);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get total balance for an address (V1 only)
    /// Returns the balance in time-circles (with CRC→Circles conversion applied)
    /// </summary>
    [HttpGet("{address}/total/v1")]
    public ActionResult<TotalBalanceResponse> GetTotalBalanceV1(string address)
    {
        // Validate address format
        if (!IsValidEthereumAddress(address))
        {
            return BadRequest(new { error = "Invalid Ethereum address format. Expected: 0x followed by 40 hex characters" });
        }

        _logger.LogInformation("Total V1 balance lookup requested for {Address} from {RemoteIp}", address, GetRemoteIp());

        try
        {
            var addressLower = address.ToLowerInvariant();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nowUnix = (ulong)timestamp;

            // Use secondary index for O(1) lookup
            // V1 balances in cache are stored as raw CRC (personal currency)
            // We need to convert to time-circles using the inflation factor
            var totalCircles = 0m;
            foreach (var tokenId in _caches.GetTokenIdsForAddress(addressLower, isV1: true))
            {
                var key = $"{addressLower}:{tokenId}";
                if (_caches.V1BalancesByAccountAndToken.TryGetValue(key, out var crcBalance))
                {
                    // Convert from CRC (decimal) to attoCircles using time-based inflation
                    // 1. Convert decimal CRC back to attoCrc (BigInteger with 18 decimals)
                    var attoCrc = CirclesConverter.CirclesToAttoCircles(crcBalance);
                    // 2. Apply CRC→Circles conversion (applies inflation factor based on time)
                    var attoCircles = CirclesConverter.AttoCrcToAttoCircles(attoCrc, nowUnix);
                    // 3. Convert back to decimal Circles
                    var circles = CirclesConverter.AttoCirclesToCircles(attoCircles);
                    totalCircles += circles;
                }
            }

            return Ok(new TotalBalanceResponse(
                totalCircles.ToString(),
                lastBlock,
                timestamp
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting V1 total balance for {Address}", address);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get total balance for an address (V2 only)
    /// </summary>
    [HttpGet("{address}/total/v2")]
    public ActionResult<TotalBalanceResponse> GetTotalBalanceV2(string address)
    {
        // Validate address format
        if (!IsValidEthereumAddress(address))
        {
            return BadRequest(new { error = "Invalid Ethereum address format. Expected: 0x followed by 40 hex characters" });
        }

        _logger.LogInformation("Total V2 balance lookup requested for {Address} from {RemoteIp}", address, GetRemoteIp());

        try
        {
            var addressLower = address.ToLowerInvariant();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Use secondary index for O(1) lookup
            var total = 0m;
            foreach (var tokenId in _caches.GetTokenIdsForAddress(addressLower, isV1: false))
            {
                var key = $"{addressLower}:{tokenId}";
                if (_caches.V2BalancesByAccountAndToken.TryGetValue(key, out var balance))
                {
                    total += balance;
                }
            }

            return Ok(new TotalBalanceResponse(
                total.ToString(),
                lastBlock,
                timestamp
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting V2 total balance for {Address}", address);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get total balance for an address (all versions)
    /// Returns balances in time-circles (V1 CRC→Circles conversion applied)
    /// </summary>
    [HttpGet("{address}/total")]
    public ActionResult<TotalBalanceResponse> GetTotalBalance(string address)
    {
        // Validate address format
        if (!IsValidEthereumAddress(address))
        {
            return BadRequest(new { error = "Invalid Ethereum address format. Expected: 0x followed by 40 hex characters" });
        }

        _logger.LogInformation("Aggregate balance lookup requested for {Address} from {RemoteIp}", address, GetRemoteIp());

        try
        {
            var addressLower = address.ToLowerInvariant();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nowUnix = (ulong)timestamp;

            // Sum V1 balances using secondary index
            // V1 balances are stored as raw CRC, need to convert to time-circles
            var v1Total = 0m;
            foreach (var tokenId in _caches.GetTokenIdsForAddress(addressLower, isV1: true))
            {
                var key = $"{addressLower}:{tokenId}";
                if (_caches.V1BalancesByAccountAndToken.TryGetValue(key, out var crcBalance))
                {
                    // Convert from CRC (decimal) to Circles using time-based inflation
                    var attoCrc = CirclesConverter.CirclesToAttoCircles(crcBalance);
                    var attoCircles = CirclesConverter.AttoCrcToAttoCircles(attoCrc, nowUnix);
                    var circles = CirclesConverter.AttoCirclesToCircles(attoCircles);
                    v1Total += circles;
                }
            }

            // Sum V2 balances using secondary index
            // V2 balances are already in Circles units (demurraged)
            var v2Total = 0m;
            foreach (var tokenId in _caches.GetTokenIdsForAddress(addressLower, isV1: false))
            {
                var key = $"{addressLower}:{tokenId}";
                if (_caches.V2BalancesByAccountAndToken.TryGetValue(key, out var balance))
                {
                    v2Total += balance;
                }
            }

            var total = v1Total + v2Total;

            return Ok(new TotalBalanceResponse(
                total.ToString(),
                lastBlock,
                timestamp
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting total balance for {Address}", address);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}
