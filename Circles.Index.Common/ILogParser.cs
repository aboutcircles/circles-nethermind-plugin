using Nethermind.Core;
using Nethermind.Logging;

namespace Circles.Index.Common;

public interface ILogParser
{
    /// <summary>
    /// Parses a log entry into a list of index events.
    /// </summary>
    IEnumerable<IIndexEvent> ParseLog(Block block, Transaction transaction, TxReceipt receipt, LogEntry log,
        int logIndex);

    /// <summary>
    /// Parses a transaction into a list of index events.
    /// This method is called after all logs have been parsed, so it can be used to aggregate events.
    /// </summary>
    IEnumerable<Either<IIndexEvent, IParsedCallData>> ParseTransaction(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        IIndexEvent[] events);

    /// <summary>
    /// Can be used to initialize the log parser. E.g. useful to load known contract addresses.
    /// </summary>
    /// <returns></returns>
    Task Init(
        InterfaceLogger logger,
        IDatabase database,
        Settings settings);
}

public readonly struct Either<TLeft, TRight>
{
    private readonly TLeft? _left;
    private readonly TRight? _right;

    public bool IsLeft { get; }
    public bool IsRight => !IsLeft;

    public TLeft Left => IsLeft
        ? _left!
        : throw new InvalidOperationException("Not a Left value");

    public TRight Right => IsRight
        ? _right!
        : throw new InvalidOperationException("Not a Right value");

    private Either(TLeft left)
    {
        _left = left;
        _right = default;
        IsLeft = true;
    }

    private Either(TRight right)
    {
        _right = right;
        _left = default;
        IsLeft = false;
    }

    public static Either<TLeft, TRight> FromLeft(TLeft left)
        => new Either<TLeft, TRight>(left);

    public static Either<TLeft, TRight> FromRight(TRight right)
        => new Either<TLeft, TRight>(right);

    public T Match<T>(
        Func<TLeft, T> onLeft,
        Func<TRight, T> onRight)
    {
        return IsLeft ? onLeft(Left) : onRight(Right);
    }

    public void Switch(
        Action<TLeft> onLeft,
        Action<TRight> onRight)
    {
        if (IsLeft) onLeft(Left);
        else onRight(Right);
    }
}