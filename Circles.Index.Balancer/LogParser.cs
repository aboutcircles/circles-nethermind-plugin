using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Circles.Index.Balancer;

public class LogParser(Address vaultAddress) : ILogParser
{
    private readonly Hash256 _authorizerChangedTopic = new(DatabaseSchema.AuthorizerChanged.Topic);
    private readonly Hash256 _pausedStateTopic = new(DatabaseSchema.PausedStateChanged.Topic);
    private readonly Hash256 _relayerTopic = new(DatabaseSchema.RelayerApprovalChanged.Topic);
    private readonly Hash256 _internalBalTopic = new(DatabaseSchema.InternalBalanceChanged.Topic);
    private readonly Hash256 _externalBalTopic = new(DatabaseSchema.ExternalBalanceTransfer.Topic);
    private readonly Hash256 _flashLoanTopic = new(DatabaseSchema.FlashLoan.Topic);
    private readonly Hash256 _swapTopic = new(DatabaseSchema.Swap.Topic);
    private readonly Hash256 _poolRegTopic = new(DatabaseSchema.PoolRegistered.Topic);
    private readonly Hash256 _tokensRegTopic = new(DatabaseSchema.TokensRegistered.Topic);
    private readonly Hash256 _tokensDeRegTopic = new(DatabaseSchema.TokensDeregistered.Topic);
    private readonly Hash256 _poolBalChangedTopic = new(DatabaseSchema.PoolBalanceChanged.Topic);
    private readonly Hash256 _poolBalManagedTopic = new(DatabaseSchema.PoolBalanceManaged.Topic);

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events) => [];

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings) => Task.CompletedTask;
    public IRollbackCache[] Caches { get; } = [];

    public IEnumerable<IIndexEvent> ParseLog(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        if (log.Topics.Length == 0 || log.Address != vaultAddress)
            yield break;

        var topic = log.Topics[0];

        if (topic == _authorizerChangedTopic) yield return ParseAuthorizerChanged(block, receipt, log, logIndex);
        else if (topic == _pausedStateTopic) yield return ParsePausedStateChanged(block, receipt, log, logIndex);
        else if (topic == _relayerTopic) yield return ParseRelayerApproval(block, receipt, log, logIndex);
        else if (topic == _internalBalTopic) yield return ParseInternalBalance(block, receipt, log, logIndex);
        else if (topic == _externalBalTopic) yield return ParseExternalBalance(block, receipt, log, logIndex);
        else if (topic == _flashLoanTopic) yield return ParseFlashLoan(block, receipt, log, logIndex);
        else if (topic == _swapTopic) yield return ParseSwap(block, receipt, log, logIndex);
        else if (topic == _poolRegTopic) yield return ParsePoolRegistered(block, receipt, log, logIndex);
        else if (topic == _tokensRegTopic)
            foreach (var e in ParseTokensRegistered(block, receipt, log, logIndex))
                yield return e;
        else if (topic == _tokensDeRegTopic)
            foreach (var e in ParseTokensDeregistered(block, receipt, log, logIndex))
                yield return e;
        else if (topic == _poolBalChangedTopic)
            foreach (var e in ParsePoolBalanceChanged(block, receipt, log, logIndex))
                yield return e;
        else if (topic == _poolBalManagedTopic) yield return ParsePoolBalanceManaged(block, receipt, log, logIndex);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 1) Authorizer / Pause / Relayer
    private AuthorizerChangedEvt ParseAuthorizerChanged(Block block, TxReceipt receipt, LogEntry log, int idx)
    {
        string newAuth = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        return new(block.Number, (long)block.Timestamp, receipt.Index, idx, receipt.TxHash!.ToString(),
            log.Address.ToString(true, false), newAuth);
    }

    private PausedStateChangedEvt ParsePausedStateChanged(Block block, TxReceipt receipt, LogEntry log, int idx)
    {
        bool paused = log.Data[^1] == 0x01; // last byte (bool packed in 32-byte slot)
        return new(block.Number, (long)block.Timestamp, receipt.Index, idx, receipt.TxHash!.ToString(),
            log.Address.ToString(true, false), paused);
    }

    private RelayerApprovalChangedEvt ParseRelayerApproval(Block block, TxReceipt receipt, LogEntry log, int idx)
    {
        string relayer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string sender = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        bool approved = log.Data[^1] == 0x01;
        return new(block.Number, (long)block.Timestamp, receipt.Index, idx, receipt.TxHash!.ToString(),
            log.Address.ToString(true, false), relayer, sender, approved);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 2) Balances
    private InternalBalanceChangedEvt ParseInternalBalance(Block block, TxReceipt receipt, LogEntry log, int idx)
    {
        string user = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string token = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        var delta = LogDataParsingHelper.ParseSingleUInt256(log.Data.AsSpan());
        return new(block.Number, (long)block.Timestamp, receipt.Index, idx, receipt.TxHash!.ToString(),
            log.Address.ToString(true, false), user, token, delta);
    }

    private ExternalBalanceTransferEvt ParseExternalBalance(Block block, TxReceipt receipt, LogEntry log, int idx)
    {
        string token = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string sender = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        var data = log.Data.AsSpan();
        string recipient = "0x" + BitConverter.ToString(data[12..32].ToArray()).Replace("-", "").ToLowerInvariant();
        var amount = LogDataParsingHelper.ParseSingleUInt256(data[32..]);
        return new(block.Number, (long)block.Timestamp, receipt.Index, idx, receipt.TxHash!.ToString(),
            log.Address.ToString(true, false), token, sender, recipient, amount);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 3) Flash-loan & Swap
    private FlashLoanEvt ParseFlashLoan(Block b, TxReceipt r, LogEntry l, int idx)
    {
        string recipient = LogDataParsingHelper.ParseAddressFromTopic(l.Topics[1].Bytes);
        string token = LogDataParsingHelper.ParseAddressFromTopic(l.Topics[2].Bytes);
        var data = l.Data.AsSpan();
        var amount = LogDataParsingHelper.ParseSingleUInt256(data);
        var fee = LogDataParsingHelper.ParseSingleUInt256(data[32..]);
        return new(b.Number, (long)b.Timestamp, r.Index, idx, r.TxHash!.ToString(),
            l.Address.ToString(true, false), recipient, token, amount, fee);
    }

    private SwapEvt ParseSwap(Block b, TxReceipt r, LogEntry l, int idx)
    {
        byte[] poolId = l.Topics[1].BytesToArray();
        string tokenIn = LogDataParsingHelper.ParseAddressFromTopic(l.Topics[2].Bytes);
        string tokenOut = LogDataParsingHelper.ParseAddressFromTopic(l.Topics[3].Bytes);
        var data = l.Data.AsSpan();
        var amountIn = LogDataParsingHelper.ParseSingleUInt256(data);
        var amountOut = LogDataParsingHelper.ParseSingleUInt256(data[32..]);
        return new(b.Number, (long)b.Timestamp, r.Index, idx, r.TxHash!.ToString(),
            l.Address.ToString(true, false), poolId, tokenIn, tokenOut,
            amountIn, amountOut);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 4) Pools & Tokens
    private PoolRegisteredEvt ParsePoolRegistered(Block b, TxReceipt r, LogEntry l, int idx)
    {
        byte[] poolId = l.Topics[1].BytesToArray();
        string poolAddress = LogDataParsingHelper.ParseAddressFromTopic(l.Topics[2].Bytes);
        byte specialization = l.Data[31];
        return new(b.Number, (long)b.Timestamp, r.Index, idx, r.TxHash!.ToString(),
            l.Address.ToString(true, false), poolId, poolAddress, specialization);
    }

    private IEnumerable<TokensRegisteredEvt> ParseTokensRegistered(Block b, TxReceipt r, LogEntry l, int idx)
    {
        byte[] poolId = l.Topics[1].BytesToArray();
        var dataSpan = l.Data.AsSpan();
        int tokensOff = LogDataParsingHelper.ParseOffset(dataSpan, 0);
        int amOff = LogDataParsingHelper.ParseOffset(dataSpan, 32);

        var tokens = LogDataParsingHelper.ParseAddressArray(dataSpan, tokensOff);
        var assetManagers = LogDataParsingHelper.ParseAddressArray(dataSpan, amOff);

        for (int i = 0; i < tokens.Length; i++)
        {
            yield return new(b.Number, (long)b.Timestamp, r.Index, idx, r.TxHash!.ToString(),
                l.Address.ToString(true, false), poolId, i, tokens[i], assetManagers[i]);
        }
    }

    private IEnumerable<TokensDeregisteredEvt> ParseTokensDeregistered(Block b, TxReceipt r, LogEntry l, int idx)
    {
        byte[] poolId = l.Topics[1].BytesToArray();
        var dataSpan = l.Data.AsSpan();
        int tokensOff = LogDataParsingHelper.ParseOffset(dataSpan, 0);
        var tokens = LogDataParsingHelper.ParseAddressArray(dataSpan, tokensOff);

        for (int i = 0; i < tokens.Length; i++)
        {
            yield return new(b.Number, (long)b.Timestamp, r.Index, idx, r.TxHash!.ToString(),
                l.Address.ToString(true, false), poolId, i, tokens[i]);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 5) Pool balances
    private IEnumerable<PoolBalanceChangedEvt> ParsePoolBalanceChanged(Block b, TxReceipt r, LogEntry l, int idx)
    {
        byte[] poolId = l.Topics[1].BytesToArray();
        string liquidityProvider = LogDataParsingHelper.ParseAddressFromTopic(l.Topics[2].Bytes);

        var dataSpan = l.Data.AsSpan();
        int tokensOff = LogDataParsingHelper.ParseOffset(dataSpan, 0);
        int deltasOff = LogDataParsingHelper.ParseOffset(dataSpan, 32);
        int feesOff = LogDataParsingHelper.ParseOffset(dataSpan, 64);

        var tokens = LogDataParsingHelper.ParseAddressArray(dataSpan, tokensOff);
        var deltas = LogDataParsingHelper.ParseUInt256Array(dataSpan, deltasOff);
        var fees = LogDataParsingHelper.ParseUInt256Array(dataSpan, feesOff);

        for (int i = 0; i < tokens.Length; i++)
        {
            yield return new(b.Number, (long)b.Timestamp, r.Index, idx, r.TxHash!.ToString(),
                l.Address.ToString(true, false), poolId, liquidityProvider, i,
                tokens[i], deltas[i], fees[i]);
        }
    }

    private PoolBalanceManagedEvt ParsePoolBalanceManaged(Block b, TxReceipt r, LogEntry l, int idx)
    {
        byte[] poolId = l.Topics[1].BytesToArray();
        string assetMngr = LogDataParsingHelper.ParseAddressFromTopic(l.Topics[2].Bytes);
        string token = LogDataParsingHelper.ParseAddressFromTopic(l.Topics[3].Bytes);
        var data = l.Data.AsSpan();
        var cashDelta = LogDataParsingHelper.ParseSingleUInt256(data);
        var managedDelta = LogDataParsingHelper.ParseSingleUInt256(data[32..]);

        return new(b.Number, (long)b.Timestamp, r.Index, idx, r.TxHash!.ToString(),
            l.Address.ToString(true, false), poolId, assetMngr, token,
            cashDelta, managedDelta);
    }
}