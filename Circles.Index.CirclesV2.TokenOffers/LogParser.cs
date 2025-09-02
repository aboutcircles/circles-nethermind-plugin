using System.Collections.Immutable;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Core.Extensions;

namespace Circles.Index.CirclesV2.TokenOffers;

public class LogParser(ImmutableHashSet<Address> factoryAddresses) : ILogParser
{
    // Topics
    private static readonly Hash256 _accountWeightProviderCreated =
        new(DatabaseSchema.AccountWeightProviderCreated.Topic);

    private static readonly Hash256 _erc20TokenOfferCreated = new(DatabaseSchema.ERC20TokenOfferCreated.Topic);

    private static readonly Hash256 _erc20TokenOfferCycleCreated =
        new(DatabaseSchema.ERC20TokenOfferCycleCreated.Topic);

    private static readonly Hash256 _cycleConfiguration = new(DatabaseSchema.CycleConfiguration.Topic);
    private static readonly Hash256 _nextOfferCreated = new(DatabaseSchema.NextOfferCreated.Topic);
    private static readonly Hash256 _nextOfferTokensDeposited = new(DatabaseSchema.NextOfferTokensDeposited.Topic);
    private static readonly Hash256 _offerTrustSynced = new(DatabaseSchema.OfferTrustSynced.Topic);
    private static readonly Hash256 _offerClaimedFromCycle = new(DatabaseSchema.OfferClaimedFromCycle.Topic);
    private static readonly Hash256 _unclaimedTokensWithdrawn = new(DatabaseSchema.UnclaimedTokensWithdrawn.Topic);

    private static readonly Hash256 _offerClaimed = new(DatabaseSchema.OfferClaimed.Topic);
    private static readonly Hash256 _offerTokensDeposited = new(DatabaseSchema.OfferTokensDeposited.Topic);

    private static readonly Hash256 _accountWeightSet = new(DatabaseSchema.AccountWeightSet.Topic);
    private static readonly Hash256 _weightsFinalized = new(DatabaseSchema.WeightsFinalized.Topic);

    // Caches
    public static readonly RollbackCache<Address, object?> OfferCycles = new("OfferCycles");
    public static readonly RollbackCache<Address, object?> TokenOffers = new("TokenOffers");
    public static readonly RollbackCache<Address, object?> AccountWeightProviders = new("AccountWeightProviders");

    public IRollbackCache[] Caches { get; } = [OfferCycles, TokenOffers, AccountWeightProviders];

    public Task InitCaches(InterfaceLogger logger, IDatabase database, Settings settings)
    {
        // Seed from previously indexed tables
        var seedsCycles = new Dictionary<Address, object?>();
        var cycles = new Circles.Index.Query.Select("CrcV2_TokenOffers", "ERC20TokenOfferCycleCreated", ["offerCycle"],
            [], [],
            int.MaxValue, false, int.MaxValue);
        foreach (var row in database.Select(cycles.ToSql(database)).Rows)
        {
            seedsCycles[new Address(row[0]!.ToString()!)] = null;
        }

        OfferCycles.Seed(seedsCycles);
        logger.Info($" * Cached {seedsCycles.Count} ERC20TokenOfferCycleCreated entries");


        var seedsOffers = new Dictionary<Address, object?>();
        var offers1 = new Circles.Index.Query.Select("CrcV2_TokenOffers", "ERC20TokenOfferCreated", ["tokenOffer"], [],
            [],
            int.MaxValue, false, int.MaxValue);
        foreach (var row in database.Select(offers1.ToSql(database)).Rows)
        {
            seedsOffers[new Address(row[0]!.ToString()!)] = null;
        }

        // also NextOfferCreated
        var offers2 = new Circles.Index.Query.Select("CrcV2_TokenOffers", "NextOfferCreated", ["nextOffer"], [], [],
            int.MaxValue, false, int.MaxValue);
        foreach (var row in database.Select(offers2.ToSql(database)).Rows)
        {
            seedsOffers[new Address(row[0]!.ToString()!)] = null;
        }

        TokenOffers.Seed(seedsOffers);
        logger.Info($" * Cached {seedsOffers.Count} TokenOffers");


        var seedsProviders = new Dictionary<Address, object?>();
        var providers = new Circles.Index.Query.Select("CrcV2_TokenOffers", "AccountWeightProviderCreated",
            ["provider"], [],
            [], int.MaxValue, false, int.MaxValue);
        foreach (var row in database.Select(providers.ToSql(database)).Rows)
        {
            seedsProviders[new Address(row[0]!.ToString()!)] = null;
        }

        // plus providers from CycleConfiguration (accountWeightProvider)
        var providers2 = new Circles.Index.Query.Select("CrcV2_TokenOffers", "CycleConfiguration",
            ["accountWeightProvider"],
            [], [], int.MaxValue, false, int.MaxValue);
        foreach (var row in database.Select(providers2.ToSql(database)).Rows)
        {
            seedsProviders[new Address(row[0]!.ToString()!)] = null;
        }

        AccountWeightProviders.Seed(seedsProviders);
        logger.Info($" * Cached {seedsProviders.Count} AccountWeightProviders");

        return Task.CompletedTask;
    }

    public IEnumerable<IIndexEvent> ParseTransaction(
        Block block,
        int transactionIndex,
        Transaction transaction,
        TxReceipt receipt,
        IReadOnlyList<IIndexEvent> events)
    {
        // Capture CycleConfiguration emitted before factory's CycleCreated in the same tx
        var cyclesInTx = events.OfType<ERC20TokenOfferCycleCreated>().Select(e => e.OfferCycle).ToHashSet();
        if (cyclesInTx.Count == 0)
        {
            yield break;
        }

        for (int i = 0; i < receipt.Logs.Length; i++)
        {
            var log = receipt.Logs[i];
            if (!cyclesInTx.Contains(log.Address.ToString(true, false))) continue;
            if (log.Topics.Length == 0) continue;
            if (log.Topics[0] == _cycleConfiguration)
            {
                yield return ParseCycleConfiguration(block, receipt, log, i);
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
        {
            yield break;
        }

        var topic = log.Topics[0];

        if (factoryAddresses.Contains(log.Address))
        {
            if (topic == _accountWeightProviderCreated)
            {
                yield return ParseAccountWeightProviderCreated(block, receipt, log, logIndex);
            }
            else if (topic == _erc20TokenOfferCreated)
            {
                yield return ParseErc20TokenOfferCreated(block, receipt, log, logIndex);
            }
            else if (topic == _erc20TokenOfferCycleCreated)
            {
                yield return ParseErc20TokenOfferCycleCreated(block, receipt, log, logIndex);
            }

            yield break;
        }

        // Cycle events
        if (OfferCycles.ContainsKey(log.Address))
        {
            if (topic == _cycleConfiguration)
            {
                yield return ParseCycleConfiguration(block, receipt, log, logIndex);
            }
            else if (topic == _nextOfferCreated)
            {
                yield return ParseNextOfferCreated(block, receipt, log, logIndex);
            }
            else if (topic == _nextOfferTokensDeposited)
            {
                yield return ParseNextOfferTokensDeposited(block, receipt, log, logIndex);
            }
            else if (topic == _offerTrustSynced)
            {
                yield return ParseOfferTrustSynced(block, receipt, log, logIndex);
            }
            else if (topic == _offerClaimedFromCycle)
            {
                yield return ParseOfferClaimedFromCycle(block, receipt, log, logIndex);
            }
            else if (topic == _unclaimedTokensWithdrawn)
            {
                yield return ParseUnclaimedTokensWithdrawn(block, receipt, log, logIndex);
            }

            yield break;
        }

        // Offer events
        if (TokenOffers.ContainsKey(log.Address))
        {
            if (topic == _offerClaimed)
            {
                yield return ParseOfferClaimed(block, receipt, log, logIndex);
            }
            else if (topic == _offerTokensDeposited)
            {
                yield return ParseOfferTokensDeposited(block, receipt, log, logIndex);
            }

            yield break;
        }

        // Provider events
        if (AccountWeightProviders.ContainsKey(log.Address))
        {
            if (topic == _accountWeightSet)
            {
                yield return ParseAccountWeightSet(block, receipt, log, logIndex);
            }
            else if (topic == _weightsFinalized)
            {
                yield return ParseWeightsFinalized(block, receipt, log, logIndex);
            }
        }
    }

    // Parsers
    private AccountWeightProviderCreated ParseAccountWeightProviderCreated(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        var provider = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var admin = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        var address = new Address(provider);
        AccountWeightProviders.Add(block.Number, address, null);

        return new AccountWeightProviderCreated(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToString(true, false), provider, admin);
    }

    private ERC20TokenOfferCreated ParseErc20TokenOfferCreated(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        string tokenOffer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string offerOwner = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string accountWeightProvider = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        var data = log.Data;
        string offerToken = new Address(data.Slice(12, 20).ToArray()).ToString(true, false);
        var tokenPriceInCRC = LogDataParsingHelper.ParseSingleUInt256(data.Slice(32, 32));
        var offerLimitInCRC = LogDataParsingHelper.ParseSingleUInt256(data.Slice(64, 32));
        var offerStart = LogDataParsingHelper.ParseSingleUInt256(data.Slice(96, 32));
        var offerEnd = LogDataParsingHelper.ParseSingleUInt256(data.Slice(128, 32));
        int orgOffset = LogDataParsingHelper.ParseOffset(data, 160);
        string orgName = LogDataParsingHelper.ParseString(data, orgOffset);
        int accCrcOffset = LogDataParsingHelper.ParseOffset(data, 192);
        string[] acceptedCRC = LogDataParsingHelper.ParseAddressArray(data, accCrcOffset);

        TokenOffers.Add(block.Number, new Address(tokenOffer), null);
        AccountWeightProviders.Add(block.Number, new Address(accountWeightProvider), null);

        return new ERC20TokenOfferCreated(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToString(true, false), tokenOffer, offerOwner,
            accountWeightProvider,
            offerToken, tokenPriceInCRC, offerLimitInCRC, offerStart,
            offerEnd, orgName, acceptedCRC);
    }

    private ERC20TokenOfferCycleCreated ParseErc20TokenOfferCycleCreated(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        string offerCycle = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string cycleOwner = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string offerTokenIdx = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        var data = log.Data;
        var offersStart = LogDataParsingHelper.ParseSingleUInt256(data.Slice(0, 32));
        var offerDuration = LogDataParsingHelper.ParseSingleUInt256(data.Slice(32, 32));
        string offerName = LogDataParsingHelper.ParseString(data, LogDataParsingHelper.ParseOffset(data, 64));
        string cycleName = LogDataParsingHelper.ParseString(data, LogDataParsingHelper.ParseOffset(data, 96));

        OfferCycles.Add(block.Number, new Address(offerCycle), null);

        return new ERC20TokenOfferCycleCreated(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToString(true, false), offerCycle, cycleOwner, offerTokenIdx,
            offersStart, offerDuration, offerName, cycleName);
    }

    private CycleConfiguration ParseCycleConfiguration(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string admin = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string accountWeightProvider = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string offerToken = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);
        var data = log.Data;
        var offersStart = LogDataParsingHelper.ParseSingleUInt256(data.Slice(0, 32));
        var offerDuration = LogDataParsingHelper.ParseSingleUInt256(data.Slice(32, 32));
        var softLockEnabled = !LogDataParsingHelper.ParseSingleUInt256(data.Slice(64, 32)).IsZero;

        AccountWeightProviders.Add(block.Number, new Address(accountWeightProvider), null);

        return new CycleConfiguration(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToString(true, false), admin, accountWeightProvider, offerToken,
            offersStart, offerDuration, softLockEnabled);
    }

    private NextOfferCreated ParseNextOfferCreated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string nextOffer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var tokenPriceInCRC = LogDataParsingHelper.ParseSingleUInt256(log.Topics[2].Bytes);
        var offerLimitInCRC = LogDataParsingHelper.ParseSingleUInt256(log.Topics[3].Bytes);
        var data = log.Data;
        string[] acceptedCRC = LogDataParsingHelper.ParseAddressArray(data, LogDataParsingHelper.ParseOffset(data, 0));

        TokenOffers.Add(block.Number, new Address(nextOffer), null);

        return new NextOfferCreated(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToString(true, false), nextOffer, tokenPriceInCRC, offerLimitInCRC,
            acceptedCRC);
    }

    private NextOfferTokensDeposited ParseNextOfferTokensDeposited(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        string nextOffer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var amount = LogDataParsingHelper.ParseSingleUInt256(log.Topics[2].Bytes);
        return new NextOfferTokensDeposited(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToString(true, false), nextOffer, amount);
    }

    private OfferTrustSynced ParseOfferTrustSynced(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var offerId = LogDataParsingHelper.ParseSingleUInt256(log.Topics[1].Bytes);
        string offer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        return new OfferTrustSynced(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToString(true, false), offerId, offer);
    }

    private OfferClaimedFromCycle ParseOfferClaimedFromCycle(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string offer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        var received = LogDataParsingHelper.ParseSingleUInt256(log.Topics[3].Bytes);
        var spent = LogDataParsingHelper.ParseSingleUInt256(log.Data.Slice(0, 32));
        return new OfferClaimedFromCycle(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToString(true, false), offer, account, received, spent);
    }

    private UnclaimedTokensWithdrawn ParseUnclaimedTokensWithdrawn(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        string offer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var amount = LogDataParsingHelper.ParseSingleUInt256(log.Topics[2].Bytes);
        return new UnclaimedTokensWithdrawn(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToString(true, false), offer, amount);
    }

    private OfferClaimed ParseOfferClaimed(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var spent = LogDataParsingHelper.ParseSingleUInt256(log.Topics[2].Bytes);
        var received = LogDataParsingHelper.ParseSingleUInt256(log.Topics[3].Bytes);
        return new OfferClaimed(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToString(true, false), account, spent, received);
    }

    private OfferTokensDeposited ParseOfferTokensDeposited(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var amount = LogDataParsingHelper.ParseSingleUInt256(log.Topics[1].Bytes);
        return new OfferTokensDeposited(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToString(true, false), amount);
    }

    private AccountWeightSet ParseAccountWeightSet(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string offer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        var weight = LogDataParsingHelper.ParseSingleUInt256(log.Topics[3].Bytes);
        return new AccountWeightSet(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToString(true, false), offer, account, weight);
    }

    private WeightsFinalized ParseWeightsFinalized(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string offer = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var accountsCount = LogDataParsingHelper.ParseSingleUInt256(log.Topics[2].Bytes);
        var totalWeight = LogDataParsingHelper.ParseSingleUInt256(log.Topics[3].Bytes);
        return new WeightsFinalized(block.Number, (long)block.Timestamp, receipt.Index, logIndex,
            receipt.TxHash!.ToString(), log.Address.ToString(true, false), offer, accountsCount, totalWeight);
    }
}