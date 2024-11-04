using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using Circles.Index.Common;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2;

public class LogParser(Address v2HubAddress, Address erc20LiftAddress) : ILogParser
{
    private readonly Hash256 _stoppedTopic = new(DatabaseSchema.Stopped.Topic);

    private readonly Hash256 _trustTopic = new(DatabaseSchema.Trust.Topic);

    // private readonly Hash256 _inviteHumanTopic = new(DatabaseSchema.InviteHuman.Topic);
    private readonly Hash256 _personalMintTopic = new(DatabaseSchema.PersonalMint.Topic);
    private readonly Hash256 _registerHumanTopic = new(DatabaseSchema.RegisterHuman.Topic);
    private readonly Hash256 _registerGroupTopic = new(DatabaseSchema.RegisterGroup.Topic);
    private readonly Hash256 _registerOrganizationTopic = new(DatabaseSchema.RegisterOrganization.Topic);
    private readonly Hash256 _transferBatchTopic = new(DatabaseSchema.TransferBatch.Topic);
    private readonly Hash256 _transferSingleTopic = new(DatabaseSchema.TransferSingle.Topic);
    private readonly Hash256 _approvalForAllTopic = new(DatabaseSchema.ApprovalForAll.Topic);
    private readonly Hash256 _uriTopic = new(DatabaseSchema.URI.Topic);
    private readonly Hash256 _erc20WrapperDeployed = new(DatabaseSchema.ERC20WrapperDeployed.Topic);
    private readonly Hash256 _erc20WrapperTransfer = new(DatabaseSchema.Erc20WrapperTransfer.Topic);
    private readonly Hash256 _depositInflationary = new(DatabaseSchema.DepositInflationary.Topic);
    private readonly Hash256 _withdrawInflationary = new(DatabaseSchema.WithdrawInflationary.Topic);
    private readonly Hash256 _depositDemurraged = new(DatabaseSchema.DepositDemurraged.Topic);
    private readonly Hash256 _withdrawDemurraged = new(DatabaseSchema.WithdrawDemurraged.Topic);
    private readonly Hash256 _streamCompletedTopic = new(DatabaseSchema.StreamCompleted.Topic);
    private readonly Hash256 _discountCostTopic = new(DatabaseSchema.DiscountCost.Topic);

    public static readonly ConcurrentDictionary<Address, object?> Erc20WrapperAddresses = new();

    private readonly byte[] _registerHumanFunctionSignature =
        Keccak.Compute("registerHuman(address,bytes32)").Bytes[..4].ToArray();

    public IEnumerable<IIndexEvent> ParseTransaction(Block block, int transactionIndex, Transaction transaction)
    {
        if (transaction.To != v2HubAddress)
        {
            yield break;
        }

        if (transaction.Data == null || transaction.Data.Value.Length < 68)
        {
            // 68 is the size of a complete registerHuman call with arguments
            yield break;
        }

        // Parse the whole call data for a `registerHuman` call to get the inviter address and
        // create a InviteHuman event from it.
        // TODO: This is only for v0.3.6 and will be replace with an additional parameter on the InviteHuman event in the next version
        // Because we cannot know how the contract was called (e.g. via a safe), we simply search for the function signature.
        int callLength = 4;
        for (int i = 0; i < transaction.Data.Value.Length - callLength; i++)
        {
            if (!transaction.Data.Value[i..(i + 4)].Span.SequenceEqual(_registerHumanFunctionSignature))
            {
                continue;
            }

            // The next 12 bytes must be zero padding.
            if (!transaction.Data.Value[(i + callLength)..(i + callLength + 12)].Span.SequenceEqual(new byte[12]))
            {
                continue;
            }

            // Extract the bytes for the following call signature:
            // function registerHuman(address _inviter, bytes32 _metadataDigest) external
            var inviterAddressOffset = i + callLength;
            var inviterAddressWithoutPaddingOffset = inviterAddressOffset + 12;
            var inviterAddress = new Address(transaction.Data.Value[inviterAddressWithoutPaddingOffset..
                (inviterAddressWithoutPaddingOffset + 20)].ToArray());

            // TODO: Usually the sender is the invitee, but this will fail e.g. in case of relayers
            var invitee = transaction.SenderAddress!;

            // Console.WriteLine(
            //     $"Found `InviteHuman` call in tx {transaction.Hash}. Inviter: {inviterAddress}, Invitee: {invitee}");

            if (inviterAddress == Address.Zero)
            {
                break;
            }

            yield return new InviteHuman(block.Number, (long)block.Timestamp, transactionIndex, -1,
                transaction.Hash!.ToString(),
                inviterAddress.ToString(), invitee.ToString());

            break;
        }
    }

    public IEnumerable<IIndexEvent> ParseLog(Block block, Transaction transaction, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        if (log.Topics.Length == 0)
        {
            yield break;
        }

        var topic = log.Topics[0];

        if (log.LoggersAddress == v2HubAddress)
        {
            if (topic == _stoppedTopic)
            {
                yield return CrcV2Stopped(block, receipt, log, logIndex);
            }

            if (topic == _trustTopic)
            {
                yield return CrcV2Trust(block, receipt, log, logIndex);
            }

            if (topic == _personalMintTopic)
            {
                yield return CrcV2PersonalMint(block, receipt, log, logIndex);
            }

            if (topic == _registerHumanTopic)
            {
                yield return CrcV2RegisterHuman(block, receipt, log, logIndex);
            }

            if (topic == _registerGroupTopic)
            {
                yield return CrcV2RegisterGroup(block, receipt, log, logIndex);
            }

            if (topic == _registerOrganizationTopic)
            {
                yield return CrcV2RegisterOrganization(block, receipt, log, logIndex);
            }

            if (topic == _transferBatchTopic)
            {
                foreach (var batchEvent in Erc1155TransferBatch(block, receipt, log, logIndex))
                {
                    yield return batchEvent;
                }
            }

            if (topic == _transferSingleTopic)
            {
                yield return Erc1155TransferSingle(block, receipt, log, logIndex);
            }

            if (topic == _approvalForAllTopic)
            {
                yield return Erc1155ApprovalForAll(block, receipt, log, logIndex);
            }

            if (topic == _streamCompletedTopic)
            {
                foreach (var streamCompleted in StreamCompleted(block, receipt, log, logIndex))
                {
                    yield return streamCompleted;
                }
            }

            if (topic == _discountCostTopic)
            {
                yield return DiscountCost(block, receipt, log, logIndex);
            }
        }

        if (log.LoggersAddress == erc20LiftAddress)
        {
            if (topic == _erc20WrapperDeployed)
            {
                yield return Erc20WrapperDeployed(block, receipt, log, logIndex);
            }
        }

        if (Erc20WrapperAddresses.ContainsKey(log.LoggersAddress))
        {
            if (topic == _erc20WrapperTransfer)
            {
                yield return Erc20WrapperTransfer(block, receipt, log, logIndex);
            }

            if (topic == _depositDemurraged)
            {
                yield return DepositDemurraged(block, receipt, log, logIndex);
            }

            if (topic == _withdrawDemurraged)
            {
                yield return WithdrawDemurraged(block, receipt, log, logIndex);
            }

            if (topic == _depositInflationary)
            {
                yield return DepositInflationary(block, receipt, log, logIndex);
            }

            if (topic == _withdrawInflationary)
            {
                yield return WithdrawInflationary(block, receipt, log, logIndex);
            }
        }
    }

    private IIndexEvent Erc20WrapperDeployed(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        // "event ERC20WrapperDeployed(address indexed avatar, address indexed erc20Wrapper, uint8 circlesType)"
        string avatar = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string erc20Wrapper = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 circlesType = new UInt256(log.Data, true);

        Erc20WrapperAddresses.TryAdd(new Address(erc20Wrapper), null);

        return new ERC20WrapperDeployed(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            avatar,
            erc20Wrapper,
            (long)(BigInteger)circlesType);
    }

    private ApprovalForAll Erc1155ApprovalForAll(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string account = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string operatorAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        bool approved = new BigInteger(log.Data) == 1;

        return new ApprovalForAll(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            account,
            operatorAddress,
            approved);
    }

    private TransferSingle Erc1155TransferSingle(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string operatorAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string fromAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string toAddress = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 id = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 value = new UInt256(log.Data.Slice(32), true);

        return new TransferSingle(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            operatorAddress,
            fromAddress,
            toAddress,
            id,
            value);
    }

    private IEnumerable<TransferBatch> Erc1155TransferBatch(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string operatorAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string fromAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string toAddress = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        int offset = 32;
        int batchSize = (int)new BigInteger(log.Data.Slice(0, 32).ToArray());
        for (int i = 0; i < batchSize; i++)
        {
            UInt256 batchId = new UInt256(log.Data.Slice(offset, 32), true);
            UInt256 batchValue = new UInt256(log.Data.Slice(offset + 32, 32), true);
            offset += 64;

            yield return new TransferBatch(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                receipt.TxHash!.ToString(),
                i,
                operatorAddress,
                fromAddress,
                toAddress,
                batchId,
                batchValue);
        }
    }

    private RegisterOrganization CrcV2RegisterOrganization(Block block, TxReceipt receipt, LogEntry log,
        int logIndex)
    {
        string orgAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string orgName = LogDataStringDecoder.ReadStrings(log.Data)[0];

        return new RegisterOrganization(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            orgAddress,
            orgName);
    }

    private RegisterGroup CrcV2RegisterGroup(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string groupAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string mintPolicy = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string treasury = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        string[] stringData = LogDataStringDecoder.ReadStrings(log.Data);
        string groupName = stringData[0];
        string groupSymbol = stringData[1];

        return new RegisterGroup(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            groupAddress,
            mintPolicy,
            treasury,
            groupName,
            groupSymbol);
    }


    private RegisterHuman CrcV2RegisterHuman(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string humanAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string inviterAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new RegisterHuman(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            humanAddress,
            inviterAddress);
    }

    private PersonalMint CrcV2PersonalMint(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string toAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 startPeriod = new UInt256(log.Data.Slice(32, 32), true);
        UInt256 endPeriod = new UInt256(log.Data.Slice(64), true);

        return new PersonalMint(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            toAddress,
            amount,
            startPeriod,
            endPeriod);
    }

    private Trust CrcV2Trust(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string userAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string canSendToAddress =
            "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 limit = new UInt256(log.Data, true);

        return new Trust(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            userAddress,
            canSendToAddress,
            limit);
    }

    private Stopped CrcV2Stopped(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string address = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        return new Stopped(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            address);
    }

    private Erc20WrapperTransfer Erc20WrapperTransfer(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string from = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string to = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new(log.Data, true);

        return new Erc20WrapperTransfer(
            block.Number
            , (long)block.Timestamp
            , receipt.Index
            , logIndex
            , receipt.TxHash!.ToString()
            , log.LoggersAddress.ToString(true, false)
            , from
            , to
            , amount);
    }

    private DepositInflationary DepositInflationary(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string account = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 demurragedAmount = new UInt256(log.Data.Slice(32), true);

        return new DepositInflationary(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            account,
            amount,
            demurragedAmount);
    }

    private WithdrawInflationary WithdrawInflationary(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string account = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 demurragedAmount = new UInt256(log.Data.Slice(32), true);

        return new WithdrawInflationary(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            account,
            amount,
            demurragedAmount);
    }

    private DepositDemurraged DepositDemurraged(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string account = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 inflationaryAmount = new UInt256(log.Data.Slice(32), true);

        return new DepositDemurraged(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            account,
            amount,
            inflationaryAmount);
    }

    private WithdrawDemurraged WithdrawDemurraged(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string account = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 amount = new UInt256(log.Data.Slice(0, 32), true);
        UInt256 inflationaryAmount = new UInt256(log.Data.Slice(32), true);

        return new WithdrawDemurraged(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            account,
            amount,
            inflationaryAmount);
    }

    private DiscountCost DiscountCost(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string account = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        UInt256 id = new UInt256(log.Topics[2].Bytes, true);
        UInt256 discountCost = new UInt256(log.Data, true);

        return new DiscountCost(
            block.Number,
            (long)block.Timestamp,
            receipt.Index,
            logIndex,
            receipt.TxHash!.ToString(),
            account,
            id,
            discountCost);
    }

    private IEnumerable<StreamCompleted> StreamCompleted(Block block, TxReceipt receipt, LogEntry log, int logIndex)
    {
        string operatorAddress = "0x" + log.Topics[1].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string fromAddress = "0x" + log.Topics[2].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);
        string toAddress = "0x" + log.Topics[3].ToString().Substring(Consts.AddressEmptyBytesPrefixLength);

        // Decode the data section containing the dynamic arrays
        var data = log.Data;
        int idsOffset = (int)new BigInteger(data.Slice(0, 32).ToArray(), true, true);
        int amountsOffset = (int)new BigInteger(data.Slice(32, 32).ToArray(), true, true);
        var ids = DecodeUInt256Array(data.Slice(idsOffset));
        var amounts = DecodeUInt256Array(data.Slice(amountsOffset));

        if (ids.Count != amounts.Count)
        {
            throw new InvalidOperationException("The number of ids and amounts must be equal.");
        }

        for (int i = 0; i < amounts.Count; i++)
        {
            // Create the event instance
            yield return new StreamCompleted(
                block.Number,
                (long)block.Timestamp,
                receipt.Index,
                logIndex,
                i,
                receipt.TxHash!.ToString(),
                operatorAddress,
                fromAddress,
                toAddress,
                ids[i],
                amounts[i]
            );
        }
    }

    private static List<UInt256> DecodeUInt256Array(ReadOnlyMemory<byte> data)
    {
        List<UInt256> result = new List<UInt256>();
        int length = (int)new BigInteger(data.Slice(0, 32).ToArray(), true, true); // First 32 bytes are the length
        for (int i = 0; i < length; i++)
        {
            UInt256 value = new UInt256(data.Slice(32 + i * 32, 32).ToArray(), true); // Each element is 32 bytes
            result.Add(value);
        }

        return result;
    }
}