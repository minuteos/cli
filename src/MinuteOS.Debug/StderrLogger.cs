using Microsoft.Extensions.Logging;

namespace MinuteOS.Debug;

/// <summary>
/// A minimal logger writing to stderr - for the DAP server, whose stdout
/// carries the protocol and must stay clean.
/// </summary>
public sealed class StderrLogger(LogLevel minLevel) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;
        Console.Error.WriteLine($"[{logLevel switch
        {
            LogLevel.Trace => "trc",
            LogLevel.Debug => "dbg",
            LogLevel.Information => "inf",
            LogLevel.Warning => "wrn",
            LogLevel.Error => "err",
            _ => "crt",
        }}] {formatter(state, exception)}{(exception != null ? $"\n{exception}" : "")}");
    }
}
