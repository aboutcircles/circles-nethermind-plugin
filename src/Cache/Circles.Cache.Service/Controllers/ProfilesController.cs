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
    private readonly IpfsContentCache _ipfsCache;
    private readonly CacheServiceState _state;
    private readonly ILogger<ProfilesController> _logger;
    private const int MaxBatchSize = 100;

    public ProfilesController(
        CacheContainer caches,
        IpfsContentCache ipfsCache,
        CacheServiceState state,
        ILogger<ProfilesController> logger)
    {
        _caches = caches;
        _ipfsCache = ipfsCache;
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

    /// <summary>
    /// Get profile content (IPFS payload) for a single CID
    /// </summary>
    [HttpGet("content/{cid}")]
    public async Task<ActionResult<ProfileContentResponse>> GetProfileContent(string cid)
    {
        try
        {
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var content = await _ipfsCache.GetAsync(cid);
            var cleanedContent = IpfsContentCache.StripJsonLdFields(content);

            return Ok(new ProfileContentResponse(cid, cleanedContent, lastBlock, timestamp));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting profile content for CID {Cid}", cid);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get profile content (IPFS payloads) for multiple CIDs in batch
    /// </summary>
    [HttpPost("content/batch")]
    public async Task<ActionResult<ProfileContentResponse[]>> GetProfileContentBatch([FromBody] ProfileContentBatchRequest request)
    {
        // Validate batch size
        if (request.Cids.Length > MaxBatchSize)
        {
            return BadRequest(new { error = $"Batch size exceeds limit of {MaxBatchSize} CIDs" });
        }

        _logger.LogDebug(
            "Profile content batch lookup requested for {Count} CIDs from {RemoteIp}",
            request.Cids.Length,
            GetRemoteIp());

        try
        {
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var contents = await _ipfsCache.GetBatchAsync(request.Cids);
            var results = new ProfileContentResponse[request.Cids.Length];

            for (int i = 0; i < request.Cids.Length; i++)
            {
                var cleanedContent = IpfsContentCache.StripJsonLdFields(contents[i]);
                results[i] = new ProfileContentResponse(request.Cids[i], cleanedContent, lastBlock, timestamp);
            }

            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting batch profile content");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}
