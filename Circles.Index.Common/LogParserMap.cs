using System.Collections.Immutable;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Circles.Index.Common;

public delegate IEnumerable<IIndexEvent> ParseLogDelegate(
    Block block,
    TxReceipt receipt,
    LogEntry log,
    int logIndex);

public delegate IEnumerable<IIndexEvent> ParseCallDataDelegate(
    Block block,
    Transaction transaction,
    TxReceipt receipt,
    IIndexEvent[] events);

public interface IParsedCallData
{
    string FunctionName { get; }
}

public record KnownFunction(
    string FunctionName,
    byte[] Selector, // 4-byte selector
    Func<byte[], int, IEnumerable<IParsedCallData>> Decoder
)
{
    public uint SelectorUint32 => BitConverter.ToUInt32(Selector);
}

public static class IndexedEventEqualityComparer
{
    public static IEqualityComparer<IIndexEvent> Instance { get; } = new DelegateEqualityComparer<IIndexEvent>(
        (a, b) => a.TransactionHash == b.TransactionHash
                  && a.LogIndex == b.LogIndex
                  && a.BatchIndex == b.BatchIndex,
        obj => obj.TransactionHash.GetHashCode() ^ obj.LogIndex.GetHashCode());
}

public class KnownContract(
    string nameSpace,
    string name,
    IEnumerable<Address>? addresses = null,
    IEnumerable<(Hash256 Topic, ParseLogDelegate Parser)>? logParsers = null,
    IEnumerable<KnownFunction>? callDataParsers = null)
{
    private ImmutableDictionary<Hash256, ParseLogDelegate> _logParsers =
        logParsers?.ToImmutableDictionary(x => x.Topic, x => x.Parser)
        ?? ImmutableDictionary<Hash256, ParseLogDelegate>.Empty;

    private ImmutableHashSet<Address> _instances = addresses?.ToImmutableHashSet()
                                                   ?? ImmutableHashSet<Address>.Empty;

    private ImmutableArray<KnownFunction> _knownFunctions =
        callDataParsers?.ToImmutableArray()
        ?? ImmutableArray<KnownFunction>.Empty;

    public ImmutableArray<KnownFunction> KnownFunctions => _knownFunctions;

    public ISet<Address> Instances => _instances;

    public bool IsKnownAddress(Address address)
    {
        return _instances.Contains(address);
    }

    public bool TryGetParser(Hash256 topic, out ParseLogDelegate? parser)
    {
        return _logParsers.TryGetValue(topic, out parser);
    }

    public void AddInstances(IEnumerable<Address> addresses)
    {
        _instances = _instances.Union(addresses);
    }

    public void AddLogParser(Hash256 topic, ParseLogDelegate parser)
    {
        _logParsers = _logParsers.SetItem(topic, parser);
    }

    public void AddCallDataParser(KnownFunction parser)
    {
        _knownFunctions = _knownFunctions.Add(parser);
    }
}