using System.Text.RegularExpressions;

namespace Circles.Common;

/// <summary>
/// Shared env-variable parsers reused across the plugin, pathfinder, and cache
/// service. Centralizing parsing prevents subtle behavioral drift between the
/// per-service copies (e.g. <c>IsNullOrEmpty</c> vs <c>RemoveEmptyEntries</c>)
/// and ensures every consumer applies the same address-shape validation.
/// </summary>
public static class EnvParsers
{
    private static readonly Regex AddressRegex =
        new(@"^0x[0-9a-f]{40}$", RegexOptions.Compiled);

    /// <summary>
    /// Parses an aggregator → sub-address map from a single environment variable.
    /// Format: <c>agg:sub1,sub2;agg2:sub3,sub4</c>. Whitespace is trimmed around
    /// each segment. All addresses are normalized to lowercase and validated
    /// against <c>^0x[0-9a-f]{40}$</c>; malformed entries throw immediately so
    /// operator typos surface at startup rather than silently degrading to
    /// single-treasury behavior in production (which over-approves mint caps).
    /// </summary>
    /// <param name="envName">Name of the env var (for error messages).</param>
    /// <param name="raw">Raw env value, or null/empty (returns empty dict).</param>
    /// <returns>Validated map; empty dict if <paramref name="raw"/> is unset.</returns>
    public static Dictionary<string, string[]> ParseAggregatorMap(string envName, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new Dictionary<string, string[]>();

        var result = new Dictionary<string, string[]>();
        var entries = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            var parts = entry.Split(':', 2);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException(
                    $"{envName}: entry '{entry}' is malformed; expected 'aggregator:sub1,sub2'");
            }

            var aggregator = parts[0].Trim().ToLowerInvariant();
            if (!AddressRegex.IsMatch(aggregator))
            {
                throw new InvalidOperationException(
                    $"{envName}: aggregator '{parts[0]}' is not a 0x-prefixed 40-char hex address");
            }

            var subs = parts[1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(addr => addr.ToLowerInvariant())
                .ToArray();
            if (subs.Length == 0)
            {
                throw new InvalidOperationException(
                    $"{envName}: aggregator '{aggregator}' has no sub-addresses");
            }
            foreach (var sub in subs)
            {
                if (!AddressRegex.IsMatch(sub))
                {
                    throw new InvalidOperationException(
                        $"{envName}: sub-address '{sub}' under '{aggregator}' is not a 0x-prefixed 40-char hex address");
                }
            }

            if (!result.TryAdd(aggregator, subs))
            {
                throw new InvalidOperationException(
                    $"{envName}: aggregator '{aggregator}' appears more than once");
            }
        }
        return result;
    }
}
