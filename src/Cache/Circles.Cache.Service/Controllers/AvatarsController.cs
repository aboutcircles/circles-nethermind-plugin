using Microsoft.AspNetCore.Mvc;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Models;

namespace Circles.Cache.Service.Controllers;

/// <summary>
/// API controller for avatar and token info queries
/// </summary>
[ApiController]
[Route("api/avatars")]
public class AvatarsController : ControllerBase
{
    private readonly CacheContainer _caches;
    private readonly CacheServiceState _state;
    private readonly ILogger<AvatarsController> _logger;
    private const int MaxBatchSize = 100;

    public AvatarsController(CacheContainer caches, CacheServiceState state, ILogger<AvatarsController> logger)
    {
        _caches = caches;
        _state = state;
        _logger = logger;
    }

    /// <summary>
    /// Get avatar info for a single address
    /// </summary>
    [HttpGet("{address}")]
    public ActionResult<AvatarInfoResponse> GetAvatarInfo(string address)
    {
        try
        {
            var addressLower = address.ToLowerInvariant();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Check V2 first (newer protocol)
            if (_caches.V2Avatars.TryGetValue(addressLower, out var v2Info))
            {
                var cid = _caches.V2AvatarToCidMap.TryGetValue(addressLower, out var c) ? c : null;
                var shortName = _caches.V2AvatarToShortNameMap.TryGetValue(addressLower, out var sn) ? sn : null;

                // Check if it's a group
                var isGroup = _caches.Groups.TryGetValue(addressLower, out var groupInfo);

                return Ok(new AvatarInfoResponse(
                    Avatar: address,
                    Version: 2,
                    Type: v2Info.Type,
                    CidV0: cid,
                    IsHuman: v2Info.Type.Contains("Human"),
                    Name: isGroup ? groupInfo.Name : null,
                    Symbol: null, // TODO: Add symbol to Groups cache
                    RegisteredAt: v2Info.RegisteredAt,
                    LastProcessedBlock: lastBlock,
                    Timestamp: timestamp
                ));
            }

            // Check V1
            if (_caches.V1Avatars.TryGetValue(addressLower, out var v1Info))
            {
                var cid = _caches.V1AvatarToCidMap.TryGetValue(addressLower, out var c) ? c : null;

                return Ok(new AvatarInfoResponse(
                    Avatar: address,
                    Version: 1,
                    Type: v1Info.Type,
                    TokenId: v1Info.TokenAddress,
                    HasV1: true,
                    V1Token: v1Info.TokenAddress,
                    CidV0: cid,
                    IsHuman: v1Info.Type.Contains("Signup"),
                    LastProcessedBlock: lastBlock,
                    Timestamp: timestamp
                ));
            }

            return NotFound(new { error = $"No avatar found for address {address}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting avatar info for {Address}", address);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get avatar info for multiple addresses in batch
    /// </summary>
    [HttpPost("batch")]
    public ActionResult<AvatarInfoResponse?[]> GetAvatarInfoBatch([FromBody] AvatarInfoBatchRequest request)
    {
        // Validate batch size
        if (request.Addresses.Length > MaxBatchSize)
        {
            return BadRequest(new { error = $"Batch size exceeds limit of {MaxBatchSize} addresses" });
        }

        try
        {
            var results = new List<AvatarInfoResponse?>();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var address in request.Addresses)
            {
                var addressLower = address.ToLowerInvariant();

                // Check V2 first
                if (_caches.V2Avatars.TryGetValue(addressLower, out var v2Info))
                {
                    var cid = _caches.V2AvatarToCidMap.TryGetValue(addressLower, out var c) ? c : null;
                    var isGroup = _caches.Groups.TryGetValue(addressLower, out var groupInfo);

                    results.Add(new AvatarInfoResponse(
                        Avatar: address,
                        Version: 2,
                        Type: v2Info.Type,
                        CidV0: cid,
                        IsHuman: v2Info.Type.Contains("Human"),
                        Name: isGroup ? groupInfo.Name : null,
                        RegisteredAt: v2Info.RegisteredAt,
                        LastProcessedBlock: lastBlock,
                        Timestamp: timestamp
                    ));
                }
                // Check V1
                else if (_caches.V1Avatars.TryGetValue(addressLower, out var v1Info))
                {
                    var cid = _caches.V1AvatarToCidMap.TryGetValue(addressLower, out var c) ? c : null;

                    results.Add(new AvatarInfoResponse(
                        Avatar: address,
                        Version: 1,
                        Type: v1Info.Type,
                        TokenId: v1Info.TokenAddress,
                        HasV1: true,
                        V1Token: v1Info.TokenAddress,
                        CidV0: cid,
                        IsHuman: v1Info.Type.Contains("Signup"),
                        LastProcessedBlock: lastBlock,
                        Timestamp: timestamp
                    ));
                }
                else
                {
                    results.Add(null);
                }
            }

            return Ok(results.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting batch avatar info");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}
