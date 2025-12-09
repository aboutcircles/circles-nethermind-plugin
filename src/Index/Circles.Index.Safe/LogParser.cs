using System.Collections.Immutable;
using System.Numerics;
using Circles.Index.Common;
using Circles.Index.Query;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Circles.Index.Safe;

public class LogParser(ImmutableHashSet<Address> factoryAddresses) : ILogParser
{
    // public static readonly ConcurrentDictionary<Address, object?> KnownSafeProxies = new();
    public static readonly RollbackCache<Address, object?> KnownSafeProxies = new("KnownSafeProxies");

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        if (settings.SafeProxyFactoryAddresses.Length > 0)
        {
            var selectSafeProxyCreation = new Select(
                "Safe",
                "ProxyCreation",
                ["proxy"],
                [],
                [],
                int.MaxValue,
                false,
                int.MaxValue);

            var sql = selectSafeProxyCreation.ToSql(database);
            var result = database.Select(sql);
            var rows = result.Rows.ToArray();

            var seed = new Dictionary<Address, object?>(rows.Length + 25_000);
            foreach (var row in rows)
            {
                var address = new Address(row[0]!.ToString()!);
                seed[address] = null;
            }

            KnownSafeProxies.Seed(seed);

            logger.Info($" * Cached {seed.Count} ProxyCreation events");
        }

        return Task.CompletedTask;
    }

    public IRollbackCache[] Caches { get; } = [KnownSafeProxies];

    private readonly Hash256 _proxyCreationTopic = new(DatabaseSchema.ProxyCreation.Topic);
    private readonly byte[] _legacyCrcProxyCreationTopic = KeccakHelper.ComputeHash("ProxyCreation(address)");
    private readonly Hash256 _safeSetupTopic = new(DatabaseSchema.SafeSetup.Topic);
    private readonly Hash256 _addedOwnerTopic = new(DatabaseSchema.AddedOwner.Topic);
    private readonly Hash256 _removedOwnerTopic = new(DatabaseSchema.RemovedOwner.Topic);

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events)
    {
        // Depending on the log index, the SafeSetup or Added/RemovedOwner event might be emitted before the ProxyCreation event.
        // In that case, we need to wait for the ProxyCreation event to be parsed before we can parse the SafeSetup event.
        if (!events.Any(e => e is ProxyCreation))
        {
            yield break;
        }

        var eventsHashSet =
            events.ToHashSet(
                new DelegateEqualityComparer<IIndexEvent>(
                    (a, b) => a.TransactionHash == b.TransactionHash && a.LogIndex == b.LogIndex,
                    obj => obj.TransactionHash.GetHashCode() ^ obj.LogIndex.GetHashCode()));

        for (int i = 0; i < receipt.Logs?.Length; i++)
        {
            var log = receipt.Logs[i];
            var topic = log.Topics[0];

            if (KnownSafeProxies.ContainsKey(log.Address))
            {
                if (topic == _safeSetupTopic)
                {
                    foreach (var safeSetupEvent in SafeSetup(block, transaction, receipt, log, i))
                    {
                        if (!eventsHashSet.Contains(safeSetupEvent))
                        {
                            yield return safeSetupEvent;
                        }
                    }
                }
                else if (topic == _addedOwnerTopic)
                {
                    var addedOwner = AddedOwner(block, transaction, receipt, log, i);
                    if (!eventsHashSet.Contains(addedOwner))
                    {
                        yield return addedOwner;
                    }
                }
                else if (topic == _removedOwnerTopic)
                {
                    var removedOwner = RemovedOwner(block, transaction, receipt, log, i);
                    if (!eventsHashSet.Contains(removedOwner))
                    {
                        yield return removedOwner;
                    }
                }
            }
        }
    }

    public IEnumerable<IIndexEvent> ParseLog(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        if (log.Topics.Length == 0)
            yield break;

        var topic = log.Topics[0];

        if (factoryAddresses.Contains(log.Address))
        {
            if (topic == _proxyCreationTopic || topic.Bytes.SequenceEqual(_legacyCrcProxyCreationTopic))
            {
                var evt = ProxyCreation(block, receipt, log, logIndex);
                KnownSafeProxies.Add(block.Number, new Address(evt.Proxy), null);
                yield return evt;
            }
        }

        if (KnownSafeProxies.ContainsKey(log.Address))
        {
            if (topic == _safeSetupTopic)
            {
                foreach (var safeSetupEvent in SafeSetup(block, transaction, receipt, log, logIndex))
                {
                    yield return safeSetupEvent;
                }
            }
            else if (topic == _addedOwnerTopic)
            {
                yield return AddedOwner(block, transaction, receipt, log, logIndex);
            }
            else if (topic == _removedOwnerTopic)
            {
                yield return RemovedOwner(block, transaction, receipt, log, logIndex);
            }
        }
    }

    private ProxyCreation ProxyCreation(
        Block block,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        string proxy = "";
        string? singleton = null;

        if (log.Topics.Length == 1)
        {
            // event ProxyCreation(address proxy);
            // event ProxyCreation(address proxy, address singleton);
            proxy = new Address(log.Data.Slice(12, 20)).ToLowerHex();
            if (log.Data.Length > 32)
            {
                singleton = new Address(log.Data.Slice(44)).ToLowerHex();
            }
        }
        else if (log.Topics.Length == 2)
        {
            // event ProxyCreation(address indexed proxy, address singleton);
            proxy = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
            singleton = new Address(log.Data.Slice(12)).ToLowerHex();
        }
        else
        {
            throw new Exception("Unsupported ProxyCreation event format");
        }

        return new ProxyCreation(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            log.Address.ToLowerHex(),
            proxy,
            singleton
        );
    }

    private IEnumerable<SafeSetup> SafeSetup(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        // event SafeSetup(address indexed initiator, address[] owners, uint256 threshold, address indexed initializer, address fallbackHandler)
        string initiator = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var dataSpan = log.Data.AsSpan();

        int ownersOffset = LogDataParsingHelper.ParseOffset(dataSpan, 0);
        string[] owners = LogDataParsingHelper.ParseAddressArray(dataSpan, ownersOffset);

        var thresholdBytes = dataSpan.Slice(32, 32);
        UInt256 threshold = new UInt256(thresholdBytes, true);

        var initializerBytes = dataSpan.Slice(64, 32);
        string initializer = new Address(initializerBytes.Slice(12, 20).ToArray()).ToLowerHex();

        var fallbackHandlerBytes = dataSpan.Slice(96, 32);
        string fallbackHandler = new Address(fallbackHandlerBytes.Slice(12, 20).ToArray()).ToLowerHex();

        string safeAddress = log.Address.ToLowerHex();

        for (int i = 0; i < owners.Length; i++)
        {
            yield return new SafeSetup(
                BlockNumber: block.Number,
                Timestamp: (long)block.Timestamp,
                TransactionIndex: receipt.Index,
                LogIndex: logIndex,
                BatchIndex: i,
                TransactionHash: receipt.TxHash!.ToString(),
                Emitter: safeAddress,
                SafeAddress: safeAddress,
                Initiator: initiator,
                Owner: owners[i],
                Threshold: (BigInteger)threshold,
                Initializer: initializer,
                FallbackHandler: fallbackHandler
            );
        }
    }

    private AddedOwner AddedOwner(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length == 32)
        {
            // event AddedOwner(address owner) => no indexed fields => single address is in log.Data
            var ownerBytes = dataSpan.Slice(12, 20);
            string owner = new Address(ownerBytes.ToArray()).ToLowerHex();

            string safeAddr = log.Address.ToLowerHex();

            return new AddedOwner(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                safeAddr,
                safeAddr,
                owner
            );
        }
        else
        {
            // event AddedOwner(address indexed owner)
            var ownerAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
            string safeAddr = log.Address.ToLowerHex();

            return new AddedOwner(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                safeAddr,
                safeAddr,
                ownerAddress
            );
        }
    }

    private RemovedOwner RemovedOwner(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length == 32)
        {
            // event RemovedOwner(address owner) => no indexed fields => single address is in log.Data
            var ownerBytes = dataSpan.Slice(12, 20);
            string owner = new Address(ownerBytes.ToArray()).ToLowerHex();

            string safeAddr = log.Address.ToLowerHex();

            return new RemovedOwner(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                safeAddr,
                safeAddr,
                owner
            );
        }
        else
        {
            // event RemovedOwner(address indexed owner)
            var ownerAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
            string safeAddr = log.Address.ToLowerHex();

            return new RemovedOwner(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                safeAddr,
                safeAddr,
                ownerAddress
            );
        }
    }
}
