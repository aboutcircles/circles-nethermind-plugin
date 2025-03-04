using System.Collections.Concurrent;
using Circles.Index.Common;
using Circles.Index.Postgres;
using Circles.Index.Query;
using Circles.Index.Rpc;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Npgsql;

namespace Circles.Index;

public class Plugin : INethermindPlugin
{
    public string Name => "Circles";

    public string Description =>
        "Indexes Circles related events and provides query capabilities via JSON-RPC.";

    public string Author => "Gnosis";

    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private StateMachine? _indexerMachine;
    private Context? _indexerContext;
    private int _isProcessing;
    private int _newItemsArrived;
    private long _latestHeadToIndex = -1;

    public async Task Init(INethermindApi nethermindApi)
    {
        ILogger baseLogger = nethermindApi.LogManager.GetClassLogger();
        InterfaceLogger pluginLogger = new LoggerWithPrefix($"{Name}: ", baseLogger);

        Settings settings = new();

        var schemas = new List<IDatabaseSchema>
        {
            new Common.DatabaseSchema(),
            new CirclesV1.DatabaseSchema(),
            new CirclesV2.DatabaseSchema(),
            new CirclesV2.NameRegistry.DatabaseSchema(),
            new CirclesV2.StandardTreasury.DatabaseSchema(),
            new CirclesViews.DatabaseSchema()
        };

        if (settings.CirclesLBPFactoryAddress != null)
        {
            schemas.Add(new Circles.Index.CirclesV2.LBP.DatabaseSchema());
        }

        if (settings.CMGroupDeployer != null)
        {
            schemas.Add(new Circles.Index.CirclesV2.CMGroupDeployer.DatabaseSchema());
        }

        if (settings.SafeProxyFactoryAddresses.Length > 0)
        {
            schemas.Add(new Circles.Index.Safe.DatabaseSchema());
        }

        IDatabaseSchema databaseSchema = new CompositeDatabaseSchema(schemas.ToArray());
        IDatabase database = new PostgresDb(settings.IndexDbConnectionString, databaseSchema);
        IReadonlyDatabase readonlyDatabase = settings.IndexReadonlyDbConnectionString != null
            ? new PostgresDb(settings.IndexReadonlyDbConnectionString, databaseSchema)
            : database;

        LogSettings(pluginLogger, settings, database);
        database.Migrate();

        Sink sink = new Sink(
            database,
            databaseSchema.SchemaPropertyMap,
            databaseSchema.EventDtoTableMap,
            settings.EventBufferSize);

        InitCaches(pluginLogger, database);

        var logParsers = new List<ILogParser>
        {
            new CirclesV1.LogParser(settings.CirclesV1HubAddress),
            new CirclesV2.LogParser(settings.CirclesV2HubAddress, settings.CirclesErc20LiftAddress),
            new CirclesV2.NameRegistry.LogParser(settings.CirclesNameRegistryAddress),
            new CirclesV2.StandardTreasury.LogParser(settings.CirclesStandardTreasuryAddress)
        };

        if (settings.CirclesLBPFactoryAddress != null)
        {
            logParsers.Add(new CirclesV2.LBP.LogParser(settings.CirclesLBPFactoryAddress));
        }

        if (settings.CMGroupDeployer != null)
        {
            logParsers.Add(new Circles.Index.CirclesV2.CMGroupDeployer.LogParser(settings.CMGroupDeployer));
        }

        if (settings.SafeProxyFactoryAddresses.Length > 0)
        {
            logParsers.Add(new Circles.Index.Safe.LogParser(settings.SafeProxyFactoryAddresses));
        }
        
        
        var liveTables = new ConcurrentDictionary<(string Namespace, string Table), object?>();

        _indexerContext = new Context(
            nethermindApi,
            pluginLogger,
            settings,
            database,
            readonlyDatabase,
            logParsers.ToArray(),
            sink,
            liveTables);

        _indexerMachine = new StateMachine(
            _indexerContext
            , nethermindApi.BlockTree!
            , nethermindApi.ReceiptFinder!
            , _cancellationTokenSource.Token);

        await _indexerMachine.TransitionTo(StateMachine.State.Initial);

        nethermindApi.BlockTree!.NewHeadBlock += (_, args) =>
        {
            var fullSyncInfo = nethermindApi.EthSyncingInfo?.GetFullInfo();

            if (fullSyncInfo?.IsSyncing ?? true)
            {
                switch (fullSyncInfo?.SyncMode)
                {
                    // Should handle blocks in the following sync modes:
                    case SyncMode.Full:
                    case SyncMode.DbLoad:
                        break;
                    default:
                        return;
                }
            }

            HandleNewHead(args.Block.Number);
        };
    }

    private void LogSettings(InterfaceLogger pluginLogger, Settings settings, IDatabase database)
    {
        // Log all indexed events
        pluginLogger.Info("Indexing events:");
        foreach (var databaseSchemaTable in database.Schema.Tables)
        {
            pluginLogger.Info(
                $" * Topic: {databaseSchemaTable.Value.Topic.ToHexString()}; Name: {databaseSchemaTable.Key.Namespace}_{databaseSchemaTable.Key.Table}");
        }

        NpgsqlConnectionStringBuilder connectionStringBuilder = new(settings.IndexDbConnectionString);
        pluginLogger.Info("Index database: " + connectionStringBuilder.Database);
        pluginLogger.Info(" * host: " + connectionStringBuilder.Host);
        pluginLogger.Info(" * port: " + connectionStringBuilder.Port);
        pluginLogger.Info(" * user: " + connectionStringBuilder.Username);

        if (settings.IndexReadonlyDbConnectionString != null)
        {
            NpgsqlConnectionStringBuilder readonlyConnectionStringBuilder =
                new(settings.IndexReadonlyDbConnectionString);
            pluginLogger.Info("Index database (readonly): " + readonlyConnectionStringBuilder.Database);
            pluginLogger.Info(" * host: " + readonlyConnectionStringBuilder.Host);
            pluginLogger.Info(" * port: " + readonlyConnectionStringBuilder.Port);
            pluginLogger.Info(" * user: " + readonlyConnectionStringBuilder.Username);
        }

        pluginLogger.Info("Contract addresses: ");
        pluginLogger.Info(" * V1 Hub address: " + settings.CirclesV1HubAddress);
        pluginLogger.Info(" * V2 Hub address: " + settings.CirclesV2HubAddress);
        pluginLogger.Info(" * V2 Name Registry address: " + settings.CirclesNameRegistryAddress);
        pluginLogger.Info(" * V2 Standard Treasury address: " + settings.CirclesStandardTreasuryAddress);
        pluginLogger.Info(" * V2 LBP Factory address: " + settings.CirclesLBPFactoryAddress);
        pluginLogger.Info(" * V2 CMGroup Deployer address: " + settings.CMGroupDeployer);
        pluginLogger.Info(" * V2 Erc20 Lift address: " + settings.CirclesErc20LiftAddress);
        pluginLogger.Info(" * Safe Proxy Factory addresses: " + string.Join(", ",
            settings.SafeProxyFactoryAddresses.Select(o => o.ToString(true, false))));
        // pluginLogger.Info("Start index from: " + settings.StartBlock);

        if (!string.IsNullOrWhiteSpace(settings.ExternalPathfinderUrl))
        {
            pluginLogger.Info("Using external pathfinder");
            pluginLogger.Info("* pathfinder url: " + settings.ExternalPathfinderUrl);
        }
    }

    private static void InitCaches(InterfaceLogger logger, IDatabase database)
    {
        logger.Info("Caching Circles token addresses");

        var selectSignups = new Select(
            "CrcV1",
            "Signup",
            ["token"],
            [],
            [],
            int.MaxValue,
            false,
            int.MaxValue);

        var sql = selectSignups.ToSql(database);
        var result = database.Select(sql);
        var rows = result.Rows.ToArray();

        logger.Info($" * Found {rows.Length} Circles token addresses");

        foreach (var row in rows)
        {
            CirclesV1.LogParser.CirclesTokenAddresses.TryAdd(new Address(row[0]!.ToString()!), null);
        }

        logger.Info("Caching Circles token addresses done");

        logger.Info("Caching erc20 wrapper addresses");

        var selectErc20WrapperDeployed = new Select(
            "CrcV2",
            "ERC20WrapperDeployed",
            ["erc20Wrapper", "circlesType"],
            [],
            [],
            int.MaxValue,
            false,
            int.MaxValue);

        sql = selectErc20WrapperDeployed.ToSql(database);
        result = database.Select(sql);
        rows = result.Rows.ToArray();

        logger.Info($" * Found {rows.Length} erc20 wrapper addresses");

        foreach (var row in rows)
        {
            CirclesV2.LogParser.Erc20WrapperAddresses.TryAdd(new Address(row[0]!.ToString()!), (long)row[1]!);
        }

        logger.Info("Caching erc20 wrapper addresses done");


        logger.Info("Caching ProxyCreation events");

        var selectSafeProxyCreation = new Select(
            "Safe",
            "ProxyCreation",
            ["proxy"],
            [],
            [],
            int.MaxValue,
            false,
            int.MaxValue);

        sql = selectSafeProxyCreation.ToSql(database);
        result = database.Select(sql);
        rows = result.Rows.ToArray();

        logger.Info($" * Found {rows.Length} ProxyCreation events");

        foreach (var row in rows)
        {
            Safe.LogParser.KnownSafeProxies.TryAdd(new Address(row[0]!.ToString()!), null);
        }

        logger.Info("Caching ProxyCreation events done");
    }

    /// <summary>
    /// Handles when a new head block is received.
    /// It updates a buffer with the latest block number and starts the ProcessBlocksAsync task if it's not already running.
    /// This method is non-blocking.
    /// </summary>
    /// <param name="blockNo">The new chain head</param>
    private void HandleNewHead(long blockNo)
    {
        // Signal that new items have arrived
        Interlocked.Exchange(ref _newItemsArrived, 1);

        // Signal which block is the new head
        Interlocked.Exchange(ref _latestHeadToIndex, blockNo);

        // Start the processing task if it's not already running
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) == 0)
        {
            // TODO: Await all ProcessBlocksAsync tasks without blocking the event handler. It's important that we always get all exceptions (e.g. as aggregate exception) of all tasks.
            Task.Run(ProcessBlocksAsync, _cancellationTokenSource.Token);
        }
    }

    /// <summary>
    /// Handles the latest head block (according to the value in _latestHeadToIndex) by passing it to the _indexerMachine.
    /// If a new event arrived during processing, it will keep processing until no new events arrived and the value of _latestHeadToIndex is -1. 
    /// </summary>
    private async Task ProcessBlocksAsync()
    {
        try
        {
            do
            {
                _cancellationTokenSource.Token.ThrowIfCancellationRequested();

                long toIndex = Interlocked.Exchange(ref _latestHeadToIndex, -1);
                if (toIndex == -1)
                {
                    continue;
                }

                await _indexerMachine!.HandleEvent(new StateMachine.NewHead(toIndex));

                // As long as new items arrive, keep processing
            } while (Interlocked.CompareExchange(ref _newItemsArrived, 0, 1) == 1);
        }
        catch (OperationCanceledException)
        {
            _indexerContext!.Logger.Info("Processing was canceled.");
        }
        catch (Exception e)
        {
            _indexerContext!.Logger.Error("Error processing blocks", e);
            throw;
        }
        finally
        {
            // Mark processing as complete
            Interlocked.Exchange(ref _isProcessing, 0);
        }
    }

    public Task InitNetworkProtocol()
    {
        return Task.CompletedTask;
    }

    public async Task InitRpcModules()
    {
        await Task.Delay(5000);

        if (_indexerContext?.NethermindApi.RpcModuleProvider == null)
        {
            throw new Exception("_indexerContext.NethermindApi.RpcModuleProvider is not set");
        }

        var (getFromAPi, _) = _indexerContext.NethermindApi.ForRpc;

        CirclesRpcModule circlesRpcModule = new(_indexerContext);
        getFromAPi.RpcModuleProvider?.Register(
            new SingletonModulePool<ICirclesRpcModule>(circlesRpcModule));

        if (getFromAPi.SubscriptionFactory == null)
        {
            throw new Exception("getFromAPi.SubscriptionFactory is not set");
        }

        getFromAPi.SubscriptionFactory.RegisterSubscriptionType<CirclesSubscriptionParams>(
            "circles",
            (client, param) => new CirclesSubscription(client, _indexerContext, param));
    }

    public ValueTask DisposeAsync()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();

        return ValueTask.CompletedTask;
    }
}