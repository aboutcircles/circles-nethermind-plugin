using Nethermind.Logging;

namespace Circles.Index.Utils;

public class LoggerWithPrefix(string prefix, ILogger logger) : ILogger
{
    public void Info(string text)
    {
        logger.Info($"{prefix} {text}");
    }

    public void Warn(string text)
    {
        logger.Warn($"{prefix} {text}");
    }

    public void Debug(string text)
    {
        logger.Debug($"{prefix} {text}");
    }

    public void Trace(string text)
    {
        logger.Trace($"{prefix} {text}");
    }

    public void Error(string text, Exception ex)
    {
        logger.Error($"{prefix} {text}", ex);
    }

    public bool IsInfo { get; }
    public bool IsWarn { get; }
    public bool IsDebug { get; }
    public bool IsTrace { get; }
    public bool IsError { get; }
}
