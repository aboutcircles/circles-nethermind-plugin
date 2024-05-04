using Nethermind.Logging;

namespace Circles.Index.Indexer;

public record Context(
    ILogger Logger,
    Settings Settings);