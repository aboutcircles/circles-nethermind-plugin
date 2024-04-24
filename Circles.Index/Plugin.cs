using System.Data.Common;
using System.Threading.Channels;
using Circles.Index.Common;
using Circles.Index.Data.Postgresql;
using Circles.Index.Data.Query;
using Circles.Index.Indexer;
using Circles.Index.Rpc;
using Circles.Index.Utils;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Npgsql;
using Block = Nethermind.Core.Block;

namespace Circles.Index;

public class CirclesIndex : INethermindPlugin
{
    public string Name => "Circles.Index";

    public string Description =>
        "Indexes Circles related events and provides query capabilities via JSON-RPC. Indexed events are: Signup, OrgSignup, Trust, HubTransfer, CrcTransfer.";

    public string Author => "Gnosis";

    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private INethermindApi? _nethermindApi;

    private StateMachine? _indexerMachine;
    private Context? _indexerContext;

    public Task Init(INethermindApi nethermindApi)
    {
        _nethermindApi = nethermindApi;

        // LibPathfinder.ffi_initialize();

        Run();

        return Task.CompletedTask;
    }

    private long? TryFindReorg(ILogger logger, IBlockTree blockTree, Settings settings)
    {
        logger.Info("Trying to find reorg.");

        using NpgsqlConnection mainConnection = new(settings.IndexDbConnectionString);
        mainConnection.Open();
        IEnumerable<(long BlockNumber, Hash256 BlockHash)> lastPersistedBlocks =
            PostgresQuery.LastPersistedBlocks(mainConnection);
        long? reorgAt = null;

        foreach ((long BlockNumber, Hash256 BlockHash) recentPersistedBlock in lastPersistedBlocks)
        {
            Block? recentChainBlock = blockTree.FindBlock(recentPersistedBlock.BlockNumber);
            if (recentChainBlock == null)
            {
                throw new Exception($"Couldn't find block {recentPersistedBlock.BlockNumber} in the chain");
            }

            if (recentPersistedBlock.BlockHash == recentChainBlock.Hash)
            {
                continue;
            }

            logger.Info($"Block {recentPersistedBlock.BlockNumber} is different in the chain.");
            logger.Info($"  Recent persisted block hash: {recentPersistedBlock.BlockHash}");
            logger.Info($"  Recent chain block hash: {recentChainBlock.Hash}");
            reorgAt = recentPersistedBlock.BlockNumber;
            break;
        }

        return reorgAt;
    }

    private async void Run()
    {
        try
        {
            if (_nethermindApi == null)
            {
                throw new Exception("_nethermindApi is not set");
            }

            Settings settings = new();
            IInitConfig initConfig = _nethermindApi.Config<IInitConfig>();

            ILogger baseLogger = _nethermindApi.LogManager.GetClassLogger();
            ILogger pluginLogger = new LoggerWithPrefix("Circles.Index:", baseLogger);

            string indexDbLocation = Path.Combine(initConfig.BaseDbPath, settings.IndexDbFileName);
            string pathfinderDbLocation = Path.Combine(initConfig.BaseDbPath, settings.PathfinderDbFileName);
            ISink sink = new PostgresSink(settings.IndexDbConnectionString);

            DbProviderFactory factory = NpgsqlFactory.Instance;
            Query.Initialize(factory);

            pluginLogger.Info("SQLite database at: " + indexDbLocation);
            pluginLogger.Info("Pathfinder database at: " + pathfinderDbLocation);
            pluginLogger.Info("Index Db connection string: " + settings.IndexDbConnectionString);
            pluginLogger.Info("V1 Hub address: " + settings.CirclesV1HubAddress);
            pluginLogger.Info("V2 Hub address: " + settings.CirclesV2HubAddress);
            pluginLogger.Info("Start index from: " + settings.StartBlock);

            _indexerContext = new Context(indexDbLocation
                , pluginLogger
                , _nethermindApi.ChainSpec
                , settings);

            // Wait in a loop as long as the nethermind node is not fully in sync with the chain
            while ((_nethermindApi.Pivot?.PivotNumber > _nethermindApi.BlockTree?.Head?.Number
                    || _nethermindApi.BlockTree?.Head == null
                    || _nethermindApi.ReceiptFinder == null)
                   && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                pluginLogger.Info("Waiting for the node to sync");
                await Task.Delay(1000);
            }

            if (_cancellationTokenSource.Token.IsCancellationRequested)
            {
                return;
            }

            Caches.Init();
            IndexerVisitor visitor = new(sink, settings);

            long? FindReorg() => TryFindReorg(pluginLogger, _nethermindApi.BlockTree!, settings);
            long GetHead() => _nethermindApi.BlockTree!.Head!.Number;

            _indexerMachine = new StateMachine(
                _indexerContext
                , _nethermindApi.BlockTree!
                , _nethermindApi.ReceiptFinder!
                , visitor
                , GetHead
                , FindReorg
                , sink
                , _cancellationTokenSource.Token);

            Channel<BlockEventArgs> blockChannel = Channel.CreateBounded<BlockEventArgs>(1);

            await Task.Run(async () =>
            {
                _nethermindApi.BlockTree!.NewHeadBlock += (_, args) =>
                {
                    blockChannel.Writer.TryWrite(args); // This will overwrite if the channel is full
                };

                // Process blocks from the channel
                _ = Task.Run(async () =>
                {
                    await foreach (BlockEventArgs args in blockChannel.Reader.ReadAllAsync(_cancellationTokenSource
                                       .Token))
                    {
                        try
                        {
                            if (args.Block.Number <= _indexerMachine.LastIndexHeight
                                && (_indexerMachine.LastReorgAt == 0 ||
                                    args.Block.Number <= _indexerMachine.LastReorgAt))
                            {
                                _indexerContext.Logger.Warn($"Reorg at {args.Block.Number}");
                                _indexerMachine.LastReorgAt = args.Block.Number;
                            }

                            _indexerContext.Logger.Debug($"New block received: {args.Block.Number}");

                            await _indexerMachine.HandleEvent(StateMachine.Event.NewBlock);
                        }
                        catch (Exception e)
                        {
                            _indexerContext.Logger.Error("Error while indexing new block", e);
                        }
                    }
                }, _cancellationTokenSource.Token);

                await _indexerMachine.TransitionTo(StateMachine.State.Initial);
            }, _cancellationTokenSource.Token);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }


    public Task InitNetworkProtocol()
    {
        return Task.CompletedTask;
    }


    public async Task InitRpcModules()
    {
        if (_nethermindApi == null)
        {
            throw new Exception("_nethermindApi is not set");
        }

        if (_nethermindApi.RpcModuleProvider == null)
        {
            throw new Exception("_nethermindApi.RpcModuleProvider is not set");
        }

        if (_indexerContext == null)
        {
            throw new Exception("_indexerContext is not set");
        }

        (IApiWithNetwork apiWithNetwork, _) = _nethermindApi.ForRpc;
        CirclesRpcModule circlesRpcModule = new(_nethermindApi, _indexerContext.Settings.IndexDbConnectionString);
        apiWithNetwork.RpcModuleProvider?.Register(new SingletonModulePool<ICirclesRpcModule>(circlesRpcModule));
    }

    public ValueTask DisposeAsync()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();

        return ValueTask.CompletedTask;
    }
}