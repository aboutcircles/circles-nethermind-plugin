namespace Circles.Rpc.Host;

/// <summary>
/// Utility class for cursor-based pagination.
/// </summary>
public static class CursorUtils
{
    /// <summary>
    /// Decodes a base64-encoded cursor string into blockNumber, transactionIndex, logIndex.
    /// </summary>
    public static (long? blockNumber, int? transactionIndex, int? logIndex) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (null, null, null);
        }

        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = decoded.Split(':');
            if (parts.Length >= 3)
            {
                return (long.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
            }
        }
        catch
        {
            // Invalid cursor, ignore
        }

        return (null, null, null);
    }

    /// <summary>
    /// Decodes a base64-encoded cursor string into blockNumber, transactionIndex, logIndex, batchIndex.
    /// </summary>
    public static (long? blockNumber, int? transactionIndex, int? logIndex, int? batchIndex) DecodeCursorWithBatch(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (null, null, null, null);
        }

        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = decoded.Split(':');
            if (parts.Length >= 4)
            {
                return (long.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
            }
            else if (parts.Length >= 3)
            {
                return (long.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), 0);
            }
        }
        catch
        {
            // Invalid cursor, ignore
        }

        return (null, null, null, null);
    }

    /// <summary>
    /// Encodes blockNumber, transactionIndex, logIndex into a base64-encoded cursor string.
    /// </summary>
    public static string? EncodeCursor(long blockNumber, int transactionIndex, int logIndex)
    {
        var cursorString = $"{blockNumber}:{transactionIndex}:{logIndex}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cursorString));
    }

    /// <summary>
    /// Encodes blockNumber, transactionIndex, logIndex, batchIndex into a base64-encoded cursor string.
    /// </summary>
    public static string? EncodeCursorWithBatch(long blockNumber, int transactionIndex, int logIndex, int batchIndex)
    {
        var cursorString = $"{blockNumber}:{transactionIndex}:{logIndex}:{batchIndex}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cursorString));
    }
}
