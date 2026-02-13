using System.Data;
using Circles.Cache.Service.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Circles.Cache.Service.Controllers;

[ApiController]
[Route("api/pathfinder")]
public class PathfinderGraphController : ControllerBase
{
    private const int SchemaVersion = 1;

    private readonly CacheServiceState _state;
    private readonly CacheServiceSettings _settings;
    private readonly ILogger<PathfinderGraphController> _logger;

    public PathfinderGraphController(
        CacheServiceState state,
        CacheServiceSettings settings,
        ILogger<PathfinderGraphController> logger)
    {
        _state = state;
        _settings = settings;
        _logger = logger;
    }

    [HttpGet("graph")]
    public async Task<IActionResult> GetGraph([FromQuery] string? format = null, [FromQuery] string? include = null)
    {
        if (!string.IsNullOrWhiteSpace(format) && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Unsupported format. Only 'json' is supported." });

        if (!_state.WarmupComplete || !_state.ListenerConnected)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Cache not ready", warmupComplete = _state.WarmupComplete, listenerConnected = _state.ListenerConnected });

        var includeSetResult = ParseInclude(include);
        if (!includeSetResult.IsValid)
            return BadRequest(new { error = includeSetResult.Error });

        var includes = includeSetResult.Values;
        var lastProcessedBlock = _state.LastProcessedBlock;

        var etag = BuildEtag(lastProcessedBlock, includes);
        var ifNoneMatch = Request.Headers.IfNoneMatch.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(ifNoneMatch) && string.Equals(ifNoneMatch, etag, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status304NotModified);

        try
        {
            await using var conn = new NpgsqlConnection(_settings.EffectiveReadonlyConnectionString);
            await conn.OpenAsync(HttpContext.RequestAborted);

            IReadOnlyList<PathfinderBalanceRow>? balances = null;
            IReadOnlyList<PathfinderTrustRow>? trust = null;
            IReadOnlyList<PathfinderGroupRow>? groups = null;
            IReadOnlyList<PathfinderGroupTrustRow>? groupTrusts = null;
            IReadOnlyList<PathfinderConsentedFlowRow>? consentedFlow = null;

            if (includes.Contains("balances"))
                balances = await LoadBalances(conn, HttpContext.RequestAborted);
            if (includes.Contains("trust"))
                trust = await LoadTrust(conn, HttpContext.RequestAborted);
            if (includes.Contains("groups"))
                groups = await LoadGroups(conn, HttpContext.RequestAborted);
            if (includes.Contains("groupTrusts"))
                groupTrusts = await LoadGroupTrusts(conn, HttpContext.RequestAborted);
            if (includes.Contains("consentedFlow"))
                consentedFlow = await LoadConsentedFlow(conn, HttpContext.RequestAborted);

            var response = new PathfinderGraphResponse(
                SchemaVersion,
                lastProcessedBlock,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                balances,
                trust,
                groups,
                groupTrusts,
                consentedFlow);

            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = "no-cache";

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build pathfinder graph snapshot");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Failed to build graph snapshot" });
        }
    }

    private static string BuildEtag(long lastProcessedBlock, ISet<string> includes)
    {
        var includeKey = string.Join(',', includes.OrderBy(x => x, StringComparer.Ordinal));
        return $"\"pf-graph-v{SchemaVersion}-{lastProcessedBlock}-{includeKey}\"";
    }

    private static (bool IsValid, string? Error, HashSet<string> Values) ParseInclude(string? include)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "balances", "trust", "groups", "groupTrusts", "consentedFlow"
        };

        if (string.IsNullOrWhiteSpace(include))
            return (true, null, new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase));

        var parsed = include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in parsed)
        {
            if (!allowed.Contains(item))
                return (false, $"Unknown include value '{item}'. Allowed: balances,trust,groups,groupTrusts,consentedFlow", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        return (true, null, parsed);
    }

    private static async Task<IReadOnlyList<PathfinderBalanceRow>> LoadBalances(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
with static_token_transfers as (
    select t.""blockNumber""
         , t.""timestamp""
         , t.""transactionIndex""
         , t.""logIndex""
         , t.""transactionHash""
         , t.""tokenAddress""
         , t.""from""
         , t.""to""
         , t.""amount""
    from ""CrcV2_Erc20WrapperTransfer"" t
             join ""CrcV2_ERC20WrapperDeployed"" d on d.""circlesType"" = 1 and d.""erc20Wrapper"" = t.""tokenAddress""
    order by t.""blockNumber"", t.""transactionIndex"", t.""logIndex""
), static_from_transfers as (
    select t1.""timestamp""
         , t1.""tokenAddress""
         , t1.""from"" as ""account""
         , -t1.""amount"" as diff
    from static_token_transfers t1
             inner join ""V_CrcV2_Avatars"" t2 on t2.avatar = t1.""from""
), static_to_transfers as (
    select t1.""timestamp""
         , t1.""tokenAddress""
         , t1.""to"" as ""account""
         , t1.""amount"" as diff
    from static_token_transfers t1
             inner join ""V_CrcV2_Avatars"" t2 on t2.avatar = t1.""to""
), static_sum as (
    select sum(diff) AS static_balance
         , account
         , ""tokenAddress""
         , max(""timestamp"") AS ""timestamp""
         , true as ""isWrapped""
         , 'static' as ""circlesType""
    from (
             select *
             from static_from_transfers
             union all
             select *
             from static_to_transfers
         ) as t
    group by account
           , ""tokenAddress""
),

demurraged_wrapped_token_transfers as (
    select t.""blockNumber""
         , t.""timestamp""
         , t.""transactionIndex""
         , t.""logIndex""
         , t.""transactionHash""
         , t.""tokenAddress""
         , t.""from""
         , t.""to""
         , t.""amount""
    from ""CrcV2_Erc20WrapperTransfer"" t
             join ""CrcV2_ERC20WrapperDeployed"" d on d.""circlesType"" = 0 and d.""erc20Wrapper"" = t.""tokenAddress""
    order by t.""blockNumber"", t.""transactionIndex"", t.""logIndex""
), demurraged_wrapped_from_transfers as (
    select t1.""timestamp""
         , t1.""tokenAddress""
         , t1.""from"" as ""account""
         , -t1.""amount"" as diff
    from demurraged_wrapped_token_transfers t1
             inner join ""V_CrcV2_Avatars"" t2 on t2.avatar = t1.""from""
), demurraged_wrapped_to_transfers as (
    select t1.""timestamp""
         , t1.""tokenAddress""
         , t1.""to"" as ""account""
         , t1.""amount"" as diff
    from demurraged_wrapped_token_transfers t1
             inner join ""V_CrcV2_Avatars"" t2 on t2.avatar = t1.""to""
), demurraged_wrapped_sum as (
    select sum(diff) AS inflationary_balance
         , account
         , ""tokenAddress""
         , max(""timestamp"") AS ""lastActivity""
         , true as ""isWrapped""
         , 'demurraged' as ""circlesType""
    from (
             select *
             from demurraged_wrapped_from_transfers
             union all
             select *
             from demurraged_wrapped_to_transfers
         ) as t
    group by account
           , ""tokenAddress""
),

all_transfers as (
    select ""static_balance"" as balance
         , ""account""
         , ""tokenAddress""
         , ""timestamp"" as ""lastActivity""
         , ""isWrapped""
         , ""circlesType""
    from static_sum
    union all
    select ""inflationary_balance"" as balance
         , ""account""
         , ""tokenAddress""
         , ""lastActivity""
         , ""isWrapped""
         , ""circlesType""
    from demurraged_wrapped_sum
    union all
    select
        ""totalBalance"" as balance
         ,""account""
         ,""tokenAddress""
         ,""lastActivity""
         ,false AS ""isWrapped""
         ,'demurraged' AS ""circlesType""
    from ""V_CrcV2_BalancesByAccountAndToken""
)

select balance::text
     , account
     , ""tokenAddress""
     , ""lastActivity""
     , ""isWrapped""
     , ""circlesType""
from all_transfers
where balance > 0
order by balance, account, ""tokenAddress"";
";

        var rows = new List<PathfinderBalanceRow>();
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandType = CommandType.Text };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PathfinderBalanceRow(
                reader.GetString(0),
                reader.GetString(1).ToLowerInvariant(),
                reader.GetString(2).ToLowerInvariant(),
                reader.GetInt64(3),
                reader.GetBoolean(4),
                reader.GetString(5)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PathfinderTrustRow>> LoadTrust(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT t1.truster, t1.trustee FROM ""V_CrcV2_TrustRelations"" t1
LEFT JOIN ""CrcV2_RegisterGroup"" t2 on t2.""group"" = t1.truster
WHERE t2.""group""  IS NULL

UNION ALL

SELECT t1.truster, t2.""erc20Wrapper"" AS trustee FROM ""V_CrcV2_TrustRelations"" t1
INNER JOIN ""CrcV2_ERC20WrapperDeployed"" t2
ON t2.avatar = t1.trustee
LEFT JOIN ""CrcV2_RegisterGroup"" t3 on t3.""group"" = t1.truster
WHERE t3.""group""  IS NULL
";

        var rows = new List<PathfinderTrustRow>();
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandType = CommandType.Text };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PathfinderTrustRow(
                reader.GetString(0).ToLowerInvariant(),
                reader.GetString(1).ToLowerInvariant(),
                100));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PathfinderGroupRow>> LoadGroups(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT
    ""group"" as group_address
FROM ""CrcV2_RegisterGroup""
WHERE ""mint"" = LOWER('0xCDFc5135AEC0aFbf102C108e7f5C8A88C6112842');
";

        var rows = new List<PathfinderGroupRow>();
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandType = CommandType.Text };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new PathfinderGroupRow(reader.GetString(0).ToLowerInvariant()));

        return rows;
    }

    private static async Task<IReadOnlyList<PathfinderGroupTrustRow>> LoadGroupTrusts(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT
    t.truster as group_address,
    t.trustee as trusted_token
FROM ""V_CrcV2_TrustRelations"" t
INNER JOIN ""CrcV2_RegisterGroup"" g ON g.""group"" = t.truster
WHERE g.""mint"" = LOWER('0xCDFc5135AEC0aFbf102C108e7f5C8A88C6112842');
";

        var rows = new List<PathfinderGroupTrustRow>();
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandType = CommandType.Text };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PathfinderGroupTrustRow(
                reader.GetString(0).ToLowerInvariant(),
                reader.GetString(1).ToLowerInvariant()));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<PathfinderConsentedFlowRow>> LoadConsentedFlow(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT DISTINCT ON (avatar) avatar, flag
FROM ""CrcV2_SetAdvancedUsageFlag""
ORDER BY avatar, ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC;
";

        var rows = new List<PathfinderConsentedFlowRow>();
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandType = CommandType.Text };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var avatar = reader.GetString(0).ToLowerInvariant();
            var flag = (byte[])reader.GetValue(1);
            var hasConsentedFlow = flag.Length >= 32 && (flag[31] & 0x01) != 0;
            rows.Add(new PathfinderConsentedFlowRow(avatar, hasConsentedFlow));
        }

        return rows;
    }
}
