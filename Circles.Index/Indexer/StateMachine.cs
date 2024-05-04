using System.Diagnostics;
using Circles.Index.Common;
using Circles.Index.Data;
using Circles.Index.Data.Postgresql;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Npgsql;

namespace Circles.Index.Indexer;

public class StateMachine(
    Context context,
    IBlockTree blockTree,
    IReceiptFinder receiptFinder,
    CancellationToken cancellationToken)
{
    public interface IEvent;

    record SyncCompleted : IEvent;

    public record NewHead(long Head) : IEvent;

    record EnterState : IEvent;

    record LeaveState : IEvent;

    record ReorgCompleted : IEvent;

    private long LastReorgAt { get; set; }
    private long LastIndexHeight { get; set; }
    private List<Exception> Errors { get; } = new();
    private State CurrentState { get; set; } = State.New;

    public enum State
    {
        New,
        Initial,
        Syncing,
        Reorg,
        WaitForNewBlock,
        Error,
        End
    }

    public async Task HandleEvent(IEvent e)
    {
        try
        {
            switch (CurrentState)
            {
                case State.New:
                    // Empty state, only used to transition to the initial state
                    return;
                case State.Initial:
                    switch (e)
                    {
                        case EnterState:
                        {
                            // Make sure all tables exist before we start
                            MigrateSchemas();

                            long head = blockTree.Head!.Number;
                            SetLastIndexHeight();

                            await using NpgsqlConnection
                                reorgConnection = new(context.Settings.IndexDbConnectionString);
                            await reorgConnection.OpenAsync();

                            IReorgHandler reorgHandler = new ReorgHandler(reorgConnection, context.Logger);
                            await reorgHandler.ReorgAt(Math.Min(LastIndexHeight, head) + 1);

                            await TransitionTo(head == LastIndexHeight
                                ? State.WaitForNewBlock
                                : State.Syncing);
                            return;
                        }
                    }

                    break;

                case State.Syncing:
                    switch (e)
                    {
                        case EnterState:
                            // Make sure we know what exactly we are syncing
                            SetLastIndexHeight();

                            await Sync();
                            return;
                        case SyncCompleted:
                            Errors.Clear(); // Clean up errors after a successful sync
                            SetLastIndexHeight();

                            await TransitionTo(State.WaitForNewBlock);
                            return;
                    }

                    break;

                case State.WaitForNewBlock:
                    switch (e)
                    {
                        case NewHead newHead:
                            LastReorgAt = newHead.Head <= LastIndexHeight
                                ? newHead.Head
                                : TryFindReorg() ?? 0;

                            if (LastReorgAt > 0)
                            {
                                context.Logger.Warn($"Reorg at {LastReorgAt}");

                                await TransitionTo(State.Reorg);
                                return;
                            }

                            await TransitionTo(State.Syncing);
                            return;
                    }

                    break;

                case State.Reorg:
                    switch (e)
                    {
                        case EnterState:
                            // Internally runs asynchronous, deletes all state after the reorg block and triggers a ReorgCompleted event when done.
                            // After that the method will stop executing and only be restarted once the state is entered again.
                            Reorg();
                            return;
                        case ReorgCompleted:
                            await TransitionTo(State.Syncing);
                            return;
                    }

                    break;

                case State.Error:
                    switch (e)
                    {
                        case EnterState:
                            await TransitionTo(Errors.Count >= 3
                                ? State.End
                                : State.Initial);
                            return;
                        case LeaveState:
                            Cleanup();
                            return;
                    }

                    break;

                case State.End:
                    Cleanup();
                    return;
            }

            context.Logger.Debug($"Unhandled event {e} in state {CurrentState}");
        }
        catch (Exception ex)
        {
            context.Logger.Error($"Error while handling {e} event in state {CurrentState}", ex);
            Errors.Add(ex);

            await TransitionTo(State.Error);
        }
    }

    public async Task TransitionTo(State newState)
    {
        context.Logger.Info($"Transitioning from {CurrentState} to {newState}");
        if (newState is not State.Error)
        {
            await HandleEvent(new LeaveState());
        }

        CurrentState = newState;

        await HandleEvent(new EnterState());
    }

    private void SetLastIndexHeight()
    {
        using NpgsqlConnection mainConnection = new(context.Settings.IndexDbConnectionString);
        mainConnection.Open();

        ISystemQueries postgresSystemQueries = new PostgresSystemQueries(mainConnection);
        LastIndexHeight = postgresSystemQueries.FirstGap() ?? postgresSystemQueries.LatestBlock() ?? 0;
    }

    private void Cleanup()
    {
    }

    private async void Reorg()
    {
        context.Logger.Info("Starting reorg process.");
        if (LastReorgAt == 0)
        {
            throw new Exception("LastReorgAt is 0");
        }

        await using NpgsqlConnection connection = new(context.Settings.IndexDbConnectionString);
        connection.Open();

        IReorgHandler reorgHandler = new ReorgHandler(connection, context.Logger);
        await reorgHandler.ReorgAt(LastReorgAt);

        LastReorgAt = 0;

        await HandleEvent(new ReorgCompleted());
    }

    private async Task Sync()
    {
        context.Logger.Info("Starting syncing process.");

        IEventSink v1Sink = new V1.PostgresSink(context.Settings.IndexDbConnectionString);
        IEventSink v2Sink = new V2.PostgresSink(context.Settings.IndexDbConnectionString);
        INewIndexerVisitor[] parsers =
        [
            new V1.IndexerVisitor(context.Settings.CirclesV1HubAddress),
            new V2.IndexerVisitor(context.Settings.CirclesV2HubAddress)
        ];

        try
        {
            ImportFlow flow = new ImportFlow(
                context.Settings
                , blockTree
                , receiptFinder
                , parsers
                , [v1Sink, v2Sink]);

            IAsyncEnumerable<long> blocksToSync = GetBlocksToSync();
            Range<long> importedBlockRange = await flow.Run(blocksToSync, cancellationToken);

            await v1Sink.Flush();
            await flow.FlushBlocks();

            if (importedBlockRange is { Min: long.MaxValue, Max: long.MinValue })
            {
                await HandleEvent(new SyncCompleted());
                return;
            }

            context.Logger.Info($"Imported blocks from {importedBlockRange.Min} to {importedBlockRange.Max}");
            await HandleEvent(new SyncCompleted());
        }
        catch (TaskCanceledException)
        {
            context.Logger.Info($"Cancelled indexing blocks.");
        }
        catch (Exception ex)
        {
            context.Logger.Error("Error while syncing blocks.", ex);
            Errors.Add(ex);
            await TransitionTo(State.Error);
        }
    }

    private async IAsyncEnumerable<long> GetBlocksToSync()
    {
        if (blockTree.Head == null)
        {
            yield break;
        }

        SetLastIndexHeight();
        LastIndexHeight = LastIndexHeight == 0 ? context.Settings.StartBlock : LastIndexHeight;

        long head = blockTree.Head.Number;
        long lastIndexHeight = LastIndexHeight;

        context.Logger.Info($"Getting blocks to sync from {LastIndexHeight} (LastIndexHeight) to {head} (chain-head)");

        if (lastIndexHeight == head)
        {
            context.Logger.Info("No blocks to sync.");
            yield break;
        }

        context.Logger.Debug($"Enumerating blocks to sync from {lastIndexHeight} to {head}");

        for (long i = lastIndexHeight + 1; i <= head; i++)
        {
            yield return i;
            await Task.Yield();
        }
    }


    private long? TryFindReorg()
    {
        context.Logger.Info("Trying to find reorg.");

        using NpgsqlConnection mainConnection = new(context.Settings.IndexDbConnectionString);
        mainConnection.Open();

        ISystemQueries postgresSystemQueries = new PostgresSystemQueries(mainConnection);

        IEnumerable<(long BlockNumber, Hash256 BlockHash)> lastPersistedBlocks =
            postgresSystemQueries.LastPersistedBlocks(100);
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

            context.Logger.Info($"Block {recentPersistedBlock.BlockNumber} is different in the chain.");
            context.Logger.Info($"  Recent persisted block hash: {recentPersistedBlock.BlockHash}");
            context.Logger.Info($"  Recent chain block hash: {recentChainBlock.Hash}");
            reorgAt = recentPersistedBlock.BlockNumber;
            break;
        }

        return reorgAt;
    }

    private void MigrateSchemas()
    {
        using NpgsqlConnection mainConnection = new(context.Settings.IndexDbConnectionString);
        mainConnection.Open();

        context.Logger.Info("Migrating database schema (common tables) ...");
        ISchema common = new Common.Schema();
        common.Migrate(mainConnection);

        context.Logger.Info("Migrating database schema (v1 tables) ...");
        ISchema v1 = new V1.Schema();
        v1.Migrate(mainConnection);

        context.Logger.Info("Migrating database schema (v2 tables) ...");
        ISchema v2 = new V2.Schema();
        v2.Migrate(mainConnection);
    }
}