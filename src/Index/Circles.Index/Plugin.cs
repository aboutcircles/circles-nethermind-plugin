using System.Collections.Immutable;
using Autofac;
using Circles.Index.Common;
using Circles.Index.Postgres;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules.Subscribe;
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
    public bool Enabled { get; } = true;

    public static readonly IReadOnlyList<IDatabaseSchema> AllSchemas = new List<IDatabaseSchema>
    {
        new CirclesV1.DatabaseSchema(),
        new CirclesV1.NameRegistry.DatabaseSchema(),
        new CirclesV2.DatabaseSchema(),
        new CirclesV2.AffiliateGroupRegistry.DatabaseSchema(),
        new CirclesV2.BaseGroupDeployer.DatabaseSchema(),
        new CirclesV2.CMGroupDeployer.DatabaseSchema(),
        new CirclesV2.DatabaseSchema(),
        new CirclesV2.InvitationEscrow.DatabaseSchema(),
        new CirclesV2.LBP.DatabaseSchema(),
        new CirclesV2.NameRegistry.DatabaseSchema(),
        new CirclesV2.OIC.DatabaseSchema(),
        new CirclesV2.StandardTreasury.DatabaseSchema(),
        new CirclesV2.TokenOffers.DatabaseSchema(),
        new CirclesViews.DatabaseSchema(),
        new DatabaseSchema(),
        new Safe.DatabaseSchema(),
    };

    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private StateMachine? _indexerMachine;
    private Context? _indexerContext;
    private int _isProcessing;
    private int _newItemsArrived;
    private long _latestHeadToIndex = -1;
    private Task? _ipfsDownloader;

    public Task Init(INethermindApi nethermindApi)
    {
        ILogger baseLogger = nethermindApi.LogManager.GetClassLogger();
        InterfaceLogger pluginLogger = new LoggerWithPrefix($"{Name}: ", baseLogger);

        Settings settings = new();

        var schemas = AllSchemas;

        IDatabaseSchema databaseSchema = new CompositeDatabaseSchema(schemas.ToArray());
        IDatabase database = new PostgresDb(settings.IndexDbConnectionString, databaseSchema);
        IReadonlyDatabase readonlyDatabase = new PostgresDb(settings.IndexReadonlyDbConnectionString, databaseSchema);

        LogSettings(pluginLogger, settings, database);
        database.Migrate();

        Sink sink = new Sink(
            database,
            databaseSchema.SchemaPropertyMap,
            databaseSchema.EventDtoTableMap,
            settings.EventBufferSize);

        //
        // Create all LogParser instances
        //
        var logParsers = new List<ILogParser>
        {
            new CirclesV1.LogParser(new(settings.CirclesV1HubAddress)),
            new CirclesV2.LogParser(new(settings.CirclesV2HubAddress), new(settings.CirclesErc20LiftAddress)),
            new CirclesV2.NameRegistry.LogParser(new(settings.CirclesNameRegistryAddress)),
            new CirclesV2.StandardTreasury.LogParser(new(settings.CirclesStandardTreasuryAddress)),
            new CirclesV2.LBP.LogParser(settings.CirclesLBPFactoryAddress.Select(o => new Address(o))
                .ToImmutableHashSet()),
            new CirclesV2.CMGroupDeployer.LogParser(settings.CMGroupDeployer.Select(o => new Address(o))
                .ToImmutableHashSet()),
            new Safe.LogParser(settings.SafeProxyFactoryAddresses.Select(o => new Address(o)).ToImmutableHashSet()),
            new CirclesV2.TokenOffers.LogParser(settings.CirclesTokenOfferFactoryAddress.Select(o => new Address(o))
                .ToImmutableHashSet()),
            new CirclesV1.NameRegistry.LogParser(new(settings.CirclesV1NameRegistry)),
            new CirclesV2.BaseGroupDeployer.LogParser(new(settings.BaseGroupDeployer)),
            new CirclesV2.AffiliateGroupRegistry.LogParser(new(settings.AffiliateGroupRegistry)),
            new CirclesV2.InvitationEscrow.LogParser(new(settings.InvitationEscrowContract)),
            new CirclesV2.OIC.LogParser(new(settings.OICContractAddress))
        };

        _indexerContext = new Context(
            nethermindApi,
            pluginLogger,
            settings,
            database,
            readonlyDatabase,
            logParsers.ToArray(),
            sink);

        _indexerMachine = new StateMachine(
            _indexerContext
            , nethermindApi.BlockTree!
            , nethermindApi.ReceiptFinder!
            , _cancellationTokenSource.Token);

        // Run the downloader
        _ipfsDownloader = Task.Run(async () => await RunIpfsDownloader(settings),
            _cancellationTokenSource.Token);

        return Task.CompletedTask;
    }

    private async Task RunIpfsDownloader(Settings settings)
    {
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                var seedQuery = @"
                    WITH digests AS (
                        SELECT DISTINCT ""metadataDigest""::bytea AS metadata_digest
                        FROM ""CrcV1_UpdateMetadataDigest""
                        UNION ALL
                        SELECT DISTINCT ""metadataDigest""
                        FROM ""CrcV2_UpdateMetadataDigest""
                    ), to_enqueue AS (
                        SELECT
                            d.metadata_digest
                        FROM   digests d
                                   LEFT   JOIN ipfs_queue q
                                               ON q.metadata_digest = d.metadata_digest
                                   LEFT   JOIN ipfs_files f
                                               ON f.metadata_digest = d.metadata_digest
                        WHERE  q.cid IS NULL
                          AND  f.metadata_digest IS NULL
                    ), with_cid as (
                        select
                            base58_encode(decode('1220','hex') || to_enqueue.metadata_digest) AS cid,
                            metadata_digest
                        from to_enqueue
                    )
                    INSERT INTO ipfs_queue (cid, metadata_digest, status, attempt_count, next_retry, updated_at)
                    SELECT  cid,
                            metadata_digest,
                            'PENDING',
                            0,
                            NOW(),
                            NOW()
                    FROM   with_cid
                    ON CONFLICT (cid) DO NOTHING;
                ";

                await using (var connection = new NpgsqlConnection(settings.IndexDbConnectionString))
                {
                    await connection.OpenAsync(_cancellationTokenSource.Token);
                    await using var command = new NpgsqlCommand(seedQuery, connection);
                    command.CommandTimeout = 600;
                    await command.ExecuteNonQueryAsync(_cancellationTokenSource.Token);
                }

                await IpfsDownloader.Main(
                    _cancellationTokenSource.Token,
                    settings.IndexDbConnectionString);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[IPFS Downloader] IPFS downloader failed: " + ex);
                await Task.Delay(TimeSpan.FromSeconds(1), _cancellationTokenSource.Token);
                Console.WriteLine("[IPFS Downloader] restarting IPFS downloader");
            }
        }
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
        pluginLogger.Info(" * V1 Hub: " + settings.CirclesV1HubAddress);
        pluginLogger.Info(" * V1 Name Registry: " + settings.CirclesV1NameRegistry);
        pluginLogger.Info(" * V2 Hub: " + settings.CirclesV2HubAddress);
        pluginLogger.Info(" * V2 Name Registry: " + settings.CirclesNameRegistryAddress);
        pluginLogger.Info(" * V2 Standard Treasury: " + settings.CirclesStandardTreasuryAddress);
        pluginLogger.Info(" * V2 LBP Factory: " + string.Join(", ", settings.CirclesLBPFactoryAddress));
        pluginLogger.Info(" * V2 CMGroup Deployer: " + string.Join(", ", settings.CMGroupDeployer));
        pluginLogger.Info(" * V2 Erc20 Lift: " + settings.CirclesErc20LiftAddress);
        pluginLogger.Info(" * V2 Affiliate group registry: " + settings.AffiliateGroupRegistry);
        pluginLogger.Info(" * V2 Token offer factory: " + string.Join(", ", settings.CirclesTokenOfferFactoryAddress));
        pluginLogger.Info(" * V2 Invitation escrow: " + settings.InvitationEscrowContract);
        pluginLogger.Info(" * V2 OIC: " + settings.OICContractAddress);
        pluginLogger.Info(" * V2 Base Group Router: " + settings.BaseGroupRouter);
        pluginLogger.Info(" * Safe Proxy Factory addresses: " + string.Join(", ", settings.SafeProxyFactoryAddresses));
        // pluginLogger.Info("Start index from: " + settings.StartBlock);

        if (!string.IsNullOrWhiteSpace(settings.ExternalPathfinderUrl))
        {
            pluginLogger.Info("Using external pathfinder");
            pluginLogger.Info("* pathfinder url: " + settings.ExternalPathfinderUrl);
        }
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
            _ = Task.Run(ProcessBlocksAsync, _cancellationTokenSource.Token);
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

    public Task InitRpcModules()
    {
        if (_indexerContext?.NethermindApi.RpcModuleProvider == null)
        {
            throw new Exception("_indexerContext.NethermindApi.RpcModuleProvider is not set");
        }

        var (getFromAPi, _) = _indexerContext.NethermindApi.ForRpc;

        if (_indexerContext?.NethermindApi?.Context is null)
            throw new InvalidOperationException("_indexerContext.NethermindApi.Context is not set");

        if (!_indexerContext.NethermindApi.Context.TryResolve(out ISubscriptionFactory? subscriptionFactory))
            throw new InvalidOperationException("ISubscriptionFactory not registered in Nethermind DI context");

        // Start the actual processing (give the other rpc modules enough time to initialize)
        // TODO: Any event we can subscribe to that indicates that rpc is ready?
        _ = Task.Delay(10_000, _cancellationTokenSource.Token).ContinueWith(async _ =>
        {
            await _indexerMachine.TransitionTo(StateMachine.State.Initial);

            _indexerContext.NethermindApi.BlockTree!.NewHeadBlock += (_, args) =>
            {
                var syncingInfo = _indexerContext.NethermindApi.SyncModeSelector?.Current;

                if (syncingInfo != null)
                {
                    switch (syncingInfo)
                    {
                        // Should handle blocks in the following sync modes:
                        case SyncMode.Full:
                        case SyncMode.WaitingForBlock:
                        case SyncMode.DbLoad:
                            _indexerContext.Logger.Info(
                                $"New head block while syncingInfo is SyncMode.Full or SyncMode.DbLoad. New head will be processed.");
                            break;
                        default:
                            _indexerContext.Logger.Warn(
                                $"New head block while syncingInfo not SyncMode.Full or SyncMode.DbLoad. New head will be skipped. Current sync-status: {syncingInfo.ToString()}");
                            return;
                    }
                }

                HandleNewHead(args.Block.Number);
            };
        }, _cancellationTokenSource.Token);

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();

        return ValueTask.CompletedTask;
    }
}