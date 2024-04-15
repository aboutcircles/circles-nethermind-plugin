using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Circles.Index.Indexer;

public record Context(
    string IndexDbLocation,
    ILogger Logger,
    ChainSpec ChainSpec,
    Settings Settings)
{
}