using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Models;
using Circles.Common;
using Microsoft.AspNetCore.Mvc;

namespace Circles.Cache.Service.Controllers;

/// <summary>
/// API controller for trust relation queries
/// </summary>
[ApiController]
[Route("api/trust")]
public class TrustRelationsController : ControllerBase
{
    private readonly CacheContainer _caches;
    private readonly CacheServiceState _state;
    private readonly ILogger<TrustRelationsController> _logger;

    public TrustRelationsController(CacheContainer caches, CacheServiceState state, ILogger<TrustRelationsController> logger)
    {
        _caches = caches;
        _state = state;
        _logger = logger;
    }

    private string GetRemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Get all trust relations for an address (both trusts and trustedBy)
    /// </summary>
    [HttpGet("{address}")]
    public ActionResult<TrustRelationsResponse> GetTrustRelations(string address, [FromQuery] int? version = null)
    {
        _logger.LogInformation("Trust relations lookup for {Address} (version={Version}) from {RemoteIp}",
            address, version, GetRemoteIp());

        try
        {
            var addressLower = address.ToLowerInvariant();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var currentBlockTimestamp = _state.CurrentBlockTimestamp;

            var trusts = new List<TrustRelationResponse>();
            var trustedBy = new List<TrustRelationResponse>();

            // Get V1 trust relations if version is null or 1
            if (version is null or 1)
            {
                foreach (var (trustee, expiryTime) in _caches.GetTrustsFor(addressLower, isV1: true))
                {
                    trusts.Add(new TrustRelationResponse(
                        Truster: addressLower,
                        Trustee: trustee,
                        ExpiryTime: expiryTime, // V1 uses limit (0-100), stored as expiryTime
                        Version: 1,
                        LastProcessedBlock: lastBlock,
                        Timestamp: timestamp
                    ));
                }

                foreach (var (truster, expiryTime) in _caches.GetTrustedByFor(addressLower, isV1: true))
                {
                    trustedBy.Add(new TrustRelationResponse(
                        Truster: truster,
                        Trustee: addressLower,
                        ExpiryTime: expiryTime,
                        Version: 1,
                        LastProcessedBlock: lastBlock,
                        Timestamp: timestamp
                    ));
                }
            }

            // Get V2 trust relations if version is null or 2
            if (version is null or 2)
            {
                var registrations = new CacheRegistrationSet(_caches);

                foreach (var (trustee, expiryTime) in _caches.GetTrustsFor(addressLower, isV1: false))
                {
                    if (expiryTime <= currentBlockTimestamp)
                        continue;
                    // Defense-in-depth: counterparty must be registered
                    if (!registrations.IsRegistered(trustee))
                        continue;

                    trusts.Add(new TrustRelationResponse(
                        Truster: addressLower,
                        Trustee: trustee,
                        ExpiryTime: expiryTime,
                        Version: 2,
                        LastProcessedBlock: lastBlock,
                        Timestamp: timestamp
                    ));
                }

                foreach (var (truster, expiryTime) in _caches.GetTrustedByFor(addressLower, isV1: false))
                {
                    if (expiryTime <= currentBlockTimestamp)
                        continue;
                    if (!registrations.IsRegistered(truster))
                        continue;

                    trustedBy.Add(new TrustRelationResponse(
                        Truster: truster,
                        Trustee: addressLower,
                        ExpiryTime: expiryTime,
                        Version: 2,
                        LastProcessedBlock: lastBlock,
                        Timestamp: timestamp
                    ));
                }
            }

            return Ok(new TrustRelationsResponse(
                Address: addressLower,
                Trusts: trusts.ToArray(),
                TrustedBy: trustedBy.ToArray(),
                LastProcessedBlock: lastBlock,
                Timestamp: timestamp
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting trust relations for {Address}", address);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}
