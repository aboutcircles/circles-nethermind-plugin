using Circles.Index.Common;
using Circles.Index.Indexer;
using Circles.Index.Postgres;
using Circles.Index.Rpc;
using Circles.Index.Utils;
using Nethermind.Api;
using Nethermind.Api.Extensions;
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

    public async Task Init(INethermindApi nethermindApi)
    {
        IDatabaseSchema common = new Common.DatabaseSchema();
        IDatabaseSchema v1 = new CirclesV1.DatabaseSchema();
        IDatabaseSchema v2 = new CirclesV2.DatabaseSchema();
        IDatabaseSchema v2NameRegistry = new CirclesV2.NameRegistry.DatabaseSchema();
        IDatabaseSchema databaseSchema = new CompositeDatabaseSchema([common, v1, v2, v2NameRegistry]);

        ILogger baseLogger = nethermindApi.LogManager.GetClassLogger();
        ILogger pluginLogger = new LoggerWithPrefix($"{Name}: ", baseLogger);

        Settings settings = new();
        pluginLogger.Info("Index Db connection string: " + settings.IndexDbConnectionString);
        pluginLogger.Info("V1 Hub address: " + settings.CirclesV1HubAddress);
        pluginLogger.Info("V2 Hub address: " + settings.CirclesV2HubAddress);
        pluginLogger.Info("Start index from: " + settings.StartBlock);

        IDatabase database = new PostgresDb(settings.IndexDbConnectionString, databaseSchema);
        database.Migrate();

        Query.Initialize(database);

        Sink sink = new Sink(
            database,
            new CompositeSchemaPropertyMap([
                v1.SchemaPropertyMap, v2.SchemaPropertyMap, v2NameRegistry.SchemaPropertyMap
            ]),
            new CompositeEventDtoTableMap([
                v1.EventDtoTableMap, v2.EventDtoTableMap, v2NameRegistry.EventDtoTableMap
            ]),
            settings.EventBufferSize);

        ILogParser[] logParsers =
        [
            new CirclesV1.LogParser(settings.CirclesV1HubAddress),
            new CirclesV2.LogParser(settings.CirclesV2HubAddress),
            new CirclesV2.NameRegistry.LogParser(settings.CirclesV2NameRegistryAddress)
        ];

        _indexerContext = new Context(
            nethermindApi,
            pluginLogger,
            settings,
            database,
            logParsers,
            sink);

        _indexerMachine = new StateMachine(
            _indexerContext
            , nethermindApi.BlockTree!
            , nethermindApi.ReceiptFinder!
            , _cancellationTokenSource.Token);

        await _indexerMachine.TransitionTo(StateMachine.State.Initial);

        Task currentMachineExecution = Task.CompletedTask;

        nethermindApi.BlockTree!.NewHeadBlock += (_, args) =>
        {
            // Simple debounce mechanism that works well when blocks are produced consistently.
            // TODO: This won't work well e.g. with spaceneth where blocks are only produced when there are transactions.
            if (currentMachineExecution is { IsCompleted: false })
            {
                return;
            }

            currentMachineExecution = Task.Run(() =>
                _indexerMachine.HandleEvent(new StateMachine.NewHead(args.Block.Number)));
        };
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

        CirclesRpcModule circlesRpcModule = new(_indexerContext);
        _indexerContext.NethermindApi.ForRpc.GetFromApi.RpcModuleProvider?.Register(
            new SingletonModulePool<ICirclesRpcModule>(circlesRpcModule));
    }

    public ValueTask DisposeAsync()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();

        return ValueTask.CompletedTask;
    }
}