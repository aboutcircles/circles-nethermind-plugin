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

// Backfill command
var backfillCommand = new Command("backfill", "Backfill specific tables from historical blocks");

var tablesOption = new Option<string[]>(
    name: "--tables",
    description: "Tables to backfill (e.g., CrcV2_FlowEdgesScopeSingleStarted)")
{
    IsRequired = true,
    AllowMultipleArgumentsPerToken = true
};
tablesOption.AddAlias("-t");

var fromBlockOption = new Option<long>(
    name: "--from-block",
    description: "Block number to start backfill from (default: 37534026 for V2)",
    getDefaultValue: () => 37534026);
fromBlockOption.AddAlias("-f");

var toBlockOption = new Option<long?>(
    name: "--to-block",
    description: "Block number to end backfill at (default: current System_Block max)");
toBlockOption.AddAlias("-e");

var connectionStringOption = new Option<string?>(
    name: "--connection-string",
    description: "PostgreSQL connection string (or set POSTGRES_CONNECTION_STRING, or POSTGRES_USER+POSTGRES_PASSWORD env vars)");
connectionStringOption.AddAlias("-c");

var rpcUrlOption = new Option<string>(
    name: "--rpc-url",
    description: "Nethermind JSON-RPC URL",
    getDefaultValue: () => "http://localhost:8545");
rpcUrlOption.AddAlias("-r");

var batchSizeOption = new Option<int>(
    name: "--batch-size",
    description: "Number of blocks to process per batch",
    getDefaultValue: () => 1000);
batchSizeOption.AddAlias("-b");

var dryRunOption = new Option<bool>(
    name: "--dry-run",
    description: "Parse blocks but don't write to database",
    getDefaultValue: () => false);

var hubAddressOption = new Option<string?>(
    name: "--hub-address",
    description: "V2 Hub contract address (default: 0xc12c1e50abb450d6205ea2c3fa861b3b834d13e8 for Gnosis)");

backfillCommand.AddOption(tablesOption);
backfillCommand.AddOption(fromBlockOption);
backfillCommand.AddOption(toBlockOption);
backfillCommand.AddOption(connectionStringOption);
backfillCommand.AddOption(rpcUrlOption);
backfillCommand.AddOption(batchSizeOption);
backfillCommand.AddOption(dryRunOption);
backfillCommand.AddOption(hubAddressOption);

backfillCommand.SetHandler(async (context) =>
{
    var tables = context.ParseResult.GetValueForOption(tablesOption)!;
    var fromBlock = context.ParseResult.GetValueForOption(fromBlockOption);
    var toBlock = context.ParseResult.GetValueForOption(toBlockOption);
    var connectionString = GetConnectionString(context.ParseResult.GetValueForOption(connectionStringOption));
    var rpcUrl = context.ParseResult.GetValueForOption(rpcUrlOption)!;
    var batchSize = context.ParseResult.GetValueForOption(batchSizeOption);
    var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
    var hubAddress = context.ParseResult.GetValueForOption(hubAddressOption);

    if (string.IsNullOrEmpty(connectionString))
    {
        Console.Error.WriteLine("Error: Connection string required.");
        Console.Error.WriteLine("  Use --connection-string, or set POSTGRES_CONNECTION_STRING,");
        Console.Error.WriteLine("  or set POSTGRES_USER + POSTGRES_PASSWORD (+ optional POSTGRES_HOST, POSTGRES_PORT, POSTGRES_DB)");
        context.ExitCode = 1;
        return;
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
        V2HubAddress = hubAddress
    };

    var runner = new BackfillRunner(options);
    var exitCode = await runner.RunAsync(context.GetCancellationToken());
    context.ExitCode = exitCode;
});

// List-tables command
var listTablesCommand = new Command("list-tables", "List all available tables and their LogParser mappings");

var listConnectionStringOption = new Option<string?>(
    name: "--connection-string",
    description: "PostgreSQL connection string (or set POSTGRES_CONNECTION_STRING, or POSTGRES_USER+POSTGRES_PASSWORD env vars)");
listConnectionStringOption.AddAlias("-c");

listTablesCommand.AddOption(listConnectionStringOption);

listTablesCommand.SetHandler(async (context) =>
{
    var connectionString = GetConnectionString(context.ParseResult.GetValueForOption(listConnectionStringOption));

    if (string.IsNullOrEmpty(connectionString))
    {
        Console.Error.WriteLine("Error: Connection string required.");
        Console.Error.WriteLine("  Use --connection-string, or set POSTGRES_CONNECTION_STRING,");
        Console.Error.WriteLine("  or set POSTGRES_USER + POSTGRES_PASSWORD (+ optional POSTGRES_HOST, POSTGRES_PORT, POSTGRES_DB)");
        context.ExitCode = 1;
        return;
    }

    await BackfillRunner.ListTablesAsync(connectionString);
});

// Status command
var statusCommand = new Command("status", "Show backfill progress for all tables");

var statusConnectionStringOption = new Option<string?>(
    name: "--connection-string",
    description: "PostgreSQL connection string (or set POSTGRES_CONNECTION_STRING, or POSTGRES_USER+POSTGRES_PASSWORD env vars)");
statusConnectionStringOption.AddAlias("-c");

statusCommand.AddOption(statusConnectionStringOption);

statusCommand.SetHandler(async (context) =>
{
    var connectionString = GetConnectionString(context.ParseResult.GetValueForOption(statusConnectionStringOption));

    if (string.IsNullOrEmpty(connectionString))
    {
        Console.Error.WriteLine("Error: Connection string required.");
        Console.Error.WriteLine("  Use --connection-string, or set POSTGRES_CONNECTION_STRING,");
        Console.Error.WriteLine("  or set POSTGRES_USER + POSTGRES_PASSWORD (+ optional POSTGRES_HOST, POSTGRES_PORT, POSTGRES_DB)");
        context.ExitCode = 1;
        return;
    }

    await BackfillRunner.ShowStatusAsync(connectionString);
});

rootCommand.AddCommand(backfillCommand);
rootCommand.AddCommand(listTablesCommand);
rootCommand.AddCommand(statusCommand);

return await rootCommand.InvokeAsync(args);
