using System.Collections.Concurrent;
using System.Reflection;

namespace Circles.Index.CirclesViews;

/// <summary>
/// Utility class for lazily loading SQL queries from embedded resources
/// </summary>
public static class LazySqlLoader
{
    private static readonly ConcurrentDictionary<string, string> SqlCache = new();

    /// <summary>
    /// Loads an SQL query from an embedded resource and caches it
    /// </summary>
    /// <param name="fileName">The name of the SQL file (without path)</param>
    /// <returns>The SQL query as a string</returns>
    public static string LoadSql(string fileName)
    {
        return SqlCache.GetOrAdd(fileName, key =>
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = $"Circles.Index.CirclesViews.queries.{key}";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new FileNotFoundException($"SQL query resource not found: {resourceName}");
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
    }
}
