using System.CommandLine;
using Circles.Index.Backfill;

// Root command
var rootCommand = new RootCommand("Circles Index Backfill Tool - Populate specific tables without full reindex");

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
    description: "PostgreSQL connection string (or set POSTGRES_CONNECTION_STRING env var)");
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

backfillCommand.AddOption(tablesOption);
backfillCommand.AddOption(fromBlockOption);
backfillCommand.AddOption(toBlockOption);
backfillCommand.AddOption(connectionStringOption);
backfillCommand.AddOption(rpcUrlOption);
backfillCommand.AddOption(batchSizeOption);
backfillCommand.AddOption(dryRunOption);

backfillCommand.SetHandler(async (context) =>
{
    var tables = context.ParseResult.GetValueForOption(tablesOption)!;
    var fromBlock = context.ParseResult.GetValueForOption(fromBlockOption);
    var toBlock = context.ParseResult.GetValueForOption(toBlockOption);
    var connectionString = context.ParseResult.GetValueForOption(connectionStringOption)
        ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
    var rpcUrl = context.ParseResult.GetValueForOption(rpcUrlOption)!;
    var batchSize = context.ParseResult.GetValueForOption(batchSizeOption);
    var dryRun = context.ParseResult.GetValueForOption(dryRunOption);

    if (string.IsNullOrEmpty(connectionString))
    {
        Console.Error.WriteLine("Error: Connection string required. Use --connection-string or set POSTGRES_CONNECTION_STRING");
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
        DryRun = dryRun
    };

    var runner = new BackfillRunner(options);
    var exitCode = await runner.RunAsync(context.GetCancellationToken());
    context.ExitCode = exitCode;
});

// List-tables command
var listTablesCommand = new Command("list-tables", "List all available tables and their LogParser mappings");

var listConnectionStringOption = new Option<string?>(
    name: "--connection-string",
    description: "PostgreSQL connection string (or set POSTGRES_CONNECTION_STRING env var)");
listConnectionStringOption.AddAlias("-c");

listTablesCommand.AddOption(listConnectionStringOption);

listTablesCommand.SetHandler(async (context) =>
{
    var connectionString = context.ParseResult.GetValueForOption(listConnectionStringOption)
        ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");

    if (string.IsNullOrEmpty(connectionString))
    {
        Console.Error.WriteLine("Error: Connection string required. Use --connection-string or set POSTGRES_CONNECTION_STRING");
        context.ExitCode = 1;
        return;
    }

    await BackfillRunner.ListTablesAsync(connectionString);
});

// Status command
var statusCommand = new Command("status", "Show backfill progress for all tables");

var statusConnectionStringOption = new Option<string?>(
    name: "--connection-string",
    description: "PostgreSQL connection string (or set POSTGRES_CONNECTION_STRING env var)");
statusConnectionStringOption.AddAlias("-c");

statusCommand.AddOption(statusConnectionStringOption);

statusCommand.SetHandler(async (context) =>
{
    var connectionString = context.ParseResult.GetValueForOption(statusConnectionStringOption)
        ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");

    if (string.IsNullOrEmpty(connectionString))
    {
        Console.Error.WriteLine("Error: Connection string required. Use --connection-string or set POSTGRES_CONNECTION_STRING");
        context.ExitCode = 1;
        return;
    }

    await BackfillRunner.ShowStatusAsync(connectionString);
});

rootCommand.AddCommand(backfillCommand);
rootCommand.AddCommand(listTablesCommand);
rootCommand.AddCommand(statusCommand);

return await rootCommand.InvokeAsync(args);
