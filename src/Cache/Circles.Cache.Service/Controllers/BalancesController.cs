using Microsoft.AspNetCore.Mvc;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Models;
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

    /// <summary>
    /// Validates that the address is a valid Ethereum address format
    /// </summary>
    private bool IsValidEthereumAddress(string address)
    {
        return !string.IsNullOrWhiteSpace(address) && EthereumAddressRegex.IsMatch(address);
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
                    balances.Add(new TokenBalanceResponse(
                        TokenId: tokenId,
                        Balance: balance.ToString(),
                        TokenOwner: null,
                        Version: 2,
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
    /// </summary>
    [HttpGet("{address}/total/v1")]
    public ActionResult<TotalBalanceResponse> GetTotalBalanceV1(string address)
    {
        // Validate address format
        if (!IsValidEthereumAddress(address))
        {
            return BadRequest(new { error = "Invalid Ethereum address format. Expected: 0x followed by 40 hex characters" });
        }

        try
        {
            var addressLower = address.ToLowerInvariant();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Use secondary index for O(1) lookup
            var total = 0m;
            foreach (var tokenId in _caches.GetTokenIdsForAddress(addressLower, isV1: true))
            {
                var key = $"{addressLower}:{tokenId}";
                if (_caches.V1BalancesByAccountAndToken.TryGetValue(key, out var balance))
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
    /// </summary>
    [HttpGet("{address}/total")]
    public ActionResult<TotalBalanceResponse> GetTotalBalance(string address)
    {
        // Validate address format
        if (!IsValidEthereumAddress(address))
        {
            return BadRequest(new { error = "Invalid Ethereum address format. Expected: 0x followed by 40 hex characters" });
        }

        try
        {
            var addressLower = address.ToLowerInvariant();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Sum V1 balances using secondary index
            var v1Total = 0m;
            foreach (var tokenId in _caches.GetTokenIdsForAddress(addressLower, isV1: true))
            {
                var key = $"{addressLower}:{tokenId}";
                if (_caches.V1BalancesByAccountAndToken.TryGetValue(key, out var balance))
                {
                    v1Total += balance;
                }
            }

            // Sum V2 balances using secondary index
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
