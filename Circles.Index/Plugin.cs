using System.Threading.Channels;
using Circles.Index.Common;
using Circles.Index.Indexer;
using Circles.Index.Postgres;
using Circles.Index.Rpc;
using Circles.Index.Utils;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Core;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Npgsql;

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

        Settings settings = new();
        ILogger baseLogger = nethermindApi.LogManager.GetClassLogger();
        ILogger pluginLogger = new LoggerWithPrefix("Circles.Index:", baseLogger);
        IDatabase database = new PostgresDb(settings.IndexDbConnectionString);

        Query.Initialize(NpgsqlFactory.Instance);

        pluginLogger.Info("Migrating database schema (common tables) ...");
        IDatabaseSchema common = new Common.DatabaseSchema();
        database.Migrate(common);

        pluginLogger.Info("Migrating database schema (v1 tables) ...");
        IDatabaseSchema v1 = new V1.DatabaseSchema();
        database.Migrate(v1);

        pluginLogger.Info("Migrating database schema (v2 tables) ...");
        IDatabaseSchema v2 = new V2.DatabaseSchema();
        database.Migrate(v2);

        pluginLogger.Info("Index Db connection string: " + settings.IndexDbConnectionString);
        pluginLogger.Info("V1 Hub address: " + settings.CirclesV1HubAddress);
        pluginLogger.Info("V2 Hub address: " + settings.CirclesV2HubAddress);
        pluginLogger.Info("Start index from: " + settings.StartBlock);

        _indexerContext = new Context(pluginLogger, settings, database);

        Run(nethermindApi);

        return Task.CompletedTask;
    }

    private async void Run(INethermindApi nethermindApi)
    {
        if (_indexerContext == null)
        {
            throw new Exception("_indexerContext is not set");
        }

        try
        {
            // Wait in a loop as long as the nethermind node is not fully in sync with the chain
            while (nethermindApi.Pivot?.PivotNumber > nethermindApi.BlockTree?.Head?.Number
                   || nethermindApi.BlockTree?.Head == null
                   || nethermindApi.ReceiptFinder == null)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    return;
                }

                _indexerContext.Logger.Info("Waiting for the node to sync");
                await Task.Delay(1000);
            }

            _indexerMachine = new StateMachine(
                _indexerContext
                , nethermindApi.BlockTree!
                , nethermindApi.ReceiptFinder!
                , _cancellationTokenSource.Token);

            Channel<BlockEventArgs> blockChannel = Channel.CreateBounded<BlockEventArgs>(1);
            nethermindApi.BlockTree!.NewHeadBlock += (_, args) =>
            {
                _indexerContext.Logger.Debug($"New head block: {args.Block.Number}");
                blockChannel.Writer.TryWrite(args);
            };

            await Task.Run(async () =>
            {
                // Process blocks from the channel
                _ = Task.Run(async () =>
                {
                    await foreach (BlockEventArgs args in blockChannel.Reader.ReadAllAsync(_cancellationTokenSource
                                       .Token))
                    {
                        try
                        {
                            _indexerContext.Logger.Debug($"New block received: {args.Block.Number}");
                            await _indexerMachine.HandleEvent(new StateMachine.NewHead(args.Block.Number));
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

    public Task InitRpcModules()
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

        CirclesRpcModule circlesRpcModule = new(_nethermindApi, _indexerContext.Database);
        _nethermindApi.ForRpc.GetFromApi.RpcModuleProvider?.Register(
            new SingletonModulePool<ICirclesRpcModule>(circlesRpcModule));

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();

        return ValueTask.CompletedTask;
    }
}