using Microsoft.Extensions.Logging;

namespace Echo.Tests.Support;

/// <summary>One log call, flattened to what a test wants to assert on.</summary>
public sealed record LoggedLine(LogLevel Level, string Message, Exception? Exception);

/// <summary>An <see cref="ILogger{T}"/> that keeps what it was told.</summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LoggedLine> Lines { get; } = new();

    public IEnumerable<LoggedLine> AtLevel(LogLevel level) => Lines.Where(l => l.Level == level);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Lines.Add(new LoggedLine(logLevel, formatter(state, exception), exception));
    }
}
