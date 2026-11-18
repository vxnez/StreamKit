namespace StreamKit.Core.Logging;

public enum AppLogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public readonly record struct LogEntry(
    DateTimeOffset At,
    AppLogLevel Level,
    string Category,
    string Message,
    Exception? Exception = null);

public interface IAppLog
{
    bool IsEnabled(AppLogLevel level);

    void Log(AppLogLevel level, string category, string message, Exception? exception = null);
}

public static class AppLogExtensions
{
    public static void Debug(this IAppLog log, string category, string message)
        => log.Log(AppLogLevel.Debug, category, message);

    public static void Info(this IAppLog log, string category, string message)
        => log.Log(AppLogLevel.Info, category, message);

    public static void Warn(this IAppLog log, string category, string message, Exception? exception = null)
        => log.Log(AppLogLevel.Warn, category, message, exception);

    public static void Error(this IAppLog log, string category, string message, Exception? exception = null)
        => log.Log(AppLogLevel.Error, category, message, exception);
}

public sealed class NullAppLog : IAppLog
{
    public static readonly NullAppLog Instance = new();

    private NullAppLog() { }

    public bool IsEnabled(AppLogLevel level) => false;

    public void Log(AppLogLevel level, string category, string message, Exception? exception = null) { }
}
