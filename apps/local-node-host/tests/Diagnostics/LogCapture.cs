using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Tests.Diagnostics;

/// <summary>One captured log call: its level, the rendered line, and the structured template values.</summary>
internal sealed record CapturedLogRecord(
    LogLevel Level,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> Values)
{
    /// <summary>The structured value logged under <paramref name="key"/> (for example <c>AuditEventType</c>), or null.</summary>
    internal object? Value(string key) =>
        Values.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.Ordinal)).Value;
}

/// <summary>
/// Captures every log call made through it, keeping both the rendered line and the structured values, so the
/// log-forging tests (CodeQL <c>cs/log-forging</c>) can assert what an argument looked like when it reached a
/// sink, and which values a line carries at all. Usable as a provider, an untyped logger or a typed logger.
/// </summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogRecord> _records = new();

    /// <summary>Every call captured so far, in order.</summary>
    internal IReadOnlyList<CapturedLogRecord> Records => [.. _records];

    /// <summary>A typed logger that writes into this capture.</summary>
    internal ILogger<T> For<T>() => new TypedSink<T>(this);

    /// <summary>Every rendered line and every structured value, joined, for a "does not appear anywhere" check.</summary>
    internal string AllText() => string.Join(
        "\n",
        Records.SelectMany(record => record.Values
            .Select(pair => pair.Value?.ToString() ?? string.Empty)
            .Prepend(record.Message)));

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Sink(this);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private void Record<TState>(LogLevel level, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state is IReadOnlyList<KeyValuePair<string, object?>> structured
            ? structured.ToList()
            : new List<KeyValuePair<string, object?>>();
        _records.Enqueue(new CapturedLogRecord(level, formatter(state, exception), values));
    }

    private class Sink(LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Record(logLevel, state, exception, formatter);
    }

    private sealed class TypedSink<T>(LogCapture owner) : Sink(owner), ILogger<T>
    {
    }
}
