using Circles.Index.Common;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Circles.Index.CirclesViews;

public class DatabaseSchema : IDatabaseSchema
{
    // Define the order of view dependencies
    private static readonly (string Namespace, string Table)[] ViewDependencyOrder = new[]
    {
        // Base views come first
        ("V_CrcV1", "Avatars"),
        ("V_CrcV2", "Avatars"),
        ("V_Crc", "Avatars"),

        // Next tier of views
        ("V_CrcV1", "TrustRelations"),
        ("V_CrcV2", "TrustRelations"),
        ("V_Crc", "TrustRelations"),
        ("V_Crc", "Tokens"),

        // Transfers and related views
        ("V_CrcV1", "Transfers"),
        ("V_CrcV2", "Transfers"),
        ("V_Crc", "Transfers"),
        ("V_Crc", "TransferSummary"),

        // Balance views
        ("V_CrcV1", "BalancesByAccountAndToken"),
        ("V_CrcV2", "BalancesByAccountAndToken"),

        // Other views
        ("V_CrcV2", "Groups"),
        ("V_CrcV2", "GroupMemberships"),
        ("V_CrcV2", "GroupVaultBalancesByToken"),
        ("V_CrcV1", "TotalSupply"),
        ("V_CrcV2", "TotalSupply"),
        ("V_Crc", "Stats"),
        ("V_CrcV2", "GroupMembersCount_1h"),
        ("V_CrcV2", "GroupMembersCount_1d"),

        ("V_CrcV2", "GroupTokenHoldersBalance"),
        ("V_CrcV2", "GroupTokenSupply"),
        ("V_CrcV2", "GroupCollateralDiffByToken"),
        ("V_CrcV2", "GroupCollateralByToken"),

        ("V_CrcV2", "Erc20BalancerVaultBalance_1h"),
        ("V_CrcV2", "Erc20BalancerVaultBalance_1d"),

        ("V_CrcV2", "GroupMintRedeem_1h"),
        ("V_CrcV2", "GroupMintRedeem_1d"),

        ("V_CrcV2", "GroupWrapUnWrap_1h"),
        ("V_CrcV2", "GroupWrapUnWrap_1d"),

        ("V_CrcV2", "AffiliateMembersCount_1h"),
        ("V_CrcV2", "AffiliateMembersCount_1d")
    };

    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();
    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();
    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; }

    public IDictionary<string, string> Indexes { get; } =
        new Dictionary<string, string>
        {
            // These indexes are used by the CirclesRpcModule's GetTokenExposureIds method:
            {
                "idx_CrcV1_Transfer_to_tokenAddress",
                "CREATE INDEX IF NOT EXISTS \"idx_CrcV1_Transfer_to_tokenAddress\" ON public.\"CrcV1_Transfer\" (\"to\", \"tokenAddress\");"
            },
            {
                "idx_CrcV2_TransferSingle_to_tokenAddress",
                "CREATE INDEX IF NOT EXISTS \"idx_CrcV2_TransferSingle_to_tokenAddress\" ON public.\"CrcV2_TransferSingle\" (\"to\", \"tokenAddress\");"
            },
            {
                "idx_CrcV2_TransferBatch_to_tokenAddress",
                "CREATE INDEX IF NOT EXISTS \"idx_CrcV2_TransferBatch_to_tokenAddress\" ON public.\"CrcV2_TransferBatch\" (\"to\", \"tokenAddress\");"
            },
            {
                "idx_CrcV2_Erc20WrapperTransfer_to_tokenAddress",
                "CREATE INDEX IF NOT EXISTS \"idx_CrcV2_Erc20WrapperTransfer_to_tokenAddress\" ON public.\"CrcV2_Erc20WrapperTransfer\" (\"to\", \"tokenAddress\");"
            },
            // Used in the V_CrcV1_Avatars view
            {
                "idx_CrcV1_UpdateMetadataDigest_avatar_cursor_desc",
                "create index if not exists \"idx_CrcV1_UpdateMetadataDigest_avatar_cursor_desc\" on \"CrcV1_UpdateMetadataDigest\" (avatar, \"blockNumber\" desc, \"transactionIndex\" desc, \"logIndex\" desc);"
            },
            // Used in the V_CrcV2_Avatars view
            {
                "idx_CrcV2_UpdateMetadataDigest_avatar_cursor_desc",
                "create index if not exists \"idx_CrcV2_UpdateMetadataDigest_avatar_cursor_desc\" on \"CrcV2_UpdateMetadataDigest\" (avatar, \"blockNumber\" desc, \"transactionIndex\" desc, \"logIndex\" desc);"
            }
        };

    public DatabaseSchema()
    {
        Tables = DiscoverAndBuildSchemas();
    }

    private IDictionary<(string Namespace, string Table), EventSchema> DiscoverAndBuildSchemas()
    {
        // Use an ordered dictionary to maintain insertion order
        var result = new Dictionary<(string Namespace, string Table), EventSchema>();

        // Discover all SQL resources
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("Circles.Index.CirclesViews.queries.") && name.EndsWith(".sql"))
            .ToList();

        // Create a mapping of (namespace, table) to resource name
        var resourceMapping = new Dictionary<(string Namespace, string Table), string>();
        foreach (var resourceName in resourceNames)
        {
            var schemaInfo = ExtractSchemaInfoFromResourceName(resourceName);
            if (schemaInfo != null)
            {
                resourceMapping[schemaInfo.Value] = resourceName;
            }
        }

        // First, build schemas in the predefined order
        foreach (var key in ViewDependencyOrder)
        {
            if (resourceMapping.TryGetValue(key, out var resourceName))
            {
                var schema = BuildSchemaFromSql(key.Namespace, key.Table, resourceName);
                result[key] = schema;

                // Remove from mapping to track which ones we've processed
                resourceMapping.Remove(key);
            }
        }

        // Then, add any remaining schemas that weren't in the predefined order
        foreach (var (key, resourceName) in resourceMapping)
        {
            var schema = BuildSchemaFromSql(key.Namespace, key.Table, resourceName);
            result[key] = schema;
        }

        return result;
    }

    private (string Namespace, string Table)? ExtractSchemaInfoFromResourceName(string resourceName)
    {
        // Extract the file name without path and extension
        string fileName = resourceName.Replace("Circles.Index.CirclesViews.queries.", "").Replace(".sql", "");

        // Pattern to match view names like V_CrcV2_Avatars, V_Crc_Stats, etc.
        var pattern = new Regex(@"^(V_[A-Za-z0-9]+)_(.+)$");
        var match = pattern.Match(fileName);

        if (match.Success)
        {
            string ns = match.Groups[1].Value;
            string table = match.Groups[2].Value;
            return (ns, table);
        }

        return null;
    }

    private EventSchema BuildSchemaFromSql(string ns, string table, string resourceName)
    {
        // Extract SQL content
        string sqlContent = LoadSqlFromResource(resourceName.Replace("Circles.Index.CirclesViews.queries.", ""));

        // Try to extract columns from SQL comments first
        var columns = ParseColumnsFromMultilineComments(sqlContent);

        // If no columns were found in comments, try to parse from the SQL itself
        if (columns.Count == 0)
        {
            columns = ParseColumnsFromSql(sqlContent);
        }

        // For the migration SQL, we need to strip the column definition comments
        // to avoid SQL syntax errors when executing the script
        string cleanSqlForMigration = StripColumnDefinitionComments(sqlContent);

        // Create the schema
        var schema = new EventSchema(ns, table, new byte[32], columns)
        {
            SqlMigrationItem = new SqlMigrationItem(cleanSqlForMigration)
        };

        return schema;
    }

    /// <summary>
    /// Strips the column definition comments from the SQL to avoid syntax errors
    /// when executing the SQL script directly
    /// </summary>
    private string StripColumnDefinitionComments(string sql)
    {
        // Remove the COLUMNS: header line
        sql = Regex.Replace(sql, @"--\s*COLUMNS:\s*(\r?\n|$)", "", RegexOptions.Multiline);

        // Remove any column definition lines
        sql = Regex.Replace(sql, @"--\s*[^:]+:ValueTypes\.[^:]+:(true|false)(?::(true|false))?(\r?\n|$)", "",
            RegexOptions.Multiline);

        return sql;
    }

    private List<EventFieldSchema> ParseColumnsFromMultilineComments(string sql)
    {
        var columns = new List<EventFieldSchema>();

        // Look for column definitions in a multiline comment block
        // Format:
        // -- COLUMNS:
        // -- columnName:ValueTypes.Type:isRequired[:isNullable]
        // -- columnName2:ValueTypes.Type2:isRequired2[:isNullable2]
        // -- ...

        // First check if we have a COLUMNS: marker
        var headerPattern = new Regex(@"--\s*COLUMNS:\s*$", RegexOptions.Multiline);
        var headerMatch = headerPattern.Match(sql);

        if (headerMatch.Success)
        {
            // Find the position of the header
            int headerPos = headerMatch.Index + headerMatch.Length;

            // Extract all column definition lines that follow
            // This regex now explicitly captures the optional 4th parameter
            var columnLinePattern = new Regex(@"--\s*(.*?):(ValueTypes\.[A-Za-z]+):(true|false)(?::(true|false))?",
                RegexOptions.Multiline);

            var matches = columnLinePattern.Matches(sql, headerPos);

            foreach (Match match in matches)
            {
                if (match.Groups.Count >= 4)
                {
                    string columnName = match.Groups[1].Value.Trim();
                    string valueTypeStr = match.Groups[2].Value.Trim();
                    bool isRequired = bool.Parse(match.Groups[3].Value.Trim());

                    // The 4th group (isNullable) is optional
                    bool isNullable = match.Groups.Count >= 5 && match.Groups[4].Success &&
                                      bool.Parse(match.Groups[4].Value.Trim());

                    // Parse the ValueTypes enum
                    if (Enum.TryParse(valueTypeStr.Replace("ValueTypes.", ""), out ValueTypes valueType))
                    {
                        // Create EventFieldSchema instance
                        columns.Add(new EventFieldSchema(columnName, valueType, isRequired, isNullable));
                    }
                }
            }
        }

        return columns;
    }

    private List<EventFieldSchema> ParseColumnsFromSql(string sql)
    {
        var columns = new List<EventFieldSchema>();

        // Try to parse the column information from CREATE or REPLACE VIEW statement
        var pattern = new Regex(@"(?:create\s+or\s+replace\s+view\s+.*?\s*\(\s*)(.*?)(?:\)\s+as)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var match = pattern.Match(sql);
        if (match.Success)
        {
            string columnDefinitions = match.Groups[1].Value;
            var columnPairs = columnDefinitions.Split(',')
                .Select(c => c.Trim())
                .Where(c => !string.IsNullOrEmpty(c));

            foreach (var columnPair in columnPairs)
            {
                // Clean up quotes and extract column name
                string columnName = columnPair
                    .Replace("\"", "")
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "";

                if (!string.IsNullOrEmpty(columnName))
                {
                    // Try to infer the type based on naming conventions
                    var valueType = InferValueTypeFromColumnName(columnName);

                    // Check if this is likely a nullable column based on naming conventions
                    bool isNullable = InferIsNullableFromColumnName(columnName);

                    // Create EventFieldSchema instance
                    columns.Add(new EventFieldSchema(columnName, valueType, true, isNullable));
                }
            }
        }

        // If no columns were found or parsing failed, add a default set of common columns
        if (columns.Count == 0)
        {
            columns.Add(new EventFieldSchema("blockNumber", ValueTypes.Int, true));
            columns.Add(new EventFieldSchema("timestamp", ValueTypes.Int, true));
            columns.Add(new EventFieldSchema("transactionIndex", ValueTypes.Int, true));
            columns.Add(new EventFieldSchema("logIndex", ValueTypes.Int, true));
            columns.Add(new EventFieldSchema("transactionHash", ValueTypes.String, true));
        }

        return columns;
    }

    private ValueTypes InferValueTypeFromColumnName(string columnName)
    {
        // Simple rule-based type inference
        columnName = columnName.ToLower();

        if (columnName.Contains("address") || columnName == "from" || columnName == "to" ||
            columnName == "operator" || columnName == "truster" || columnName == "trustee" ||
            columnName == "avatar" || columnName == "token" || columnName == "tokenaddress" ||
            columnName == "group")
        {
            return ValueTypes.Address;
        }

        if (columnName.Contains("amount") || columnName.Contains("balance") ||
            columnName.Contains("value") || columnName.Contains("supply") ||
            columnName == "id" || columnName.EndsWith("id") ||
            columnName.Contains("time") && !columnName.Equals("timestamp"))
        {
            return ValueTypes.BigInt;
        }

        if (columnName == "blocknumber" || columnName == "timestamp" ||
            columnName == "transactionindex" || columnName == "logindex" ||
            columnName == "batchindex" || columnName == "limit" || columnName == "version" ||
            columnName == "count" || columnName.Contains("count"))
        {
            return ValueTypes.Int;
        }

        if (columnName.Contains("digest") || columnName.Contains("bytes"))
        {
            return ValueTypes.Bytes;
        }

        if (columnName == "events" || columnName.Contains("json"))
        {
            return ValueTypes.Json;
        }

        // Default to string for everything else
        return ValueTypes.String;
    }

    private bool InferIsNullableFromColumnName(string columnName)
    {
        // Special case for batchIndex which is known to be nullable
        if (columnName.ToLower() == "batchindex")
        {
            return true;
        }

        // By default, assume columns are not nullable
        return false;
    }

    private string LoadSqlFromResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fullResourceName = $"Circles.Index.CirclesViews.queries.{resourceName}";

        using var stream = assembly.GetManifestResourceStream(fullResourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"SQL query resource not found: {fullResourceName}");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}