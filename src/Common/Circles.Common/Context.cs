using Nethermind.Api;
using Nethermind.Logging;

namespace Circles.Common;

public record Context(
    INethermindApi NethermindApi,
    InterfaceLogger Logger,
    Settings Settings,
    IDatabase Database,
    IReadonlyDatabase ReadonlyDatabase,
    ILogParser[] LogParsers,
    Sink Sink);
