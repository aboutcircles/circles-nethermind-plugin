using Circles.Index.Common;
using Nethermind.Api;
using Nethermind.Logging;

namespace Circles.Index.Indexer;

public record Context(
    INethermindApi NethermindApi,
    ILogger Logger,
    Settings Settings,
    IDatabase Database);