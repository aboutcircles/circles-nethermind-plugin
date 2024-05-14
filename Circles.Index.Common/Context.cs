using Nethermind.Api;
using Nethermind.Logging;

namespace Circles.Index.Common;

public record Context(
    INethermindApi NethermindApi,
    ILogger Logger,
    Settings Settings,
    IDatabase Database,
    ILogParser[] LogParsers,
    Sink Sink);