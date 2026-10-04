using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Tests.Packs;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

// Test-only, bounded witnesses. Never records SQL, parameters, keys, request bodies or exceptions.
internal sealed class ConfigurationExampleDiagnostics
{
    private const int Capacity = 512;
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly ConcurrentQueue<string> _phases = new();
    private readonly AsyncLocal<int> _request = new();
    private readonly string _fixture = Guid.NewGuid().ToString("N");
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _nextRequest;
    private int _sequence;

    internal IDisposable BeginRequest()
    {
        var previous = _request.Value;
        _request.Value = Interlocked.Increment(ref _nextRequest);
        Observe("request-start");
        return new Scope(() => _request.Value = previous);
    }

    internal void Observe(string phase)
    {
        if (_request.Value == 0) return;
        // Observation cannot replace the original workload failure.
        try
        {
            var sequence = Interlocked.Increment(ref _sequence);
            if (sequence > Capacity) return;
            _phases.Enqueue(phase);
            _entries.Enqueue($"fixture={_fixture} request={_request.Value} sequence={sequence} elapsedMs={_clock.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId} phase={phase}");
            if (sequence == Capacity) _entries.Enqueue("trace-capacity-reached");
        }
        catch { /* Test diagnostics are non-gating. */ }
    }

    internal void Clear()
    {
        _entries.Clear(); _phases.Clear(); Interlocked.Exchange(ref _sequence, 0);
    }
    internal string[] Phases() => _phases.ToArray();
    internal string Render() => string.Join("; ", _entries);
    internal IDbContextFactory<NodeLocalPacksDbContext> ObserveFactory(PacksTestStore store) => new Factory(store, this);

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class Factory(PacksTestStore store, ConfigurationExampleDiagnostics trace) : IDbContextFactory<NodeLocalPacksDbContext>
    {
        public NodeLocalPacksDbContext CreateDbContext()
        {
            trace.Observe("context-create-start");
            try
            {
                // The harness opens and keys SQLCipher here before EF receives its connection.
                var context = store.CreateContext(new Commands(trace));
                trace.Observe("context-create-complete");
                return context;
            }
            catch { trace.Observe("context-create-failed"); throw; }
        }
    }

    private sealed class Commands(ConfigurationExampleDiagnostics trace) : DbCommandInterceptor, ISaveChangesInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        { trace.Observe("command-reader-start"); return result; }
        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        { trace.Observe("command-reader-complete"); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { trace.Observe("command-reader-start"); return ValueTask.FromResult(result); }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        { trace.Observe("command-reader-complete"); return ValueTask.FromResult(result); }
        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => trace.Observe("command-failed");
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        { trace.Observe("command-failed"); return Task.CompletedTask; }
        public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        { trace.Observe("save-changes-start"); return result; }
        public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        { trace.Observe("save-changes-complete"); return result; }
        public void SaveChangesFailed(DbContextErrorEventData eventData) => trace.Observe("save-changes-failed");
    }
}
