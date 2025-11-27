using Circles.Index.Common;
using SchemaProvider = Circles.Index.DatabaseSchemaProvider.Schemas;

namespace Circles.Rpc.Host;

/// <summary>
/// Provides a comprehensive mapping of all database tables and their address/filterable columns.
/// This is used by GetEvents and other RPC methods to dynamically query the database schema.
/// </summary>
public static class DatabaseSchemaMap
{
    /// <summary>
    /// Maps table names to their address-containing columns.
    /// Key: Table name (e.g., "CrcV1_Signup")
    /// Value: Array of column names that contain addresses
    /// </summary>
    public static readonly Dictionary<string, string[]> TableAddressColumns =
        SchemaProvider.AllSchemas
            .SelectMany(s => s.Tables)
            .GroupBy(kvp => $"{kvp.Key.Namespace}_{kvp.Key.Table}")
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(kvp => kvp.Value.Columns)
                    .Where(f => f.Type == ValueTypes.Address)
                    .Select(f => f.Column)
                    .Distinct()
                    .ToArray());

    /// <summary>
    /// Maps table names to all their columns.
    /// Key: Table name
    /// Value: Dictionary of column names to their types
    /// </summary>
    public static readonly Dictionary<string, Dictionary<string, string>> TableColumns =
        SchemaProvider.AllSchemas
            .SelectMany(s => s.Tables)
            .GroupBy(kvp => $"{kvp.Key.Namespace}_{kvp.Key.Table}")
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(kvp => kvp.Value.Columns)
                    .DistinctBy(c => c.Column)
                    .ToDictionary(c => c.Column, c => c.Type.ToString()));


    /// <summary>
    /// Gets all table names that are queryable.
    /// </summary>
    public static IEnumerable<string> AllTables => TableAddressColumns.Keys;

    /// <summary>
    /// Gets address columns for a specific table.
    /// </summary>
    public static string[] GetAddressColumns(string tableName)
    {
        return TableAddressColumns.TryGetValue(tableName, out var columns) ? columns : Array.Empty<string>();
    }

    /// <summary>
    /// Checks if a table exists in the schema.
    /// </summary>
    public static bool TableExists(string tableName)
    {
        return TableAddressColumns.ContainsKey(tableName);
    }

    /// <summary>
    /// Gets all columns for a specific table.
    /// </summary>
    public static Dictionary<string, string>? GetTableColumns(string tableName)
    {
        return TableColumns.TryGetValue(tableName, out var columns) ? columns : null;
    }
}
