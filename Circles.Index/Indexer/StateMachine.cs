using Circles.Index.Common;
using Circles.Index.Data;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;

namespace Circles.Index.Indexer;

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

    private long LastIndexHeight => context.Database.FirstGap() ?? context.Database.LatestBlock() ?? 0;

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
                            // Initially delete all events for which no "System_Block" exists
                            await TransitionTo(State.Reorg, LastIndexHeight);
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
                            context.Logger.Info($"New head received: {newHead.Head}");
                            if (newHead.Head <= LastIndexHeight)
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
                            context.Logger.Info($"Imported blocks from {importedBlockRange.Min} " +
                                                $"to {importedBlockRange.Max}");
                            Errors.Clear();

                            await TransitionTo(State.WaitForNewBlock);
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
                            return;
                    }

                    break;

                case State.End:
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

    private async Task TransitionTo<TArgument>(State newState, TArgument? argument)
    {
        context.Logger.Info($"Transitioning from {CurrentState} to {newState}");
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
        long lastIndexHeight = LastIndexHeight;
        if (lastIndexHeight == toBlock)
        {
            context.Logger.Info("No blocks to sync.");
            yield break;
        }

        var nextBlock = lastIndexHeight + 1;
        context.Logger.Info($"Enumerating blocks to sync from {nextBlock} (LastIndexHeight + 1) to {toBlock}");

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