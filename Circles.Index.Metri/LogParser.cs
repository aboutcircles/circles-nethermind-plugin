using System.Collections.Concurrent;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;


namespace Circles.Index.Metri;

public class EventEqualityComparer : IEqualityComparer<IIndexEvent>
{
    public bool Equals(IIndexEvent? x, IIndexEvent? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null) return false;
        if (y is null) return false;
        if (x.GetType() != y.GetType()) return false;
        return x.BlockNumber == y.BlockNumber && x.Timestamp == y.Timestamp &&
               x.TransactionIndex == y.TransactionIndex && x.LogIndex == y.LogIndex;
    }

    public int GetHashCode(IIndexEvent obj)
    {
        return HashCode.Combine(obj.BlockNumber, obj.Timestamp, obj.TransactionIndex, obj.LogIndex);
    }
}

public class LogParser() : ILogParser
{
    public static readonly Address ModuleProxyFactoryAddress =
        new Address("0x000000000000aDdB49795b0f9bA5BC298cDda236");

    public static readonly string PayDelayModuleImplementation = "0x9646fDAD06d3e24444381f44362a3B0eB343D337";
    public static readonly Address GnosisPayNFTAddress = new Address("0x88997988a6A5aAF29BA973d298D276FE75fb69ab");
    public static readonly Address SafeProxyFactoryAddress = new Address("0xa6B71E26C5e0845f74c812102Ca7114b6a896AB2");
    public static readonly Address GPSettlementAddress = new Address("0x9008D19f58AAbD9eD0D60971565AA8510560ab41");

    public static readonly Address EURE = new("0xcB444e90D8198415266c6a2724b7900fb12FC56E");
    public static readonly Address GBPE = new("0x5Cb9073902F2035222B9749F8fB0c9BFe5527108");

    public static readonly Address GNO = new("0x9C58BAcC331c9aa871AFD802DB6379a98e80CEdb");

    // public static readonly Address XDAI = new("0x0000000000000000000000000000000000000000");
    public static readonly Address WXDAI = new("0xe91D153E0b41518A2Ce8Dd3D7944Fa863463a97d");
    public static readonly Address EUReV2TokenAddress = new("0x420CA0f9B9b604cE0fd9C18EF134C705e5Fa3430");
    public static readonly Address GBPeV2TokenAddress = new("0x8E34bfEC4f6Eb781f9743D9b4af99CD23F9b7053");

    public readonly HashSet<Address> Erc20Addresses =
    [
        EURE,
        GBPE,
        GNO,
        // XDAI,
        WXDAI,
        EUReV2TokenAddress,
        GBPeV2TokenAddress
    ];

    private readonly Hash256 _proxyCreation = new(DatabaseSchema.ProxyCreation.Topic);
    private readonly Hash256 _moduleProxyCreation = new(DatabaseSchema.ModuleProxyCreation.Topic);
    private readonly Hash256 _ownershipTransferred = new(DatabaseSchema.OwnershipTransferred.Topic);
    private readonly Hash256 _gnosisPayOGNftTransfer = new(DatabaseSchema.GnosisPayOGNftTransfer.Topic);
    private readonly Hash256 _erc20Transfer = new(DatabaseSchema.Erc20Transfer.Topic);

    private readonly Hash256 _executionSuccess = new(DatabaseSchema.ExecutionSuccess.Topic);
    private readonly Hash256 _executionFailure = new(DatabaseSchema.ExecutionFailure.Topic);
    private readonly Hash256 _safeMultiSigTransaction = new(DatabaseSchema.SafeMultiSigTransaction.Topic);
    private readonly Hash256 _safeReceived = new(DatabaseSchema.SafeReceived.Topic);
    private readonly Hash256 _safeSetup = new(DatabaseSchema.SafeSetup.Topic);
    private readonly Hash256 _removedOwner = new(DatabaseSchema.RemovedOwner.Topic);

    private readonly Hash256 _GPv2Settlement = new(DatabaseSchema.GPv2Settlement.Topic);


    // TODO: Fill from DB in "warmup caches"
    public static ConcurrentDictionary<Address, object?> PayDelayModules = new();
    public static ConcurrentDictionary<Address, object?> SafeProxies = new();

    public IEnumerable<IIndexEvent> ParseTransaction(Block block, int transactionIndex, Transaction transaction)
    {
        return Enumerable.Empty<IIndexEvent>();
    }

    public IEnumerable<IIndexEvent> ParseLog(Block block, Transaction transaction, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        HashSet<IIndexEvent> events = new(new EventEqualityComparer());
        if (log.Topics.Length == 0)
        {
            return events;
        }

        var topic = log.Topics[0];

        if (log.LoggersAddress == SafeProxyFactoryAddress)
        {
            if (topic == _proxyCreation)
            {
                var proxyCreation = ProxyCreation(block, receipt, log, logIndex);
                SafeProxies.TryAdd(new Address(proxyCreation.Proxy), null);

                events.Add(proxyCreation);

                // When there is a ProxyCreation event, there can/must be also a SafeSetup event for the created safe
                foreach (var innerLog in receipt.Logs ?? [])
                {
                    if (innerLog.Topics.Length == 0)
                    {
                        continue;
                    }

                    var innerTopic = innerLog.Topics[0];
                    if (innerTopic == _safeSetup)
                    {
                        events.Add(ParseSafeSetup(block, receipt, innerLog, logIndex));
                    }
                }
            }
        }

        if (log.LoggersAddress == ModuleProxyFactoryAddress)
        {
            if (topic == _moduleProxyCreation)
            {
                /* From envio:
                   ModuleProxyFactory.ModuleProxyCreation.contractRegister(
                     async ({ event, context }) => {
                       if (event.params.masterCopy === PAY_DELAY_MODULE_IMPLEMENTATION) {
                         context.addPayDelayModule(event.params.proxy);
                       }
                     }
                   );
                 */
                var parsedEvent = ModuleProxyCreation(block, receipt, log, logIndex);
                if (parsedEvent.MasterCopy == PayDelayModuleImplementation)
                {
                    PayDelayModules.TryAdd(new Address(parsedEvent.Proxy), null);
                    events.Add(parsedEvent);
                }
            }
        }

        if (PayDelayModules.ContainsKey(log.LoggersAddress))
        {
            if (topic == _ownershipTransferred)
            {
                events.Add(OwnershipTransferred(block, receipt, log, logIndex));
            }
        }

        if (log.LoggersAddress == GnosisPayNFTAddress)
        {
            if (topic == _gnosisPayOGNftTransfer)
            {
                events.Add(GnosisPayOGNftTransfer(block, receipt, log, logIndex));
            }
        }

        if (Erc20Addresses.Contains(log.LoggersAddress))
        {
            if (topic == _erc20Transfer)
            {
                events.Add(Erc20Transfer(block, receipt, log, logIndex));
            }
        }

        if (SafeProxies.ContainsKey(log.LoggersAddress))
        {
            if (topic == _executionSuccess)
            {
                events.Add(ParseExecutionSuccess(block, receipt, log, logIndex));
            }
            else if (topic == _executionFailure)
            {
                events.Add(ParseExecutionFailure(block, receipt, log, logIndex));
            }
            else if (topic == _safeMultiSigTransaction)
            {
                events.Add(ParseSafeMultiSigTransaction(block, receipt, log, logIndex));
            }
            else if (topic == _safeReceived)
            {
                events.Add(ParseSafeReceived(block, receipt, log, logIndex));
            }
            else if (topic == _safeSetup)
            {
                events.Add(ParseSafeSetup(block, receipt, log, logIndex));
            }
            else if (topic == _removedOwner)
            {
                events.Add(ParseRemovedOwner(block, receipt, log, logIndex));
            }
        }

        if (log.LoggersAddress == GPSettlementAddress)
        {
            if (topic == _GPv2Settlement)
            {
                events.Add(ParseGPv2Settlement(block, receipt, log, logIndex));
            }
        }

        return events;
    }

    private IIndexEvent Erc20Transfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var fromBytes = log.Topics[1].Bytes;
        string from = new Address(fromBytes.Slice(12, 20).ToArray()).ToString(true, false);

        var toBytes = log.Topics[2].Bytes;
        string to = new Address(toBytes.Slice(12, 20).ToArray()).ToString(true, false);

        var value = new UInt256(log.Data, true);

        return new Erc20Transfer(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            from,
            to,
            value
        );
    }

    private ModuleProxyCreation ModuleProxyCreation(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // "ModuleProxyCreation(address indexed proxy, address indexed masterCopy)");
        var proxyBytes = log.Topics[1].Bytes;
        string proxy = new Address(proxyBytes.Slice(12, 20).ToArray()).ToString(true, false);

        var masterCopyBytes = log.Topics[2].Bytes;
        string masterCopy = new Address(masterCopyBytes.Slice(12, 20).ToArray()).ToString(true, false);

        return new ModuleProxyCreation(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            proxy,
            masterCopy
        );
    }

    private ProxyCreation ProxyCreation(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var proxyBytes = log.Data.AsSpan(0, 32);
        string proxy = new Address(proxyBytes.Slice(12, 20).ToArray()).ToString(true, false);

        var singletonBytes = log.Data.AsSpan(32, 32);
        string singleton = new Address(singletonBytes.Slice(12, 20).ToArray()).ToString(true, false);

        return new ProxyCreation(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            proxy,
            singleton
        );
    }

    private IIndexEvent OwnershipTransferred(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var fromBytes = log.Topics[1].Bytes;
        string from = new Address(fromBytes.Slice(12, 20).ToArray()).ToString(true, false);

        var toBytes = log.Topics[2].Bytes;
        string to = new Address(toBytes.Slice(12, 20).ToArray()).ToString(true, false);

        return new OwnershipTransferred(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            from,
            to
        );
    }

    private IIndexEvent GnosisPayOGNftTransfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var fromBytes = log.Topics[1].Bytes;
        string from = new Address(fromBytes.Slice(12, 20).ToArray()).ToString(true, false);

        var toBytes = log.Topics[2].Bytes;
        string to = new Address(toBytes.Slice(12, 20).ToArray()).ToString(true, false);

        var tokenId = new UInt256(log.Topics[3].Bytes, true);

        return new GnosisPayOGNftTransfer(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            from,
            to,
            tokenId
        );
    }

    private IIndexEvent ParseExecutionSuccess(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // ExecutionSuccess(bytes32 txHash, uint256 payment)
        var data = log.Data;
        var txHashBytes = data.AsSpan(0, 32).ToArray();

        var paymentBytes = data.AsSpan(32, 32).ToArray();
        var payment = new UInt256(paymentBytes, true);

        return new ExecutionSuccess(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            txHashBytes,
            payment
        );
    }

    private IIndexEvent ParseExecutionFailure(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // ExecutionFailure(bytes32 txHash, uint256 payment)
        var data = log.Data;
        var txHashBytes = data.AsSpan(0, 32).ToArray();

        var paymentBytes = data.AsSpan(32, 32).ToArray();
        var payment = new UInt256(paymentBytes, true);

        return new ExecutionFailure(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            txHashBytes,
            payment
        );
    }

    private IIndexEvent ParseSafeMultiSigTransaction(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var data = log.Data.AsSpan();

        // Parameter 1: address to
        var toBytes = data.Slice(0, 32);
        string to = new Address(toBytes.Slice(12, 20).ToArray()).ToString(true, false);

        // Parameter 2: uint256 value
        var valueBytes = data.Slice(32, 32);
        var value = new UInt256(valueBytes.ToArray(), true);

        // Parameter 3: bytes data (dynamic)
        var dataOffsetBytes = data.Slice(64, 32);
        int dataOffset = (int)new UInt256(dataOffsetBytes.ToArray(), true);

        // Parameter 4: uint8 operation
        var operationBytes = data.Slice(96, 32);
        byte operation = operationBytes[31];

        // Parameter 5: uint256 safeTxGas
        var safeTxGasBytes = data.Slice(128, 32);
        var safeTxGas = new UInt256(safeTxGasBytes.ToArray(), true);

        // Parameter 6: uint256 baseGas
        var baseGasBytes = data.Slice(160, 32);
        var baseGas = new UInt256(baseGasBytes.ToArray(), true);

        // Parameter 7: uint256 gasPrice
        var gasPriceBytes = data.Slice(192, 32);
        var gasPrice = new UInt256(gasPriceBytes.ToArray(), true);

        // Parameter 8: address gasToken
        var gasTokenBytes = data.Slice(224, 32);
        string gasToken = new Address(gasTokenBytes.Slice(12, 20).ToArray()).ToString(true, false);

        // Parameter 9: address refundReceiver
        var refundReceiverBytes = data.Slice(256, 32);
        string refundReceiver = new Address(refundReceiverBytes.Slice(12, 20).ToArray()).ToString(true, false);

        // Parameter 10: bytes signatures (dynamic)
        var signaturesOffsetBytes = data.Slice(288, 32);
        int signaturesOffset = (int)new UInt256(signaturesOffsetBytes.ToArray(), true);

        // Parameter 11: bytes additionalInfo (dynamic)
        var additionalInfoOffsetBytes = data.Slice(320, 32);
        int additionalInfoOffset = (int)new UInt256(additionalInfoOffsetBytes.ToArray(), true);

        // Parse dynamic parameter: bytes data
        var dataLengthBytes = data.Slice(dataOffset, 32);
        int dataLength = (int)new UInt256(dataLengthBytes.ToArray(), true);
        var dataBytes = data.Slice(dataOffset + 32, dataLength).ToArray();

        // Parse dynamic parameter: bytes signatures
        var signaturesLengthBytes = data.Slice(signaturesOffset, 32);
        int signaturesLength = (int)new UInt256(signaturesLengthBytes.ToArray(), true);
        var signaturesBytes = data.Slice(signaturesOffset + 32, signaturesLength).ToArray();

        // Parse dynamic parameter: bytes additionalInfo
        var additionalInfoLengthBytes = data.Slice(additionalInfoOffset, 32);
        int additionalInfoLength = (int)new UInt256(additionalInfoLengthBytes.ToArray(), true);
        var additionalInfoBytes = data.Slice(additionalInfoOffset + 32, additionalInfoLength).ToArray();

        return new SafeMultiSigTransaction(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            to,
            value,
            dataBytes,
            operation,
            safeTxGas,
            baseGas,
            gasPrice,
            gasToken,
            refundReceiver,
            signaturesBytes,
            additionalInfoBytes
        );
    }

    private IIndexEvent ParseSafeReceived(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var senderBytes = log.Topics[1].Bytes;
        string sender = new Address(senderBytes.Slice(12, 20).ToArray()).ToString(true, false);

        var data = log.Data;
        var value = new UInt256(data, true);

        return new SafeReceived(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            sender,
            value
        );
    }

    private IIndexEvent ParseSafeSetup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var initiatorBytes = log.Topics[1].Bytes;
        string initiator = new Address(initiatorBytes.Slice(12, 20).ToArray()).ToString(true, false);

        var data = log.Data.AsSpan();

        // Parameter owners: offset to owners (dynamic array)
        var ownersOffsetBytes = data.Slice(0, 32);
        int ownersOffset = (int)new UInt256(ownersOffsetBytes.ToArray(), true);

        // Parameter threshold
        var thresholdBytes = data.Slice(32, 32);
        var threshold = new UInt256(thresholdBytes.ToArray(), true);

        // Parameter initializer
        var initializerBytes = data.Slice(64, 32);
        string initializer = new Address(initializerBytes.Slice(12, 20).ToArray()).ToString(true, false);

        // Parameter fallbackHandler
        var fallbackHandlerBytes = data.Slice(96, 32);
        string fallbackHandler = new Address(fallbackHandlerBytes.Slice(12, 20).ToArray()).ToString(true, false);

        // Parse dynamic parameter: address[] owners
        var ownersLengthBytes = data.Slice(ownersOffset, 32);
        int ownersLength = (int)new UInt256(ownersLengthBytes.ToArray(), true);

        var owners = new string[ownersLength];
        for (int i = 0; i < ownersLength; i++)
        {
            var ownerBytes = data.Slice(ownersOffset + 32 + i * 32, 32);
            string owner = new Address(ownerBytes.Slice(12, 20).ToArray()).ToString(true, false);
            owners[i] = owner;
        }

        return new SafeSetup(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            initiator,
            owners,
            threshold,
            initializer,
            fallbackHandler
        );
    }

    private IIndexEvent ParseRemovedOwner(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var data = log.Data;

        var ownerBytes = data.AsSpan(12, 20).ToArray();
        string owner = new Address(ownerBytes).ToString(true, false);

        return new RemovedOwner(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            owner
        );
    }

    private IIndexEvent ParseGPv2Settlement(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // Parse the indexed parameter 'owner' from topics[1]
        var ownerBytes = log.Topics[1].Bytes;
        string owner = new Address(ownerBytes.Slice(12, 20).ToArray()).ToString(true, false);

        var data = log.Data.AsSpan();

        // Offset positions based on the Ethereum ABI encoding
        int offset = 0;

        // Parse 'sellToken' (address, 32 bytes)
        var sellTokenBytes = data.Slice(offset, 32);
        string sellToken = new Address(sellTokenBytes.Slice(12, 20).ToArray()).ToString(true, false);
        offset += 32;

        // Parse 'buyToken' (address, 32 bytes)
        var buyTokenBytes = data.Slice(offset, 32);
        string buyToken = new Address(buyTokenBytes.Slice(12, 20).ToArray()).ToString(true, false);
        offset += 32;

        // Parse 'sellAmount' (uint256, 32 bytes)
        var sellAmountBytes = data.Slice(offset, 32);
        var sellAmount = new UInt256(sellAmountBytes.ToArray(), true);
        offset += 32;

        // Parse 'buyAmount' (uint256, 32 bytes)
        var buyAmountBytes = data.Slice(offset, 32);
        var buyAmount = new UInt256(buyAmountBytes.ToArray(), true);
        offset += 32;

        // Parse 'feeAmount' (uint256, 32 bytes)
        var feeAmountBytes = data.Slice(offset, 32);
        var feeAmount = new UInt256(feeAmountBytes.ToArray(), true);
        offset += 32;

        // Parse 'orderUid' (bytes, dynamic)
        // First, get the offset to 'orderUid' data
        var orderUidOffsetBytes = data.Slice(offset, 32);
        int orderUidOffset = (int)new UInt256(orderUidOffsetBytes.ToArray(), true);
        // Adjust the offset relative to the start of 'data'
        int absoluteOrderUidOffset = orderUidOffset;

        // Get the length of 'orderUid' bytes array
        var orderUidLengthBytes = data.Slice(absoluteOrderUidOffset, 32);
        int orderUidLength = (int)new UInt256(orderUidLengthBytes.ToArray(), true);

        // Extract 'orderUid' bytes
        var orderUidData = data.Slice(absoluteOrderUidOffset + 32, orderUidLength).ToArray();

        return new GPv2Settlement(
            receipt.BlockNumber,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            owner,
            sellToken,
            buyToken,
            sellAmount,
            buyAmount,
            feeAmount,
            orderUidData
        );
    }
}