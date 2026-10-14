using Microsoft.Extensions.Logging;

namespace DarkTide.Room.Tests;

/// <summary>把日志条目留在内存里，供断言检查级别、内容与结构化参数。</summary>
internal sealed class RecordingLogger : ILogger
{
    public sealed record LogEntry(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Values);

    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var values = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
            ? pairs
            : Array.Empty<KeyValuePair<string, object?>>();

        lock (_entries)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), values));
        }
    }

    /// <summary>所有日志文本拼在一起，用于“必须不包含某内容”的断言。</summary>
    public string AllText => string.Join('\n', Entries.Select(entry => entry.Message));
}
