using Nethermind.Api;
using Nethermind.Logging;

namespace Circles.Index.Common;

public record Context(
    INethermindApi NethermindApi,
    InterfaceLogger Logger,
    Settings Settings,
    IDatabase Database,
    IReadonlyDatabase ReadonlyDatabase,
    ILogParser[] LogParsers,
    Sink Sink);