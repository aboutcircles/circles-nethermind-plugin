using Circles.Index.Data.Model;
using Circles.Index.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;

namespace Circles.Index.Indexer;

public class StateMachine(
    Context context,
    IBlockTree blockTree,
    IReceiptFinder receiptFinder,
    IIndexerVisitor visitor,
    Func<long> getHead,
    Func<long?> tryFindReorg)
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

                            await using SqliteConnection
                                reorgConnection = new($"Data Source={context.IndexDbLocation}");
                            await reorgConnection.OpenAsync();

                            long head = getHead();
                            await ReorgHandler.ReorgAt(
                                reorgConnection
                                , context.Logger
                                , Math.Min(LastIndexHeight, head));

                            SetLastIndexHeight();

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

                            await TransitionTo(State.WaitForNewBlock);
                            return;
                    }

                    break;

                case State.WaitForNewBlock:
                    switch (e)
                    {
                        case Event.NewBlock:
                            if (LastReorgAt <= 0)
                            {
                                LastReorgAt = tryFindReorg() ?? 0;
                            }

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
        using SqliteConnection mainConnection = new($"Data Source={context.IndexDbLocation}");
        mainConnection.Open();

        LastIndexHeight = Query.LatestBlock(mainConnection) ?? 0;
        context.Logger.Info($"Current index height: {LastIndexHeight}");

        context.Logger.Info($"Current chain height: {getHead()}");
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

        await using SqliteConnection connection = new($"Data Source={context.IndexDbLocation}");
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
                , context.Settings);

            IEnumerable<long> blocksToSync = GetBlocksToSync();
            Range<long> importedBlockRange = await flow.Run(blocksToSync);

            context.Logger.Info($"Imported blocks from {importedBlockRange.Min} to {importedBlockRange.Max}");
        }
        catch (TaskCanceledException)
        {
            context.Logger.Info($"Cancelled indexing blocks.");
            return;
        }

        await HandleEvent(Event.SyncCompleted);
    }

    private IEnumerable<long> GetBlocksToSync()
    {
        long head = getHead();
        LastIndexHeight = LastIndexHeight == 0 ? 12000000 : LastIndexHeight;
        if (LastIndexHeight == head)
        {
            context.Logger.Info("No blocks to sync.");
            yield break;
        }

        context.Logger.Debug($"Enumerating blocks to sync from {LastIndexHeight} to {head}");

        for (long i = LastIndexHeight; i <= head; i++)
        {
            yield return i;
        }
    }

    private void MigrateTables()
    {
        using SqliteConnection mainConnection = new($"Data Source={context.IndexDbLocation}");
        mainConnection.Open();
        mainConnection.Open();

        context.Logger.Info("SQLite database at: " + context.IndexDbLocation);

        context.Logger.Info("Migrating database schema (tables)");
        Schema.MigrateTables(mainConnection);
    }

    private void MigrateIndexes()
    {
        using SqliteConnection mainConnection = new($"Data Source={context.IndexDbLocation}");
        mainConnection.Open();

        context.Logger.Info("Migrating database schema (indexes)");
        Schema.MigrateIndexes(mainConnection);
    }
}