using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using Circles.Index.CirclesV2.CMGroupDeployer;
using Circles.Index.CirclesV2.Hub;
using Circles.Index.CirclesV2.LBP;
using Circles.Index.CirclesV2.NameRegistry;
using Circles.Index.CirclesV2.StandardTreasury;
using Circles.Index.CirclesV2.Decoders;
using Circles.Index.Common;
using Circles.Index.Query;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Circles.Index.CirclesV2;

// Example "operateFlowMatrix" call-data structure:
public record ParsedOperateFlowMatrix(
    string FunctionName,
    string[] FlowVertices,
    FlowEdge[] FlowEdges,
    StreamStruct[] Streams,
    byte[] PackedCoordinates
) : IParsedCallData;

// For ERC-1155 single transfer:
public record ParsedSafeTransferFrom(
    string FunctionName,
    string From,
    string To,
    UInt256 TokenId,
    UInt256 Amount,
    byte[] Data
) : IParsedCallData;

// For ERC-1155 batch transfer:
public record ParsedSafeBatchTransferFrom(
    string FunctionName,
    string From,
    string To,
    UInt256[] Ids,
    UInt256[] Amounts,
    byte[] Data
) : IParsedCallData;

// A couple of helper records mirroring your CirclesHubDecoder “FlowEdge” and “StreamStruct”:
public record FlowEdge(ushort StreamSinkId, UInt256 Amount);

public record StreamStruct(
    ushort SourceCoordinate,
    ushort[] FlowEdgeIds,
    byte[] Data
);

public class LogParser : ILogParser
{
    private readonly ImmutableArray<KnownContract> _knownContracts = ImmutableArray<KnownContract>.Empty;

    public readonly KnownContract HubContract;
    public readonly KnownContract Erc20LiftContract;
    public readonly KnownContract DemurrageCircles;
    public readonly KnownContract InflationaryCircles;
    public readonly KnownContract NameRegistryContract;
    public readonly KnownContract StandardTreasuryContract;
    public readonly KnownContract? CmGroupDeployerContract;
    public readonly KnownContract? LbpFactoryContract;

    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        WriteIndented = false,
        Converters =
        {
            new UInt256AsStringConverter()
        }
    };

    public LogParser(Address v2HubAddress,
        Address erc20LiftAddress,
        Address nameRegistryAddress,
        Address standardTreasuryAddress,
        Address[] cmGroupDeployerAddresses,
        Address? lbpFactoryAddress)
    {
        var tryParseOperateFlowMatrix = new Func<byte[], int, IEnumerable<IParsedCallData>>((data, offset) =>
        {
            // We'll parse a single operateFlowMatrix(...) call from the raw `data` (including the selector),
            // starting at the given byte offset. If decoding fails, we return an empty list.

            // The function signature is 0x0d22d9b5, so the first 4 bytes are the selector.
            // We skip those 4 bytes => arguments start at offset+4.

            // The function has 4 arguments (all dynamic in standard ABI):
            //   1) address[] _flowVertices
            //   2) (uint16, uint192)[] _flow
            //   3) (uint16, uint16[], bytes)[] _streams
            //   4) bytes _packedCoordinates
            // Each argument is a 32-byte “offset” (since they’re dynamic).
            // So we read four “words” to get the offsets, then decode each region.

            // For convenience, we define some local helpers:
            static BigInteger ToBigInt(ReadOnlySpan<byte> span)
            {
                // same style as event parsing (big-endian from 0..32)
                return new BigInteger(span, isBigEndian: true);
            }

            static string ToAddress(ReadOnlySpan<byte> word32)
            {
                // “address” is last 20 bytes of the 32-byte word
                var addressBytes = word32[^20..]; // slice last 20
                return "0x" + BitConverter.ToString(addressBytes.ToArray()).Replace("-", "").ToLower();
            }

            List<IParsedCallData> results = new();

            try
            {
                // 1) We need to ensure at least 4 * 32 bytes after the selector
                int minNeeded = 4 * 32;
                if (data.Length < offset + 4 + minNeeded)
                    return results; // not enough data

                // read the four offset-words
                // each word is 32 bytes => data[(offset + 4 + i*32) .. (offset + 4 + (i+1)*32)]
                ReadOnlySpan<byte> arg0OffsetBytes = data.AsSpan(offset + 4 + 0 * 32, 32);
                ReadOnlySpan<byte> arg1OffsetBytes = data.AsSpan(offset + 4 + 1 * 32, 32);
                ReadOnlySpan<byte> arg2OffsetBytes = data.AsSpan(offset + 4 + 2 * 32, 32);
                ReadOnlySpan<byte> arg3OffsetBytes = data.AsSpan(offset + 4 + 3 * 32, 32);

                BigInteger offsetFlowVertices = ToBigInt(arg0OffsetBytes);
                BigInteger offsetFlow = ToBigInt(arg1OffsetBytes);
                BigInteger offsetStreams = ToBigInt(arg2OffsetBytes);
                BigInteger offsetPackedCoords = ToBigInt(arg3OffsetBytes);

                // decode each parameter
                var flowVertices = DecodeAddressArray(data, offset, offsetFlowVertices);
                var flowEdges = DecodeFlowEdgesArray(data, offset, offsetFlow);
                var streams = DecodeStreamsArray(data, offset, offsetStreams);
                var packedCoords = DecodeBytes(data, offset, (int)offsetPackedCoords);

                results.Add(new ParsedOperateFlowMatrix("operateFlowMatrix", flowVertices, flowEdges, streams,
                    packedCoords));
            }
            catch
            {
                // If something fails, we skip. 
                // You might want to log the error; for now we just return empty.
            }

            return results;
        });

        var tryParseSafeTransferFrom = new Func<byte[], int, IEnumerable<IParsedCallData>>((data, offset) =>
        {
            // single “safeTransferFrom” call => 5 arguments:
            //   (address from, address to, uint256 id, uint256 amount, bytes data)
            // each argument is 32 bytes except the last which is dynamic => an offset to the actual bytes.

            List<IParsedCallData> results = new();

            static BigInteger ToBigInt(ReadOnlySpan<byte> span)
                => new BigInteger(span, isBigEndian: true);

            static string ToAddress(ReadOnlySpan<byte> word32)
                => "0x" + BitConverter.ToString(word32[^20..].ToArray()).Replace("-", "").ToLower();

            try
            {
                // we skip 4 bytes for the selector
                // then we read 5 “words” => need at least 5*32 = 160 bytes
                int minNeeded = 5 * 32;
                if (data.Length < offset + 4 + minNeeded)
                    return results;

                var fromWord = data.AsSpan(offset + 4 + 0 * 32, 32);
                var toWord = data.AsSpan(offset + 4 + 1 * 32, 32);
                var idWord = data.AsSpan(offset + 4 + 2 * 32, 32);
                var amtWord = data.AsSpan(offset + 4 + 3 * 32, 32);
                var dataOffset = data.AsSpan(offset + 4 + 4 * 32, 32);

                string from = ToAddress(fromWord);
                string to = ToAddress(toWord);
                var tokenId = (UInt256)ToBigInt(idWord);
                var amount = (UInt256)ToBigInt(amtWord);
                var dataOff = (int)ToBigInt(dataOffset); // just cast, we assume it fits

                byte[] extraData = Array.Empty<byte>();
                if (dataOff != 0)
                {
                    extraData = DecodeBytes(data, offset, dataOff);
                }

                results.Add(new ParsedSafeTransferFrom("safeTransaferFrom", from, to, tokenId, amount, extraData));
            }
            catch
            {
                // ignore errors
            }

            return results;
        });

        var tryParseSafeBatchTransferFrom = new Func<byte[], int, IEnumerable<IParsedCallData>>((data, offset) =>
        {
            // single “safeBatchTransferFrom” => 5 arguments:
            //    (address from, address to, uint256[] ids, uint256[] amounts, bytes data)
            // each argument is 32 bytes => offset to a dynamic region

            List<IParsedCallData> results = new();

            static BigInteger ToBigInt(ReadOnlySpan<byte> span)
                => new BigInteger(span, isBigEndian: true);

            static string ToAddress(ReadOnlySpan<byte> word32)
                => "0x" + BitConverter.ToString(word32[^20..].ToArray()).Replace("-", "").ToLower();

            try
            {
                int minNeeded = 5 * 32;
                if (data.Length < offset + 4 + minNeeded)
                    return results;

                var fromWord = data.AsSpan(offset + 4 + 0 * 32, 32);
                var toWord = data.AsSpan(offset + 4 + 1 * 32, 32);
                var idsOffset = data.AsSpan(offset + 4 + 2 * 32, 32);
                var amtsOffset = data.AsSpan(offset + 4 + 3 * 32, 32);
                var dataOffset = data.AsSpan(offset + 4 + 4 * 32, 32);

                string from = ToAddress(fromWord);
                string to = ToAddress(toWord);

                var idsOffVal = (int)ToBigInt(idsOffset);
                var amtOffVal = (int)ToBigInt(amtsOffset);
                var dataOffVal = (int)ToBigInt(dataOffset);

                var ids = DecodeUint256Array(data, offset, idsOffVal);
                var amounts = DecodeUint256Array(data, offset, amtOffVal);
                var extraData = Array.Empty<byte>();
                if (dataOffVal != 0)
                {
                    extraData = DecodeBytes(data, offset, dataOffVal);
                }

                results.Add(
                    new ParsedSafeBatchTransferFrom("safeBatchTransaferFrom", from, to, ids, amounts, extraData));
            }
            catch
            {
                // ignore
            }

            return results;
        });

        HubContract = new("CrcV2", "Hub", [v2HubAddress], [
            (Hub.DatabaseSchema.Stopped.Topic, CrcV2Stopped),
            (Hub.DatabaseSchema.Trust.Topic, CrcV2Trust),
            (Hub.DatabaseSchema.PersonalMint.Topic, CrcV2PersonalMint),
            (Hub.DatabaseSchema.RegisterHuman.Topic, CrcV2RegisterHuman),
            (Hub.DatabaseSchema.RegisterGroup.Topic, CrcV2RegisterGroup),
            (Hub.DatabaseSchema.RegisterOrganization.Topic, CrcV2RegisterOrganization),
            (Hub.DatabaseSchema.TransferBatch.Topic, Erc1155TransferBatch),
            (Hub.DatabaseSchema.TransferSingle.Topic, Erc1155TransferSingle),
            (Hub.DatabaseSchema.ApprovalForAll.Topic, Erc1155ApprovalForAll),
            (Hub.DatabaseSchema.GroupMint.Topic, GroupMint),
            (Hub.DatabaseSchema.StreamCompleted.Topic, StreamCompleted),
            (Hub.DatabaseSchema.DiscountCost.Topic, DiscountCost),
            (Hub.DatabaseSchema.FlowEdgesScopeSingleStarted.Topic, FlowEdgesScopeSingleStarted),
            (Hub.DatabaseSchema.FlowEdgesScopeLastEnded.Topic, FlowEdgesScopeLastEnded)
        ], [
            new("operateFlowMatrix", [0x0d, 0x22, 0xd9, 0xb5], tryParseOperateFlowMatrix),
            new("safeTransferFrom", [0xf2, 0x42, 0x43, 0x2a], tryParseSafeTransferFrom),
            new("safeBatchTransferFrom", [0x2e, 0xb2, 0xc2, 0xd6], tryParseSafeBatchTransferFrom)
        ]);
        _knownContracts = _knownContracts.Add(HubContract);

        Erc20LiftContract = new("CrcV2", "Erc20Lift", [erc20LiftAddress], [
            (Hub.DatabaseSchema.ERC20WrapperDeployed.Topic, Erc20WrapperDeployed)
        ]);
        _knownContracts = _knownContracts.Add(Erc20LiftContract);

        DemurrageCircles = new("CrcV2", "DemurrageCircles", [], [
            (Hub.DatabaseSchema.Erc20WrapperTransfer.Topic, Erc20WrapperTransfer),
            (Hub.DatabaseSchema.DepositDemurraged.Topic, DepositDemurraged),
            (Hub.DatabaseSchema.WithdrawDemurraged.Topic, WithdrawDemurraged)
        ]);
        _knownContracts = _knownContracts.Add(DemurrageCircles);

        InflationaryCircles = new("CrcV2", "InflationaryCircles", [], [
            (Hub.DatabaseSchema.Erc20WrapperTransfer.Topic, Erc20WrapperTransfer),
            (Hub.DatabaseSchema.DepositInflationary.Topic, DepositInflationary),
            (Hub.DatabaseSchema.WithdrawInflationary.Topic, WithdrawInflationary)
        ]);
        _knownContracts = _knownContracts.Add(InflationaryCircles);

        NameRegistryContract = new("CrcV2", "NameRegistry", [nameRegistryAddress], [
            (NameRegistry.DatabaseSchema.RegisterShortName.Topic, RegisterShortName),
            (NameRegistry.DatabaseSchema.UpdateMetadataDigest.Topic, UpdateMetadataDigest),
            (NameRegistry.DatabaseSchema.CidV0.Topic, CidV0)
        ]);
        _knownContracts = _knownContracts.Add(NameRegistryContract);

        StandardTreasuryContract = new("CrcV2", "StandardTreasury", [standardTreasuryAddress], [
            (StandardTreasury.DatabaseSchema.CreateVault.Topic, CreateVault),
            (StandardTreasury.DatabaseSchema.CollateralLockedSingle.Topic, CollateralLockedSingle),
            (StandardTreasury.DatabaseSchema.CollateralLockedBatch.Topic, CollateralLockedBatch),
            (StandardTreasury.DatabaseSchema.GroupRedeem.Topic, GroupRedeem),
            (StandardTreasury.DatabaseSchema.GroupRedeemCollateralReturn.Topic, GroupRedeemCollateralReturn),
            (StandardTreasury.DatabaseSchema.GroupRedeemCollateralBurn.Topic, GroupRedeemCollateralBurn)
        ]);
        _knownContracts = _knownContracts.Add(StandardTreasuryContract);

        if (cmGroupDeployerAddresses.Length > 0)
        {
            var cmGroupCreatedTopicNew =
                Keccak.Compute("CMGroupCreated(address,address,address,address,address)");

            var cmGroupCreatedTopicOld =
                Keccak.Compute("CMGroupCreated(address,address,address,address)");

            var cmGroupDeployer = new KnownContract("CrcV2", "CmGroupDeployer", cmGroupDeployerAddresses, [
                (cmGroupCreatedTopicOld, CMGroupCreated),
                (cmGroupCreatedTopicNew, CMGroupCreated)
            ]);
            CmGroupDeployerContract = cmGroupDeployer;
            _knownContracts = _knownContracts.Add(CmGroupDeployerContract);
        }

        if (lbpFactoryAddress != null)
        {
            var lbpFactory = new KnownContract("CrcV2", "LbpFactory", [lbpFactoryAddress], [
                (LBP.DatabaseSchema.CirclesBackingDeployed.Topic, CirclesBackingDeployed),
                (LBP.DatabaseSchema.LBPDeployed.Topic, LbpDeployed),
                (LBP.DatabaseSchema.CirclesBackingInitiated.Topic, CirclesBackingInitiated),
                (LBP.DatabaseSchema.CirclesBackingCompleted.Topic, CirclesBackingCompleted),
                (LBP.DatabaseSchema.Released.Topic, Released)
            ]);
            LbpFactoryContract = lbpFactory;
            _knownContracts = _knownContracts.Add(LbpFactoryContract);
        }
    }

    public Task Init(
        InterfaceLogger logger,
        IDatabase database,
        Settings settings)
    {
        logger.Info("Caching erc20 wrapper addresses");

        var selectErc20WrapperDeployed = new Select(
            "CrcV2",
            "ERC20WrapperDeployed",
            ["erc20Wrapper", "circlesType"],
            [],
            [],
            int.MaxValue,
            false,
            int.MaxValue);

        var sql = selectErc20WrapperDeployed.ToSql(database);
        var result = database.Select(sql);
        object?[][] rows = result.Rows.ToArray();

        logger.Info($" * Found {rows.Length} erc20 wrapper addresses");

        DemurrageCircles.AddInstances(
            rows.Where(row => (long)row[1]! == 0L)
                .Select(row => new Address(row[0]!.ToString()!)));

        logger.Info($"   * {DemurrageCircles.Instances.Count} demurraged");

        InflationaryCircles.AddInstances(
            rows.Where(row => (long)row[1]! == 1L)
                .Select(row => new Address(row[0]!.ToString()!)));

        logger.Info($"   * {InflationaryCircles.Instances.Count} inflationary");
        logger.Info("Caching erc20 wrapper addresses done");

        return Task.CompletedTask;
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

        for (int i = 0; i < _knownContracts.Length; i++)
        {
            var knownContract = _knownContracts[i];

            // Check if the contract has a parser for the topic
            if (!knownContract.TryGetParser(topic, out var parseLogDelegate) || parseLogDelegate == null)
            {
                continue;
            }

            // Check if we know the address
            if (!knownContract.IsKnownAddress(log.Address))
            {
                continue;
            }

            // Parse the log using the appropriate parser
            foreach (var ev in parseLogDelegate(block, receipt, log, logIndex))
            {
                switch (ev)
                {
                    // If we encounter a ERC20WrapperDeployed event, add the wrapper address to the list of known wrapper instances.
                    case ERC20WrapperDeployed { CirclesType: 0L } demurrageWrapperDeployed:
                        DemurrageCircles.AddInstances([new Address(demurrageWrapperDeployed.Erc20Wrapper)]);
                        break;
                    case ERC20WrapperDeployed { CirclesType: 1L } inflationaryWrapperDeployed:
                        InflationaryCircles.AddInstances([new Address(inflationaryWrapperDeployed.Erc20Wrapper)]);
                        break;
                }

                yield return ev;
            }
        }
    }

    public IEnumerable<Either<IIndexEvent, IParsedCallData>> ParseTransaction(
        Block block,
        Transaction transaction,
        TxReceipt receipt,
        IIndexEvent[] events)
    {
        if (events.Length == 0)
        {
            yield break;
        }

        if (transaction.Data != null)
        {
            var transactionData = transaction.Data.Value;
            for (int i = 0; i < _knownContracts.Length; i++)
            {
                var knownContract = _knownContracts[i];
                if (knownContract.KnownFunctions.Length == 0)
                {
                    continue;
                }

                for (var j = 0; j < knownContract.KnownFunctions.Length; j++)
                {
                    var knownFunction = knownContract.KnownFunctions[j];

                    var offsets = transactionData.FindOccurrences(knownFunction.SelectorUint32);
                    if (offsets.Length == 0)
                        continue;

                    foreach (var offset in offsets)
                    {
                        IEnumerable<IParsedCallData>? decoded;
                        try
                        {
                            decoded = knownFunction.Decoder(transactionData.ToArray(), offset);
                        }
                        catch (Exception e)
                        {
                            // log and skip
                            Console.WriteLine(
                                $"Error decoding {knownFunction.FunctionName} at offset {offset}: {e.Message}");
                            continue;
                        }

                        foreach (var e in decoded)
                        {
                            yield return Either<IIndexEvent, IParsedCallData>.FromRight(e);
                        }
                    }
                }
            }
        }

        var eventsv2 = new List<IIndexedEventV2>(events.Length);
        foreach (var e in events)
        {
            if (e is IIndexedEventV2 v2) eventsv2.Add(v2);
        }

        if (eventsv2.Count == 0)
            yield break;


        var result = TransferSummaryAggregator.AggregateAll(eventsv2, InflationaryCircles.Instances);
        int syntheticLogIndex = -(result.StreamTransfers.Totals.Count() + result.NonStreamTransfers.Totals.Count());

        if (result.StreamTransfers.Totals.Any())
        {
            var streamEventsJson = JsonSerializer.Serialize(result.StreamEvents, _jsonSerializerOptions);
            foreach (var summary in result.StreamTransfers.Totals)
            {
                yield return Either<IIndexEvent, IParsedCallData>.FromLeft(new TransferSummary(
                    block.Number,
                    (long)block.Timestamp,
                    receipt.Index,
                    syntheticLogIndex++,
                    0,
                    transaction.Hash!.ToString(true),
                    "",
                    summary.Key.From,
                    summary.Key.To,
                    (UInt256)summary.Value,
                    streamEventsJson
                ));
            }
        }

        if (result.NonStreamTransfers.Totals.Any())
        {
            var nonStreamEventsJson = JsonSerializer.Serialize(result.NonStreamEvents, _jsonSerializerOptions);
            foreach (var transfer in result.NonStreamTransfers.Totals)
            {
                yield return Either<IIndexEvent, IParsedCallData>.FromLeft(new TransferSummary(
                    block.Number,
                    (long)block.Timestamp,
                    receipt.Index,
                    syntheticLogIndex++,
                    0,
                    transaction.Hash!.ToString(true),
                    "",
                    transfer.Key.From,
                    transfer.Key.To,
                    (UInt256)transfer.Value,
                    nonStreamEventsJson
                ));
            }
        }

        if (transaction.Data == null)
        {
            yield break;
        }
    }

    private IEnumerable<IIndexEvent> Erc20WrapperDeployed(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event ERC20WrapperDeployed(address indexed avatar, address indexed erc20Wrapper, uint8 circlesType)

        // Parse addresses from topics:
        string avatar = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string erc20Wrapper = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        // Parse the single uint256 from log.Data => circlesType
        UInt256 circlesType = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        yield return new ERC20WrapperDeployed(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            avatar,
            erc20Wrapper,
            (long)circlesType
        );
    }

    private IEnumerable<ApprovalForAll> Erc1155ApprovalForAll(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event ApprovalForAll(address indexed account, address indexed operator, bool approved)

        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string operatorAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        // We'll interpret the 32 bytes of log.Data as bool => nonzero => true
        UInt256 raw = LogDataParsingHelper.ParseSingleUInt256(log.Data);
        bool approved = !raw.IsZero;

        yield return new ApprovalForAll(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account,
            operatorAddress,
            approved
        );
    }

    private IEnumerable<TransferSingle> Erc1155TransferSingle(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event TransferSingle(address indexed operator, address indexed from, address indexed to, uint256 id, uint256 value)

        string operatorAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string fromAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string toAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for TransferSingle (needs 64 bytes).");
        }

        var id = new UInt256(dataSpan.Slice(0, 32), true);
        var value = new UInt256(dataSpan.Slice(32, 32), true);

        yield return new TransferSingle(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            operatorAddress,
            fromAddress,
            toAddress,
            id,
            value
        );
    }

    private IEnumerable<TransferBatch> Erc1155TransferBatch(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event TransferBatch(address indexed operator, address indexed from, address indexed to, uint256[] ids, uint256[] values)
        string operatorAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string fromAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string toAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for TransferBatch (needs at least 64 bytes).");
        }

        int idsOffset = LogDataParsingHelper.ParseOffset(dataSpan, 0);
        int valuesOffset = LogDataParsingHelper.ParseOffset(dataSpan, 32);

        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(dataSpan, idsOffset);
        UInt256[] values = LogDataParsingHelper.ParseUInt256Array(dataSpan, valuesOffset);

        if (ids.Length != values.Length)
        {
            throw new InvalidOperationException("The number of ids and values must match in TransferBatch.");
        }

        for (int i = 0; i < ids.Length; i++)
        {
            yield return new TransferBatch(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                log.Address.ToString(true, false),
                i,
                operatorAddress,
                fromAddress,
                toAddress,
                ids[i],
                values[i]
            );
        }
    }

    private IEnumerable<RegisterOrganization> CrcV2RegisterOrganization(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event RegisterOrganization(address indexed organization, string name)
        string orgAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string orgName = LogDataStringDecoder.ReadStrings(log.Data)[0];

        yield return new RegisterOrganization(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            orgAddress,
            orgName
        );
    }

    private IEnumerable<RegisterGroup> CrcV2RegisterGroup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event RegisterGroup(address indexed group, address indexed mintPolicy, address indexed treasury, string name, string symbol)

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string mintPolicy = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string treasury = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        string[] stringData = LogDataStringDecoder.ReadStrings(log.Data);
        string groupName = stringData[0];
        string groupSymbol = stringData[1];

        yield return new RegisterGroup(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            groupAddress,
            mintPolicy,
            treasury,
            groupName,
            groupSymbol
        );
    }

    private IEnumerable<RegisterHuman> CrcV2RegisterHuman(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event RegisterHuman(address indexed human, address indexed inviter)
        string humanAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string inviterAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        yield return new RegisterHuman(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            humanAddress,
            inviterAddress
        );
    }

    private IEnumerable<PersonalMint> CrcV2PersonalMint(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event PersonalMint(address indexed to, uint256 amount, uint256 startPeriod, uint256 endPeriod)
        string toAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 96)
        {
            throw new ArgumentException("Insufficient data for PersonalMint (needs 96 bytes).");
        }

        var amount = new UInt256(dataSpan.Slice(0, 32), true);
        var startPeriod = new UInt256(dataSpan.Slice(32, 32), true);
        var endPeriod = new UInt256(dataSpan.Slice(64, 32), true);

        yield return new PersonalMint(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            toAddress,
            amount,
            startPeriod,
            endPeriod
        );
    }

    private IEnumerable<Trust> CrcV2Trust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event Trust(address indexed user, address indexed canSendTo, uint256 trustLimit)
        string userAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string canSendToAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        UInt256 limit = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        yield return new Trust(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            userAddress,
            canSendToAddress,
            limit
        );
    }

    private IEnumerable<Stopped> CrcV2Stopped(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event Stopped(address indexed who)
        string address = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        yield return new Stopped(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            address
        );
    }

    private IEnumerable<Erc20WrapperTransfer> Erc20WrapperTransfer(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event Erc20WrapperTransfer(address indexed from, address indexed to, uint256 value)

        string from = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string to = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        UInt256 amount = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        yield return new Erc20WrapperTransfer(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            log.Address.ToString(true, false),
            from,
            to,
            amount
        );
    }

    private IEnumerable<DepositInflationary> DepositInflationary(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event DepositInflationary(address indexed account, uint256 amount, uint256 demurragedAmount)
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for DepositInflationary (needs 64 bytes).");
        }

        var amount = new UInt256(dataSpan.Slice(0, 32), true);
        var demurraged = new UInt256(dataSpan.Slice(32, 32), true);

        yield return new DepositInflationary(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account,
            amount,
            demurraged
        );
    }

    private IEnumerable<WithdrawInflationary> WithdrawInflationary(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event WithdrawInflationary(address indexed account, uint256 amount, uint256 demurragedAmount)
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for WithdrawInflationary (needs 64 bytes).");
        }

        var amount = new UInt256(dataSpan.Slice(0, 32), true);
        var demurraged = new UInt256(dataSpan.Slice(32, 32), true);

        yield return new WithdrawInflationary(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account,
            amount,
            demurraged
        );
    }

    private IEnumerable<DepositDemurraged> DepositDemurraged(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event DepositDemurraged(address indexed account, uint256 amount, uint256 inflationaryAmount)
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for DepositDemurraged (needs 64 bytes).");
        }

        var amount = new UInt256(dataSpan.Slice(0, 32), true);
        var inflation = new UInt256(dataSpan.Slice(32, 32), true);

        yield return new DepositDemurraged(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account,
            amount,
            inflation
        );
    }

    private IEnumerable<WithdrawDemurraged> WithdrawDemurraged(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event WithdrawDemurraged(address indexed account, uint256 amount, uint256 inflationaryAmount)
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for WithdrawDemurraged (needs 64 bytes).");
        }

        var amount = new UInt256(dataSpan.Slice(0, 32), true);
        var inflation = new UInt256(dataSpan.Slice(32, 32), true);

        yield return new WithdrawDemurraged(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account,
            amount,
            inflation
        );
    }

    private IEnumerable<DiscountCost> DiscountCost(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event DiscountCost(address indexed account, uint256 indexed id, uint256 discountCost)
        string account = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var id = new UInt256(log.Topics[2].Bytes, true);

        UInt256 cost = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        yield return new DiscountCost(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            account,
            id,
            cost
        );
    }

    private IEnumerable<StreamCompleted> StreamCompleted(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event StreamCompleted(address indexed operator, address indexed from, address indexed to, uint256[] ids, uint256[] amounts)

        string operatorAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string fromAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string toAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for StreamCompleted (needs at least 64 bytes).");
        }

        int idsOffset = LogDataParsingHelper.ParseOffset(dataSpan, 0);
        int amountsOffset = LogDataParsingHelper.ParseOffset(dataSpan, 32);

        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(dataSpan, idsOffset);
        UInt256[] amounts = LogDataParsingHelper.ParseUInt256Array(dataSpan, amountsOffset);

        if (ids.Length != amounts.Length)
        {
            throw new InvalidOperationException("The number of ids and amounts must match in StreamCompleted.");
        }

        for (int i = 0; i < ids.Length; i++)
        {
            yield return new StreamCompleted(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                i,
                receipt.TxHash!.ToString(),
                log.Address.ToString(true, false),
                operatorAddress,
                fromAddress,
                toAddress,
                ids[i],
                amounts[i]
            );
        }
    }

    private IEnumerable<GroupMint> GroupMint(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event GroupMint(address indexed sender, address indexed receiver, address indexed group, uint256[] collateral, uint256[] amounts);

        string sender = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string receiver = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string group = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);

        var dataSpan = log.Data.AsSpan();
        if (dataSpan.Length < 64)
        {
            throw new ArgumentException("Insufficient data for GroupMint (needs at least 64 bytes).");
        }

        int collateralOffset = LogDataParsingHelper.ParseOffset(dataSpan, 0);
        int amountsOffset = LogDataParsingHelper.ParseOffset(dataSpan, 32);

        UInt256[] collateral = LogDataParsingHelper.ParseUInt256Array(dataSpan, collateralOffset);
        UInt256[] amounts = LogDataParsingHelper.ParseUInt256Array(dataSpan, amountsOffset);

        if (collateral.Length != amounts.Length)
        {
            throw new InvalidOperationException("The number of collateral and amounts must match in GroupMint.");
        }

        for (int i = 0; i < collateral.Length; i++)
        {
            yield return new GroupMint(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                i,
                receipt.TxHash!.ToString(),
                log.Address.ToString(true, false),
                sender,
                receiver,
                group,
                collateral[i],
                amounts[i]
            );
        }
    }

    private IEnumerable<FlowEdgesScopeSingleStarted> FlowEdgesScopeSingleStarted(
        Block block,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        // event FlowEdgesScopeSingleStarted(uint256 indexed flowEdgeId, uint16 streamId);
        UInt256 flowEdgeId = new UInt256(log.Topics[1].Bytes, true);
        UInt256 streamId = LogDataParsingHelper.ParseSingleUInt256(log.Data);

        yield return new FlowEdgesScopeSingleStarted(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            flowEdgeId,
            (ushort)streamId
        );
    }

    private IEnumerable<FlowEdgesScopeLastEnded> FlowEdgesScopeLastEnded(
        Block block,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        // event FlowEdgesScopeLastEnded();
        yield return new FlowEdgesScopeLastEnded(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false)
        );
    }

    // event CMGroupCreated(address indexed proxy, address indexed owner, address indexed mintHandler, address redemptionHandler);
    private IEnumerable<CMGroupCreated> CMGroupCreated(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string proxy = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string owner = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);
        string mintHandler = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[3].Bytes);
        string redemptionHandler = new Address(log.Data.Slice(12, 20)).ToString(true, false);
        string liquidityProvider =
            log.Data.Length == 64 ? new Address(log.Data.Slice(44, 20)).ToString(true, false) : "";

        yield return new CMGroupCreated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            proxy,
            owner,
            mintHandler,
            redemptionHandler,
            liquidityProvider
        );
    }

    // event CirclesBackingDeployed(address indexed backer, address indexed circlesBackingInstance);
    private IEnumerable<CirclesBackingDeployed> CirclesBackingDeployed(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        var backer = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var circlesBackingInstance = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        yield return new CirclesBackingDeployed(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            backer,
            circlesBackingInstance
        );
    }

    // event LBPDeployed(address indexed circlesBackingInstance, address indexed lbp);
    private IEnumerable<LbpDeployed> LbpDeployed(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var circlesBackingInstance = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var lbp = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        yield return new LbpDeployed(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            circlesBackingInstance,
            lbp
        );
    }

    // event CirclesBackingInitiated(address indexed backer, address indexed circlesBackingInstance, address backingAsset, address personalCirclesAddress);
    private IEnumerable<CirclesBackingInitiated> CirclesBackingInitiated(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        var backer = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var circlesBackingInstance = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        var data = log.Data;
        var backingAssetBytes = data[0..32];
        var personalCirclesAddressBytes = data[32..64];

        var backingAsset = "0x" + BitConverter
            .ToString(backingAssetBytes[12..32])
            .Replace("-", "")
            .ToLowerInvariant();

        var personalCirclesAddress = "0x" + BitConverter
            .ToString(personalCirclesAddressBytes[12..32])
            .Replace("-", "")
            .ToLowerInvariant();

        yield return new CirclesBackingInitiated(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            backer,
            circlesBackingInstance,
            backingAsset,
            personalCirclesAddress
        );
    }

    // event CirclesBackingCompleted(address indexed backer, address indexed circlesBackingInstance, address lbp);
    private IEnumerable<CirclesBackingCompleted> CirclesBackingCompleted(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        var backer = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var circlesBackingInstance = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var lbp = log.Topics.Length > 3
            ? "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength)
            : new Address(log.Data.Slice(12)).ToString(true, false);

        yield return new CirclesBackingCompleted(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            backer,
            circlesBackingInstance,
            lbp
        );
    }

    private IEnumerable<Released> Released(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        var backer = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var circlesBackingInstance = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        var lbp = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        yield return new Released(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            backer,
            circlesBackingInstance,
            lbp
        );
    }

    private IEnumerable<UpdateMetadataDigest> UpdateMetadataDigest(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event UpdateMetadataDigest(address indexed avatar, bytes32 metadataDigest)
        string avatar = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        byte[] metadataDigest = log.Data;

        yield return new UpdateMetadataDigest(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            avatar,
            metadataDigest);
    }

    private IEnumerable<RegisterShortName> RegisterShortName(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event RegisterShortName(address indexed avatar, uint72 shortName, uint256 nonce)
        string avatar = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        UInt256 shortName = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 nonce = new UInt256(log.Data.Slice(32, 32), true);

        yield return new RegisterShortName(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            avatar,
            shortName,
            nonce);
    }

    private IEnumerable<CidV0> CidV0(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string avatar = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        yield return new CidV0(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            avatar,
            log.Data);
    }

    private IEnumerable<CreateVault> CreateVault(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event CreateVault(address indexed group, address indexed vault);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string vaultAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        yield return new CreateVault(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            groupAddress,
            vaultAddress
        );
    }

    private IEnumerable<CollateralLockedSingle> CollateralLockedSingle(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event CollateralLockedSingle(address indexed group, uint256 indexed id, uint256 value, bytes userData);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        UInt256 id = new UInt256(log.Topics[2].Bytes, true);

        // Non-indexed params are in log.Data
        //  - first 32 bytes => value
        //  - remainder => userData
        UInt256 value = new UInt256(log.Data.Slice(0, 32), true);
        byte[] userData = log.Data.Slice(32).ToArray();

        yield return new CollateralLockedSingle(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            groupAddress,
            id,
            value,
            userData
        );
    }

    private IEnumerable<CollateralLockedBatch> CollateralLockedBatch(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        // event CollateralLockedBatch(address indexed group, uint256[] ids, uint256[] values, bytes userData);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        var data = log.Data;

        // We expect at least 3 offsets (one each for ids, values, userData):
        //  - [0..31] => offset to ids
        //  - [32..63] => offset to values
        //  - [64..95] => offset to userData
        if (data.Length < 96)
        {
            throw new ArgumentException("Log data is too short to contain offsets");
        }

        // Parse each offset
        int idsOffset = LogDataParsingHelper.ParseOffset(data, 0);
        int valuesOffset = LogDataParsingHelper.ParseOffset(data, 32);
        int userDataOffset = LogDataParsingHelper.ParseOffset(data, 64);

        if (idsOffset < 0 || valuesOffset < 0 || userDataOffset < 0)
        {
            throw new ArgumentException("Failed to parse offsets");
        }

        // Parse arrays/bytes from each offset
        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(data, idsOffset);
        UInt256[] values = LogDataParsingHelper.ParseUInt256Array(data, valuesOffset);
        byte[] userDataBytes = LogDataParsingHelper.ParseBytes(data, userDataOffset);

        // Typically, you can yield a single event that has all arrays,
        // but your existing pattern yields one event per (id, value).
        for (int i = 0; i < ids.Length; i++)
        {
            // If there are fewer values than ids, default to zero
            var val = i < values.Length ? values[i] : UInt256.Zero;

            yield return new CollateralLockedBatch(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                log.Address.ToString(true, false),
                i,
                groupAddress,
                ids[i],
                val,
                userDataBytes
            );
        }
    }

    private IEnumerable<GroupRedeem> GroupRedeem(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // event GroupRedeem(address indexed group, uint256 indexed id, uint256 value, bytes data);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        UInt256 id = new UInt256(log.Topics[2].Bytes, true);

        UInt256 value = new UInt256(log.Data.Slice(0, 32), true);
        byte[] dataBytes = log.Data.Slice(32).ToArray();

        yield return new GroupRedeem(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            0,
            receipt.TxHash!.ToString(),
            log.Address.ToString(true, false),
            groupAddress,
            id,
            value,
            dataBytes
        );
    }

    private IEnumerable<GroupRedeemCollateralReturn> GroupRedeemCollateralReturn(
        Block block,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        // event GroupRedeemCollateralReturn(address indexed group, address indexed to, uint256[] ids, uint256[] values);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);
        string toAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[2].Bytes);

        var data = log.Data;
        if (data.Length < 64)
        {
            throw new ArgumentException("Log data is too short to contain offsets");
        }

        // offsets to ids and values
        int idsOffset = LogDataParsingHelper.ParseOffset(data, 0);
        int valuesOffset = LogDataParsingHelper.ParseOffset(data, 32);

        if (idsOffset < 0 || valuesOffset < 0)
        {
            throw new ArgumentException("Failed to parse offsets");
        }

        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(data, idsOffset);
        UInt256[] values = LogDataParsingHelper.ParseUInt256Array(data, valuesOffset);

        for (int i = 0; i < ids.Length; i++)
        {
            var val = i < values.Length ? values[i] : UInt256.Zero;

            yield return new GroupRedeemCollateralReturn(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                log.Address.ToString(true, false),
                i,
                groupAddress,
                toAddress,
                ids[i],
                val
            );
        }
    }

    private IEnumerable<GroupRedeemCollateralBurn> GroupRedeemCollateralBurn(
        Block block,
        TxReceipt receipt,
        LogEntry log,
        int logIndex)
    {
        // event GroupRedeemCollateralBurn(address indexed group, uint256[] ids, uint256[] values);

        string groupAddress = LogDataParsingHelper.ParseAddressFromTopic(log.Topics[1].Bytes);

        var data = log.Data;
        if (data.Length < 64)
        {
            yield break;
        }

        int idsOffset = LogDataParsingHelper.ParseOffset(data, 0);
        int valuesOffset = LogDataParsingHelper.ParseOffset(data, 32);

        if (idsOffset < 0 || valuesOffset < 0)
        {
            yield break;
        }

        UInt256[] ids = LogDataParsingHelper.ParseUInt256Array(data, idsOffset);
        UInt256[] values = LogDataParsingHelper.ParseUInt256Array(data, valuesOffset);

        for (int i = 0; i < ids.Length; i++)
        {
            var val = i < values.Length ? values[i] : UInt256.Zero;

            yield return new GroupRedeemCollateralBurn(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                log.Address.ToString(true, false),
                i,
                groupAddress,
                ids[i],
                val
            );
        }
    }

    private static byte[] DecodeBytes(byte[] fullData, int baseOffset, int dynamicOffset)
    {
        // dynamicOffset is relative to (baseOffset + 4) from standard solidity ABI
        // so the actual start is: (baseOffset + 4 + dynamicOffset)
        int start = baseOffset + 4 + dynamicOffset;
        if (start + 32 > fullData.Length)
            return Array.Empty<byte>();

        // first 32 bytes = length
        ReadOnlySpan<byte> lengthWord = fullData.AsSpan(start, 32);
        int lengthVal = (int)new BigInteger(lengthWord, isBigEndian: true);

        int dataStart = start + 32;
        if (dataStart + lengthVal > fullData.Length)
            return Array.Empty<byte>();

        return fullData[dataStart..(dataStart + lengthVal)];
    }

    private static UInt256[] DecodeUint256Array(byte[] fullData, int baseOffset, int dynamicOffset)
    {
        if (dynamicOffset == 0)
            return Array.Empty<UInt256>();

        int start = baseOffset + 4 + dynamicOffset;
        if (start + 32 > fullData.Length)
            return Array.Empty<UInt256>();

        // array length
        ReadOnlySpan<byte> lengthWord = fullData.AsSpan(start, 32);
        int arrayLen = (int)new BigInteger(lengthWord, isBigEndian: true);

        var result = new UInt256[arrayLen];
        int elementsStart = start + 32;
        for (int i = 0; i < arrayLen; i++)
        {
            int pos = elementsStart + i * 32;
            if (pos + 32 > fullData.Length) break;

            result[i] = new UInt256(fullData.AsSpan(pos, 32), true);
        }

        return result;
    }

    // For address[] in operateFlowMatrix:
    private static string[] DecodeAddressArray(byte[] fullData, int baseOffset, BigInteger offsetVal)
    {
        if (offsetVal == 0)
            return Array.Empty<string>();

        int start = (int)(baseOffset + 4 + offsetVal);
        if (start + 32 > fullData.Length)
            return Array.Empty<string>();

        int lengthVal = (int)new BigInteger(fullData.AsSpan(start, 32), isBigEndian: true);
        string[] addresses = new string[lengthVal];

        int elementsStart = start + 32;
        for (int i = 0; i < lengthVal; i++)
        {
            int pos = elementsStart + i * 32;
            if (pos + 32 > fullData.Length) break;

            var word32 = fullData.AsSpan(pos, 32);
            var last20 = word32[^20..];
            addresses[i] = "0x" + BitConverter.ToString(last20.ToArray()).Replace("-", "").ToLower();
        }

        return addresses;
    }

    // For (uint16,uint192)[] flow edges:
    private static FlowEdge[] DecodeFlowEdgesArray(byte[] fullData, int baseOffset, BigInteger offsetVal)
    {
        if (offsetVal == 0)
            return Array.Empty<FlowEdge>();

        int start = (int)(baseOffset + 4 + offsetVal);
        if (start + 32 > fullData.Length)
            return Array.Empty<FlowEdge>();

        int lengthVal = (int)new BigInteger(fullData.AsSpan(start, 32), isBigEndian: true);
        var result = new FlowEdge[lengthVal];

        // each element is 2 * 32 bytes => 64 bytes
        int elementsStart = start + 32;
        for (int i = 0; i < lengthVal; i++)
        {
            int pos = elementsStart + i * 64;
            if (pos + 64 > fullData.Length) break;

            var word0 = fullData.AsSpan(pos, 32);
            var word1 = fullData.AsSpan(pos + 32, 32);

            ushort sinkId = (ushort)(new BigInteger(word0, isBigEndian: true) & 0xFFFF);
            var amount = new UInt256(word1, true);

            result[i] = new FlowEdge(sinkId, amount);
        }

        return result;
    }

    // For (uint16 sourceCoordinate, uint16[] flowEdgeIds, bytes data)[] streams:
    private static StreamStruct[] DecodeStreamsArray(byte[] fullData, int baseOffset, BigInteger offsetVal)
    {
        if (offsetVal == 0)
            return Array.Empty<StreamStruct>();

        int start = (int)(baseOffset + 4 + offsetVal);
        if (start + 32 > fullData.Length)
            return Array.Empty<StreamStruct>();

        int lengthVal = (int)new BigInteger(fullData.AsSpan(start, 32), isBigEndian: true);
        var result = new StreamStruct[lengthVal];

        // each stream is 3 * 32 bytes = 96
        int elementsStart = start + 32;
        for (int i = 0; i < lengthVal; i++)
        {
            int pos = elementsStart + i * 96;
            if (pos + 96 > fullData.Length) break;

            // field0 => sourceCoordinate (uint16 in bottom of a 32-byte word)
            var field0 = fullData.AsSpan(pos, 32);
            ushort sourceCoord = (ushort)(new BigInteger(field0, isBigEndian: true) & 0xFFFF);

            // field1 => offset for flowEdgeIds
            var field1 = fullData.AsSpan(pos + 32, 32);
            int offsetFlowEdgeIds = (int)new BigInteger(field1, isBigEndian: true);

            // field2 => offset for data
            var field2 = fullData.AsSpan(pos + 64, 32);
            int offsetData = (int)new BigInteger(field2, isBigEndian: true);

            // decode the array of uint16
            ushort[] flowEdgeIds = DecodeUint16Array(fullData, baseOffset, offsetFlowEdgeIds);
            // decode the dynamic bytes
            byte[] dataBytes = DecodeBytes(fullData, baseOffset, offsetData);

            result[i] = new StreamStruct(sourceCoord, flowEdgeIds, dataBytes);
        }

        return result;
    }

    private static ushort[] DecodeUint16Array(byte[] fullData, int baseOffset, int offsetVal)
    {
        if (offsetVal == 0) return Array.Empty<ushort>();

        int start = baseOffset + 4 + offsetVal;
        if (start + 32 > fullData.Length) return Array.Empty<ushort>();

        int lengthVal = (int)new BigInteger(fullData.AsSpan(start, 32), isBigEndian: true);
        var array = new ushort[lengthVal];

        int elementsStart = start + 32;
        for (int i = 0; i < lengthVal; i++)
        {
            int pos = elementsStart + i * 32;
            if (pos + 32 > fullData.Length) break;

            var word = new BigInteger(fullData.AsSpan(pos, 32), isBigEndian: true);
            array[i] = (ushort)(word & 0xFFFF);
        }

        return array;
    }
}