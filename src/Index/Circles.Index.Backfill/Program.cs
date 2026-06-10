using System.CommandLine;
using Circles.Index.Backfill;

// Root command
var rootCommand = new RootCommand("Circles Index Backfill Tool - Populate specific tables without full reindex");

/// <summary>
/// Gets the PostgreSQL connection string from environment variables.
/// Tries POSTGRES_CONNECTION_STRING first, then constructs from individual components.
/// </summary>
string? GetConnectionString(string? explicitValue)
{
    // Explicit value takes precedence
    if (!string.IsNullOrEmpty(explicitValue))
        return explicitValue;

    // Try full connection string env var
    var connString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
    if (!string.IsNullOrEmpty(connString))
        return connString;

    // Try to construct from individual components
    var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
    var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
    var db = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? "postgres";
    var user = Environment.GetEnvironmentVariable("POSTGRES_USER");
    var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");

    // Need at least user and password to construct
    if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        return null;

    return $"Server={host};Port={port};Database={db};User Id={user};Password={password};";
}

const string connectionStringHelp =
    "PostgreSQL connection string (or set POSTGRES_CONNECTION_STRING, or POSTGRES_USER+POSTGRES_PASSWORD env vars)";

void PrintConnectionStringError()
{
    Console.Error.WriteLine("Error: Connection string required.");
    Console.Error.WriteLine("  Use --connection-string, or set POSTGRES_CONNECTION_STRING,");
    Console.Error.WriteLine("  or set POSTGRES_USER + POSTGRES_PASSWORD (+ optional POSTGRES_HOST, POSTGRES_PORT, POSTGRES_DB)");
}

// Backfill command
var backfillCommand = new Command("backfill", "Backfill specific tables from historical blocks");

var tablesOption = new Option<string[]>("--tables", "-t")
{
    Description = "Tables to backfill (e.g., CrcV2_FlowEdgesScopeSingleStarted)",
    Required = true,
    AllowMultipleArgumentsPerToken = true
};

var fromBlockOption = new Option<long>("--from-block", "-f")
{
    Description = "Block number to start backfill from (default: 37534026 for V2)",
    DefaultValueFactory = _ => 37534026
};

var toBlockOption = new Option<long?>("--to-block", "-e")
{
    Description = "Block number to end backfill at (default: current System_Block max)"
};

var connectionStringOption = new Option<string?>("--connection-string", "-c")
{
    Description = connectionStringHelp
};

var rpcUrlOption = new Option<string>("--rpc-url", "-r")
{
    Description = "Nethermind JSON-RPC URL",
    DefaultValueFactory = _ => "http://localhost:8545"
};

var batchSizeOption = new Option<int>("--batch-size", "-b")
{
    Description = "Number of blocks to process per batch",
    DefaultValueFactory = _ => 1000
};

var dryRunOption = new Option<bool>("--dry-run")
{
    Description = "Parse blocks but don't write to database"
};

var hubAddressOption = new Option<string?>("--hub-address")
{
    Description = "V2 Hub contract address (default: 0xc12c1e50abb450d6205ea2c3fa861b3b834d13e8 for Gnosis)"
};

var forceOption = new Option<bool>("--force")
{
    Description = "Bypass safety check that verifies the indexer is not running"
};

backfillCommand.Options.Add(tablesOption);
backfillCommand.Options.Add(fromBlockOption);
backfillCommand.Options.Add(toBlockOption);
backfillCommand.Options.Add(connectionStringOption);
backfillCommand.Options.Add(rpcUrlOption);
backfillCommand.Options.Add(batchSizeOption);
backfillCommand.Options.Add(dryRunOption);
backfillCommand.Options.Add(hubAddressOption);
backfillCommand.Options.Add(forceOption);

backfillCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var tables = parseResult.GetValue(tablesOption)!;
    var fromBlock = parseResult.GetValue(fromBlockOption);
    var toBlock = parseResult.GetValue(toBlockOption);
    var connectionString = GetConnectionString(parseResult.GetValue(connectionStringOption));
    var rpcUrl = parseResult.GetValue(rpcUrlOption)!;
    var batchSize = parseResult.GetValue(batchSizeOption);
    var dryRun = parseResult.GetValue(dryRunOption);
    var hubAddress = parseResult.GetValue(hubAddressOption);
    var force = parseResult.GetValue(forceOption);

    if (string.IsNullOrEmpty(connectionString))
    {
        PrintConnectionStringError();
        return 1;
    }

    var options = new BackfillOptions
    {
        Tables = tables,
        FromBlock = fromBlock,
        ToBlock = toBlock,
        ConnectionString = connectionString,
        RpcUrl = rpcUrl,
        BatchSize = batchSize,
        DryRun = dryRun,
        V2HubAddress = hubAddress,
        Force = force
    };

    var runner = new BackfillRunner(options);
    return await runner.RunAsync(cancellationToken);
});

// List-tables command
var listTablesCommand = new Command("list-tables", "List all available tables and their LogParser mappings");

var listConnectionStringOption = new Option<string?>("--connection-string", "-c")
{
    Description = connectionStringHelp
};

listTablesCommand.Options.Add(listConnectionStringOption);

listTablesCommand.SetAction(async (parseResult, _) =>
{
    var connectionString = GetConnectionString(parseResult.GetValue(listConnectionStringOption));

    if (string.IsNullOrEmpty(connectionString))
    {
        PrintConnectionStringError();
        return 1;
    }

    await BackfillRunner.ListTablesAsync(connectionString);
    return 0;
});

// Status command
var statusCommand = new Command("status", "Show backfill progress for all tables");

var statusConnectionStringOption = new Option<string?>("--connection-string", "-c")
{
    Description = connectionStringHelp
};

statusCommand.Options.Add(statusConnectionStringOption);

statusCommand.SetAction(async (parseResult, _) =>
{
    var connectionString = GetConnectionString(parseResult.GetValue(statusConnectionStringOption));

    if (string.IsNullOrEmpty(connectionString))
    {
        PrintConnectionStringError();
        return 1;
    }

    await BackfillRunner.ShowStatusAsync(connectionString);
    return 0;
});

rootCommand.Subcommands.Add(backfillCommand);
rootCommand.Subcommands.Add(listTablesCommand);
rootCommand.Subcommands.Add(statusCommand);

return await rootCommand.Parse(args).InvokeAsync();
