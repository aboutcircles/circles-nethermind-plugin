using Microsoft.AspNetCore.Mvc;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Models;

namespace Circles.Cache.Service.Controllers;

/// <summary>
/// API controller for profile queries
/// </summary>
[ApiController]
[Route("api/profiles")]
public class ProfilesController : ControllerBase
{
    private readonly CacheContainer _caches;
    private readonly CacheServiceState _state;
    private readonly ILogger<ProfilesController> _logger;
    private const int MaxBatchSize = 100;

    public ProfilesController(CacheContainer caches, CacheServiceState state, ILogger<ProfilesController> logger)
    {
        _caches = caches;
        _state = state;
        _logger = logger;
    }

    private string GetRemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Get profile CID for a single address
    /// </summary>
    [HttpGet("{address}/cid")]
    public ActionResult<ProfileCidResponse> GetProfileCid(string address)
    {
        _logger.LogInformation("Profile CID lookup requested for {Address} from {RemoteIp}", address, GetRemoteIp());
        try
        {
            var addressLower = address.ToLowerInvariant();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Check V2 first
            if (_caches.V2AvatarToCidMap.TryGetValue(addressLower, out var cidV2))
            {
                return Ok(new ProfileCidResponse(cidV2, lastBlock, timestamp));
            }

            // Check V1
            if (_caches.V1AvatarToCidMap.TryGetValue(addressLower, out var cidV1))
            {
                return Ok(new ProfileCidResponse(cidV1, lastBlock, timestamp));
            }

            return Ok(new ProfileCidResponse(null, lastBlock, timestamp));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting profile CID for {Address}", address);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get profile CIDs for multiple addresses in batch
    /// </summary>
    [HttpPost("cid/batch")]
    public ActionResult<ProfileCidResponse[]> GetProfileCidBatch([FromBody] ProfileCidBatchRequest request)
    {
        // Validate batch size
        if (request.Addresses.Length > MaxBatchSize)
        {
            return BadRequest(new { error = $"Batch size exceeds limit of {MaxBatchSize} addresses" });
        }

        _logger.LogInformation(
            "Profile CID batch lookup requested for {Count} addresses from {RemoteIp}",
            request.Addresses.Length,
            GetRemoteIp());

        try
        {
            var results = new List<ProfileCidResponse>();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var address in request.Addresses)
            {
                var addressLower = address.ToLowerInvariant();

                // Check V2 first
                if (_caches.V2AvatarToCidMap.TryGetValue(addressLower, out var cidV2))
                {
                    results.Add(new ProfileCidResponse(cidV2, lastBlock, timestamp));
                }
                // Check V1
                else if (_caches.V1AvatarToCidMap.TryGetValue(addressLower, out var cidV1))
                {
                    results.Add(new ProfileCidResponse(cidV1, lastBlock, timestamp));
                }
                else
                {
                    results.Add(new ProfileCidResponse(null, lastBlock, timestamp));
                }
            }

            return Ok(results.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting batch profile CIDs");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}
