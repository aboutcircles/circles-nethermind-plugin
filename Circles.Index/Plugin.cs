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

    public Task Init(INethermindApi nethermindApi)
    {
        IDatabaseSchema common = new Common.DatabaseSchema();
        IDatabaseSchema v1 = new V1.DatabaseSchema();
        IDatabaseSchema v2 = new V2.DatabaseSchema();
        IDatabaseSchema databaseSchema = new CompositeDatabaseSchema([common, v1, v2]);

        ILogger baseLogger = nethermindApi.LogManager.GetClassLogger();
        ILogger pluginLogger = new LoggerWithPrefix(Name, baseLogger);

        Settings settings = new();
        pluginLogger.Info("Index Db connection string: " + settings.IndexDbConnectionString);
        pluginLogger.Info("V1 Hub address: " + settings.CirclesV1HubAddress);
        pluginLogger.Info("V2 Hub address: " + settings.CirclesV2HubAddress);
        pluginLogger.Info("Start index from: " + settings.StartBlock);

        IDatabase database = new PostgresDb(settings.IndexDbConnectionString, databaseSchema, pluginLogger);
        _indexerContext = new Context(nethermindApi, pluginLogger, settings, database);

        _indexerContext.Database.Migrate();
        Query.Initialize(_indexerContext.Database);

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
            await WaitUntilSynced(nethermindApi);

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

    private async Task WaitUntilSynced(INethermindApi nethermindApi)
    {
        long count = 0;

        while (nethermindApi.Pivot?.PivotNumber > nethermindApi.BlockTree?.Head?.Number
               || nethermindApi.BlockTree?.Head == null
               || nethermindApi.ReceiptFinder == null)
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested)
            {
                return;
            }

            count++;
            if (count % 10 == 0)
            {
                _indexerContext?.Logger.Info("Waiting for sync...");
            }

            await Task.Delay(1000);
        }
    }

    public Task InitNetworkProtocol()
    {
        return Task.CompletedTask;
    }

    public async Task InitRpcModules()
    {
        await Task.Delay(1000);

        if (_indexerContext?.NethermindApi == null)
        {
            throw new Exception("_nethermindApi is not set");
        }

        if (_indexerContext?.NethermindApi.RpcModuleProvider == null)
        {
            throw new Exception("_nethermindApi.RpcModuleProvider is not set");
        }

        CirclesRpcModule circlesRpcModule = new(_indexerContext);
        _indexerContext.NethermindApi.ForRpc.GetFromApi.RpcModuleProvider?.Register(
            new SingletonModulePool<ICirclesRpcModule>(circlesRpcModule));

        return;
    }

    public ValueTask DisposeAsync()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();

        return ValueTask.CompletedTask;
    }
}