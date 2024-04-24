using Circles.Index.Common;
using Circles.Index.Data;
using Circles.Index.Data.Postgresql;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Npgsql;

namespace Circles.Index.Indexer;

public class StateMachine(
    Context context,
    IBlockTree blockTree,
    IReceiptFinder receiptFinder,
    IIndexerVisitor visitor,
    Func<long> getHead,
    Func<long?> tryFindReorg,
    ISink dataSink,
    CancellationToken cancellationToken)
{
    public State CurrentState { get; private set; } = State.New;
    public long LastReorgAt { get; set; }
    public long LastIndexHeight { get; set; }
    public List<Exception> Errors { get; } = new();

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

    public enum Event
    {
        SyncCompleted,
        NewBlock,
        ErrorOccurred,
        EnterState,
        LeaveState,
        ReorgCompleted
    }

    public async Task HandleEvent(Event e)
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
                        case Event.EnterState:
                        {
                            // Make sure all tables exist before we start
                            MigrateTables();

                            long head = getHead();
                            SetLastIndexHeight();

                            await using NpgsqlConnection
                                reorgConnection = new(context.Settings.IndexDbConnectionString);
                            await reorgConnection.OpenAsync();

                            await ReorgHandler.ReorgAt(
                                reorgConnection
                                , context.Logger
                                , Math.Min(LastIndexHeight, head) + 1);

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
                        case Event.EnterState:
                            // Make sure we know what exactly we are syncing
                            SetLastIndexHeight();

                            // Internally runs asynchronous and syncs the whole backlog of missed blocks.
                            // After that the method will trigger SyncComplete and stop executing.
                            // It will only be restarted once the state is entered again.
                            Sync();
                            return;
                        case Event.SyncCompleted:
                            Errors.Clear(); // Clean up errors after a successful sync

                            // TODO: Should only be done once, not every time the event is triggered
                            MigrateIndexes(); // Make sure that the indexes are only created once the syncing is complete
                            SetLastIndexHeight();

                            await TransitionTo(State.WaitForNewBlock);
                            return;
                    }

                    break;

                case State.WaitForNewBlock:
                    switch (e)
                    {
                        case Event.NewBlock:
                            LastReorgAt = tryFindReorg() ?? 0;
                            if (LastReorgAt > 0)
                            {
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
                        case Event.EnterState:
                            // Internally runs asynchronous, deletes all state after the reorg block and triggers a ReorgCompleted event when done.
                            // After that the method will stop executing and only be restarted once the state is entered again.
                            Reorg();
                            return;
                        case Event.ReorgCompleted:
                            await TransitionTo(State.Syncing);
                            return;
                    }

                    break;

                case State.Error:
                    switch (e)
                    {
                        case Event.EnterState:
                            await TransitionTo(Errors.Count >= 3
                                ? State.End
                                : State.Initial);
                            return;
                        case Event.LeaveState:
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

            await HandleEvent(Event.ErrorOccurred);
        }
    }

    #region Context changing methods

    private void SetLastIndexHeight()
    {
        using NpgsqlConnection mainConnection = new(context.Settings.IndexDbConnectionString);
        mainConnection.Open();

        LastIndexHeight = PostgresQuery.FirstGap(mainConnection) ?? PostgresQuery.LatestBlock(mainConnection) ?? 0;
    }

    private void Cleanup()
    {
    }

    #endregion

    private async void Reorg()
    {
        context.Logger.Info("Starting reorg process.");
        if (LastReorgAt == 0)
        {
            throw new Exception("LastReorgAt is 0");
        }

        await using NpgsqlConnection connection = new(context.Settings.IndexDbConnectionString);
        connection.Open();
        await ReorgHandler.ReorgAt(connection, context.Logger, LastReorgAt);

        LastReorgAt = 0;

        await HandleEvent(Event.ReorgCompleted);
    }

    public async Task TransitionTo(State newState)
    {
        context.Logger.Info($"Transitioning from {CurrentState} to {newState}");
        await HandleEvent(Event.LeaveState);

        CurrentState = newState;

        await HandleEvent(Event.EnterState);
    }

    private async void Sync()
    {
        context.Logger.Info("Starting syncing process.");

        try
        {
            ImportFlow flow = new ImportFlow(
                blockTree
                , receiptFinder
                , visitor
                , dataSink);

            IAsyncEnumerable<long> blocksToSync = GetBlocksToSync();
            Range<long> importedBlockRange = await flow.Run(blocksToSync, cancellationToken);

            if (importedBlockRange is { Min: long.MaxValue, Max: long.MinValue })
            {
                await HandleEvent(Event.SyncCompleted);
                return;
            }

            context.Logger.Info($"Imported blocks from {importedBlockRange.Min} to {importedBlockRange.Max}");
            await dataSink.Flush();
            await HandleEvent(Event.SyncCompleted);
        }
        catch (TaskCanceledException)
        {
            context.Logger.Info($"Cancelled indexing blocks.");
        }
    }

    private async IAsyncEnumerable<long> GetBlocksToSync()
    {
        long head = getHead();
        LastIndexHeight = LastIndexHeight == 0 ? context.Settings.StartBlock : LastIndexHeight;
        context.Logger.Info($"Getting blocks to sync from {LastIndexHeight} (LastIndexHeight) to {head} (chain-head)");

        if (LastIndexHeight == head)
        {
            context.Logger.Info("No blocks to sync.");
            yield break;
        }

        context.Logger.Debug($"Enumerating blocks to sync from {LastIndexHeight} to {head}");

        for (long i = LastIndexHeight + 1; i <= head; i++)
        {
            yield return i;
            await Task.Yield();
        }
    }

    private void MigrateTables()
    {
        using NpgsqlConnection mainConnection = new(context.Settings.IndexDbConnectionString);
        mainConnection.Open();

        context.Logger.Info("Migrating database schema (tables)");
        Schema.Migrate(mainConnection);
    }

    private void MigrateIndexes()
    {
        using NpgsqlConnection mainConnection = new(context.Settings.IndexDbConnectionString);
        mainConnection.Open();

        // Check if the index exists. If yes, return.
        using NpgsqlCommand command =
            new("SELECT 1 FROM pg_indexes WHERE tablename = 'block' AND indexname = 'idx_block_block_number';",
                mainConnection);
        using NpgsqlDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            return;
        }

        reader.Close();

        context.Logger.Info("Migrating database schema (indexes)");
        Schema.Migrate(mainConnection);
    }
}