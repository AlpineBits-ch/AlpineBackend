using Microsoft.Extensions.Logging;

namespace Echo.Tests.Support;

/// <summary>
/// Host-wide sink for log lines, so a test can assert on what a middleware logged rather than only
/// on what it returned.
/// </summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly List<(string Category, LoggedLine Line)> _lines = [];
    private readonly object _gate = new();

    public IReadOnlyList<(string Category, LoggedLine Line)> Lines
    {
        get { lock (_gate) return _lines.ToList(); }
    }

    public IEnumerable<LoggedLine> For(string category) =>
        Lines.Where(l => l.Category == category).Select(l => l.Line);

    public ILogger CreateLogger(string categoryName) => new CategoryLogger(this, categoryName);

    public void Dispose() { }

    private void Add(string category, LoggedLine line)
    {
        lock (_gate) _lines.Add((category, line));
    }

    private sealed class CategoryLogger(RecordingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Add(category, new LoggedLine(logLevel, formatter(state, exception), exception));
    }
}
