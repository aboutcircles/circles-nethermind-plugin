using Circles.Index.Common;
using Circles.Index.Rpc;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;

namespace Circles.Index;

public class StateMachine(
    Context context,
    IBlockTree blockTree,
    IReceiptFinder receiptFinder,
    CancellationToken cancellationToken)
{
    public interface IEvent;

    public record NewHead(long Head) : IEvent;

    public enum State
    {
        New,
        Initial,
        Syncing,
        NotifySubscribers,
        Reorg,
        WaitForNewBlock,
        Error,
        End
    }

    private record EnterState : IEvent;

    private record EnterState<TArg>(TArg Arg) : EnterState;

    private record LeaveState : IEvent;

    private List<Exception> Errors { get; } = new();

    private State CurrentState { get; set; } = State.New;

    public async Task HandleEvent(IEvent e)
    {
        try
        {
            switch (CurrentState)
            {
                case State.New:
                    return;

                case State.Initial:
                    switch (e)
                    {
                        case EnterState:
                        {
                            context.Logger.Info("Initializing: Finding the last persisted block...");
                            var lastPersistedBlock = context.Database.FirstGap() ?? context.Database.LatestBlock() ?? 0;
                            
                            context.Logger.Info($"Initializing: Last persisted block is {lastPersistedBlock}. Deleting all events from this block onwards...");
                            await TransitionTo(State.Reorg, lastPersistedBlock);
                            return;
                        }
                    }

                    break;

                case State.Reorg:
                    switch (e)
                    {
                        case EnterState<long> enterState:
                            context.Logger.Info(
                                $"Reorg at {enterState.Arg}. Deleting all events from this block onwards...");

                            await context.Database.DeleteFromBlockOnwards(enterState.Arg);
                            await TransitionTo(State.WaitForNewBlock);
                            return;
                    }

                    break;

                case State.WaitForNewBlock:
                    switch (e)
                    {
                        case NewHead newHead:
                            context.Logger.Debug($"New head received: {newHead.Head}");
                            if (newHead.Head <= context.Database.LatestBlock())
                            {
                                await TransitionTo(State.Reorg, newHead.Head);
                                return;
                            }

                            await TransitionTo(State.Syncing, newHead.Head);
                            return;
                    }

                    break;

                case State.Syncing:
                    switch (e)
                    {
                        case EnterState<long> enterSyncing:
                            var importedBlockRange = await Sync(enterSyncing.Arg);
                            context.Logger.Debug($"Imported blocks from {importedBlockRange.Min} " +
                                                 $"to {importedBlockRange.Max}");
                            Errors.Clear();

                            await TransitionTo(State.NotifySubscribers, importedBlockRange);
                            return;
                    }

                    break;

                case State.NotifySubscribers:
                    switch (e)
                    {
                        case EnterState<Range<long>> importedBlockRange:
                            context.Logger.Info(
                                $"Notifying {CirclesSubscription.SubscriberCount} subscribers about new blocks: " +
                                $"{importedBlockRange.Arg.Min} - {importedBlockRange.Arg.Max}");

                            if (importedBlockRange.Arg.Max - importedBlockRange.Arg.Min > 1000)
                            {
                                context.Logger.Warn(
                                    $"Too many blocks to notify: {importedBlockRange.Arg.Max - importedBlockRange.Arg.Min}");
                            }
                            else
                            {
                                CirclesSubscription.Notify(context, importedBlockRange.Arg);
                            }

                            await TransitionTo(State.WaitForNewBlock);
                            return;
                    }

                    break;

                case State.Error:
                    switch (e)
                    {
                        case EnterState:
                            // Exponential backoff based on the number of errors
                            var delay = Errors.Count * Errors.Count * 1000;

                            // If the delay is larger than 60 sec, clear the oldest errors
                            if (delay > 60000)
                            {
                                Errors.RemoveAt(0);
                            }

                            // Add some jitter to the delay
                            var jitter = new Random((int)DateTime.Now.TimeOfDay.TotalSeconds).Next(0, 1000);
                            delay += jitter;

                            // Wait 'delay' ms
                            context.Logger.Info($"Waiting {delay} ms before retrying after an error...");
                            await Task.Delay(delay, cancellationToken);

                            // Retry
                            context.Logger.Info("Transitioning to 'Initial' state after an error...");
                            await TransitionTo(State.Initial);
                            return;
                        case LeaveState:
                            return;
                    }

                    break;

                case State.End:
                    return;
            }

            context.Logger.Trace($"Unhandled event {e} in state {CurrentState}");
        }
        catch (Exception ex)
        {
            context.Logger.Error($"Error while handling {e} event in state {CurrentState}", ex);
            Errors.Add(ex);

            await TransitionTo(State.Error);
        }
    }

    private async Task TransitionTo<TArgument>(State newState, TArgument? argument)
    {
        context.Logger.Debug($"Transitioning from {CurrentState} to {newState}");
        if (newState is not State.Error)
        {
            await HandleEvent(new LeaveState());
        }

        CurrentState = newState;

        await HandleEvent(new EnterState<TArgument?>(argument));
    }

    public async Task TransitionTo(State newState)
    {
        await TransitionTo<object>(newState, null);
    }

    private async IAsyncEnumerable<long> GetBlocksToSync(long toBlock)
    {
        long lastIndexHeight = context.Database.LatestBlock() ?? 0;
        if (lastIndexHeight == toBlock)
        {
            context.Logger.Info("No blocks to sync.");
            yield break;
        }

        var nextBlock = lastIndexHeight + 1;
        context.Logger.Debug($"Enumerating blocks to sync from {nextBlock} (LastIndexHeight + 1) to {toBlock}");

        for (long i = nextBlock; i <= toBlock; i++)
        {
            yield return i;
            await Task.Yield();
        }
    }

    private async Task<Range<long>> Sync(long toBlock)
    {
        Range<long> importedBlockRange = new();
        try
        {
            ImportFlow flow = new(blockTree, receiptFinder, context);
            IAsyncEnumerable<long> blocksToSync = GetBlocksToSync(toBlock);
            importedBlockRange = await flow.Run(blocksToSync, cancellationToken);

            await context.Sink.Flush();
            await flow.FlushBlocks();
        }
        catch (TaskCanceledException)
        {
            context.Logger.Info("Cancelled indexing blocks.");
        }

        return importedBlockRange;
    }
}