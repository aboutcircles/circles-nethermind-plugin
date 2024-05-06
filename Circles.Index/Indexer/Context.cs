using Circles.Index.Common;
using Nethermind.Logging;

namespace Circles.Index.Indexer;

public record Context(
    ILogger Logger,
    Settings Settings,
    IDatabase Database);