using System.Collections.Concurrent;
using Nethermind.Core;

namespace Circles.Index.Indexer;

public static class Caches
{
    public static readonly ConcurrentDictionary<Address, object?> CirclesTokenAddresses = new();
    public static readonly ConcurrentDictionary<long, object?> KnownBlocks = new();

    public static long MaxKnownBlock
    {
        get;
        private set;
    }

    public static void Init()
    {
        // TODO: Load only for a specific chain
        // var path = Path.Combine(Path.GetDirectoryName(typeof(Caches).Assembly.Location)!, "circles_index_known_blocks.csv");
        // using var reader = File.OpenText(path);
        // do
        // {
        //     var line = reader.ReadLine();
        //     if (line == null)
        //     {
        //         break;
        //     }
        //     var knownBlock = long.Parse(line, CultureInfo.InvariantCulture);
        //     if (knownBlock > MaxKnownBlock)
        //     {
        //         MaxKnownBlock = knownBlock;
        //     }
        //     KnownBlocks.TryAdd(knownBlock, null);
        // } while (true);
        //
        // Console.WriteLine($"Loaded {KnownBlocks.Count} known blocks");
    }
}