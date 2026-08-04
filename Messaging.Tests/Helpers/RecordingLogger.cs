using Microsoft.Extensions.Logging;

namespace Messaging.Tests.Helpers;

/// <summary>
/// An <see cref="ILogger{T}"/> that keeps what it was told, for the handful of behaviours whose only
/// observable output <b>is</b> a log line - "this sweep is inert", "this rotation is running late".
///
/// <para>Those are operator-facing signals, and a signal nobody asserts on is a signal that stops
/// being emitted during the next refactor without anything failing. Everything else in this suite
/// asserts on state or on a returned result; use this only where the log is the product.</para>
/// </summary>
public sealed class RecordingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    public List<Entry> Entries { get; } = [];

    public IEnumerable<string> Messages(LogLevel level) =>
        Entries.Where(e => e.Level == level).Select(e => e.Message);

    public bool Any(LogLevel level, string containing) =>
        Messages(level).Any(m => m.Contains(containing, StringComparison.OrdinalIgnoreCase));

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add(new Entry(logLevel, formatter(state, exception), exception));

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
