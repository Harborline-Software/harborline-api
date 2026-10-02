using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Tests.TestDoubles;

// Store only structural metadata. Never invoke the log formatter or read exception messages,
// request/response bodies, headers, URLs, environment values, or principal/session identifiers.
internal sealed class FailureOnlyHttpDiagnostics : ILoggerProvider
{
    private const int Capacity = 64;
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private int _sequence;

    public ILogger CreateLogger(string categoryName) => new StructuralLogger(this,
        categoryName.StartsWith("Microsoft.AspNetCore.Server.Kestrel", StringComparison.Ordinal));
    public void Dispose() { }

    public async Task ObserveAsync(HttpContext context, RequestDelegate next)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        Add($"request {sequence} started");
        try
        {
            await next(context);
            Add($"request {sequence} completed status={context.Response.StatusCode}");
        }
        catch (Exception exception)
        {
            Add($"request {sequence} threw type={exception.GetType().FullName}");
            throw;
        }
    }

    public string Snapshot() => string.Join(Environment.NewLine, _entries.ToArray());

    public void WriteStatusFailure(int expected, int actual, Action<string> write)
    {
        if (expected == actual) return;
        write($"HTTP fixture failure: expected={expected}, actual={actual}");
        write(Snapshot());
    }

    private void Add(string entry)
    {
        _entries.Enqueue($"{Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F0}ms {entry}");
        while (_entries.Count > Capacity) _entries.TryDequeue(out _);
    }

    private sealed class StructuralLogger(FailureOnlyHttpDiagnostics owner, bool kestrel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => kestrel && level >= LogLevel.Debug && level < LogLevel.None;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level)) owner.Add($"kestrel level={level} event={id.Id} exceptionType={exception?.GetType().FullName ?? "none"}");
        }
    }
}
