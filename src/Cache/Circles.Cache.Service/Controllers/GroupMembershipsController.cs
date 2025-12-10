using Microsoft.AspNetCore.Mvc;
using Circles.Cache.Service.Caches;
using Circles.Cache.Service.Models;

namespace Circles.Cache.Service.Controllers;

/// <summary>
/// API controller for group membership queries
/// </summary>
[ApiController]
[Route("api/groups")]
public class GroupMembershipsController : ControllerBase
{
    private readonly CacheContainer _caches;
    private readonly CacheServiceState _state;
    private readonly ILogger<GroupMembershipsController> _logger;

    public GroupMembershipsController(CacheContainer caches, CacheServiceState state, ILogger<GroupMembershipsController> logger)
    {
        _caches = caches;
        _state = state;
        _logger = logger;
    }

    private string GetRemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Get all members of a specific group
    /// </summary>
    [HttpGet("{groupAddress}/members")]
    public ActionResult<GroupMembersResponse> GetGroupMembers(string groupAddress)
    {
        _logger.LogInformation("Group members lookup for {GroupAddress} from {RemoteIp}",
            groupAddress, GetRemoteIp());

        try
        {
            var groupLower = groupAddress.ToLowerInvariant();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var members = _caches.GetGroupMembers(groupLower)
                .Select(m => new GroupMembershipResponse(
                    Group: groupLower,
                    Member: m.Member,
                    ExpiryTime: m.ExpiryTime,
                    LastProcessedBlock: lastBlock,
                    Timestamp: timestamp
                ))
                .ToArray();

            return Ok(new GroupMembersResponse(
                Group: groupLower,
                Members: members,
                LastProcessedBlock: lastBlock,
                Timestamp: timestamp
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting group members for {GroupAddress}", groupAddress);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get all groups that a member belongs to
    /// </summary>
    [HttpGet("memberships/{memberAddress}")]
    public ActionResult<MemberGroupsResponse> GetMemberGroups(string memberAddress)
    {
        _logger.LogInformation("Member groups lookup for {MemberAddress} from {RemoteIp}",
            memberAddress, GetRemoteIp());

        try
        {
            var memberLower = memberAddress.ToLowerInvariant();
            var lastBlock = _state.LastProcessedBlock;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var groups = _caches.GetMemberGroups(memberLower)
                .Select(g => new GroupMembershipResponse(
                    Group: g.Group,
                    Member: memberLower,
                    ExpiryTime: g.ExpiryTime,
                    LastProcessedBlock: lastBlock,
                    Timestamp: timestamp
                ))
                .ToArray();

            return Ok(new MemberGroupsResponse(
                Member: memberLower,
                Groups: groups,
                LastProcessedBlock: lastBlock,
                Timestamp: timestamp
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting member groups for {MemberAddress}", memberAddress);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}
