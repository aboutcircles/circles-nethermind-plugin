using System.Text.Json;

namespace Circles.Rpc.Host.Wire;

/// <summary>
/// Parsed positional parameters for <c>circles_searchProfiles</c>.
///
/// The wire format is a JSON-RPC params array, positional:
///   [0] text:      string                  (required)
///   [1] limit:     int   (default 20)      (optional)
///   [2] offset:    int   (default 0)       (optional)
///   [3] types:     string[] | null         (optional)
///   [4] groupType: string | null           (optional)
///
/// Each position is independent — a caller can pass <c>null</c> for any
/// optional slot, or omit trailing slots entirely. New params MUST be appended,
/// never inserted, to avoid silently shifting the meaning of existing callers.
/// </summary>
public sealed record SearchProfilesRequest(
    string Text,
    int Limit,
    int Offset,
    string[]? Types,
    string? GroupType);

public static class SearchProfilesRequestParser
{
    public const int DefaultLimit = 20;
    public const int DefaultOffset = 0;

    /// <summary>
    /// Parse the positional JSON-RPC params for <c>circles_searchProfiles</c>.
    /// Throws <see cref="ArgumentException"/> if the required text parameter is missing.
    /// </summary>
    public static SearchProfilesRequest Parse(JsonElement[]? parameters)
    {
        if (parameters == null || parameters.Length == 0)
        {
            throw new ArgumentException("Search text parameter is required");
        }

        string text = parameters[0].GetString() ?? "";
        int limit = DefaultLimit;
        int offset = DefaultOffset;
        string[]? types = null;
        string? groupType = null;

        if (parameters.Length > 1 && parameters[1].ValueKind != JsonValueKind.Null)
        {
            limit = parameters[1].GetInt32();
        }

        if (parameters.Length > 2 && parameters[2].ValueKind != JsonValueKind.Null)
        {
            offset = parameters[2].GetInt32();
        }

        if (parameters.Length > 3 && parameters[3].ValueKind != JsonValueKind.Null)
        {
            types = parameters[3].Deserialize<string[]>();
        }

        if (parameters.Length > 4 && parameters[4].ValueKind != JsonValueKind.Null)
        {
            groupType = parameters[4].GetString();
        }

        return new SearchProfilesRequest(text, limit, offset, types, groupType);
    }
}
