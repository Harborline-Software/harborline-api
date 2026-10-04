using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

// Bounded test-only startup diagnostics, emitted into the failed test's TRX output.
// A passive observer: it never changes pooling, retries work, resets a statement,
// disposes borrowed native handles, suppresses interception, or changes an exception.
internal sealed class SqliteStartupLifecycleProbe : IDisposable,
    IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private static readonly AsyncLocal<SqliteStartupLifecycleProbe?> Ambient = new();
    private readonly ConcurrentQueue<ProbeEvent> events = new();
    private readonly ConcurrentQueue<Failure> failures = new();
    private readonly ConcurrentQueue<ProbeEvent> failureEvents = new();
    private readonly HashSet<long> firstChanceIdentities = new();
    private readonly HashSet<long> firstChanceBusyIdentities = new();
    private readonly ConcurrentQueue<Failure> firstChanceBusy = new();
    private readonly ConcurrentQueue<Failure> lifecycleFailures = new();
    private readonly ConcurrentQueue<Failure> firstChanceSqlite = new();
    private readonly ConditionalWeakTable<object, Identity> identities = new();
    private readonly List<IDisposable> subscriptions = new();
    private readonly ConnectionObserver connections;
    private readonly CommandObserver commands;
    private long sequence;
    private long identitySequence;
    private int failureCount;
    private int lifecycleFailureCount;
    private int failureEventCount;

    internal SqliteStartupLifecycleProbe()
    {
        connections = new(this);
        commands = new(this);
        try { subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this)); }
        catch { /* A diagnostic listener failure must not affect startup. */ }
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
    }

    internal static SqliteStartupLifecycleProbe? Current => Ambient.Value;
    internal ProbeEvent[] Events => events.ToArray();
    internal ProbeEvent[] FailureEvents => failureEvents.ToArray();
    internal Failure[] Failures => failures.Concat(lifecycleFailures).OrderBy(entry => entry.Sequence).ToArray();
    internal Failure[] FirstChanceSqlite => firstChanceSqlite.Concat(firstChanceBusy).OrderBy(entry => entry.Sequence).ToArray();

    internal IDisposable EnterScope()
    {
        var prior = Ambient.Value;
        Ambient.Value = this;
        return new Scope(() => Ambient.Value = prior);
    }

    internal static Action<IServiceCollection>? Combine(Action<IServiceCollection>? existing)
    {
        var probe = Current;
        if (probe is null) return existing;
        return services =>
        {
            existing?.Invoke(services);
            // Append options configuration; keep the existing factory, SQLCipher key
            // interceptor, context ownership, connection string, and pooling policy.
            services.ConfigureDbContext<LocalNodeDbContext>(options =>
                options.AddInterceptors(probe.connections, probe.commands));
        };
    }

    internal void CaptureFailure(string phase, Exception exception)
    {
        try
        {
            // Reserve explicit startup/disposal evidence separately: interceptor
            // noise must not consume the slots for the decisive lifecycle faults.
            if (Interlocked.Increment(ref lifecycleFailureCount) <= 16)
                lifecycleFailures.Enqueue(new(Interlocked.Increment(ref sequence), phase, Describe(exception)));
        }
        catch { /* Observation cannot replace the exception being investigated. */ }
    }

    internal void Capture(string phase, DbConnection? connection = null,
        Guid? commandId = null, string? context = null, Exception? exception = null)
    {
        try
        {
            var entry = new ProbeEvent(Interlocked.Increment(ref sequence), phase,
                connection is null ? null : Id(connection), commandId, context,
                Snapshot(connection), exception is null ? null : Describe(exception));
            events.Enqueue(entry);
            while (events.Count > 128) events.TryDequeue(out _);
            if (exception is not null)
            {
                if (Interlocked.Increment(ref failureEventCount) <= 16) failureEvents.Enqueue(entry);
                CaptureObservedFailure(phase, exception);
            }
        }
        catch { /* No observer failure may affect the original operation. */ }
    }

    private void CaptureObservedFailure(string phase, Exception exception)
    {
        try
        {
            if (Interlocked.Increment(ref failureCount) <= 32)
                failures.Enqueue(new(Interlocked.Increment(ref sequence), phase, Describe(exception)));
        }
        catch { }
    }

    internal void ExportBestEffort(string destination)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            File.WriteAllText(destination, SerializeBoundedEvidence());
        }
        catch { /* A failed evidence write cannot hide the startup/disposal failure. */ }
    }

    internal static async Task RunObservedAsync(Func<Task> operation, Action<string> writeOutput)
    {
        using var probe = new SqliteStartupLifecycleProbe();
        using var scope = probe.EnterScope();
        try { await operation(); }
        catch (Exception original)
        {
            probe.CaptureFailure("test-propagated", original);
            probe.WriteOutputBestEffort(writeOutput);
            throw;
        }
    }

    internal void WriteOutputBestEffort(Action<string> writeOutput)
    {
        try
        {
            writeOutput("SQLITE_STARTUP_LIFECYCLE_V1 " + SerializeBoundedEvidence());
        }
        catch { /* Evidence serialization/output never replaces the primary fault. */ }
    }

    internal static string? SanitizeStack(string? stack)
    {
        if (stack is null) return null;
        var sanitized = System.Text.RegularExpressions.Regex.Replace(stack, @" in [^\r\n]+", "");
        return sanitized[..Math.Min(sanitized.Length, 4096)];
    }

    private long Id(object value) => identities.GetValue(value,
        _ => new Identity(Interlocked.Increment(ref identitySequence))).Value;

    private ExceptionEvidence Describe(Exception exception)
    {
        var remaining = 32;
        return Describe(exception, 0, ref remaining);
    }

    private ExceptionEvidence Describe(Exception exception, int depth, ref int remaining)
    {
        remaining--;
        IReadOnlyList<Exception> children = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : exception.InnerException is { } inner ? new[] { inner } : Array.Empty<Exception>();
        var retained = new List<ExceptionEvidence>();
        if (depth < 4)
            foreach (var child in children.Take(8))
            {
                if (remaining == 0) break;
                retained.Add(Describe(child, depth + 1, ref remaining));
            }
        return new(Id(exception), ClipMetadata(exception.GetType().FullName, 256)!,
            exception is SqliteException sqlite ? sqlite.SqliteErrorCode : null,
            exception is SqliteException extended ? extended.SqliteExtendedErrorCode : null,
            SanitizeStack(exception.StackTrace), retained.ToArray(), retained.Count < children.Count);
    }

    // A single export budget covers all exception graphs, not each branch or record.
    internal string SerializeBoundedEvidence()
    {
        var lifecycle = lifecycleFailures.ToArray();
        var observed = failures.ToArray();
        var native = FailureEvents;
        var firstChance = FirstChanceSqlite;
        var roots = lifecycle.Length + observed.Length + native.Count(row => row.Error is not null) + firstChance.Length;
        for (var stackBytes = 65536; ; stackBytes /= 2)
        {
            var budget = new ExportBudget(128 - roots, stackBytes);
            var explicitFailures = lifecycle.Select(row => Project(row, budget)).ToArray();
            var nativeFailures = native.Select(row => Project(row, budget, keepError: true)).ToArray();
            var observedFailures = observed.Select(row => Project(row, budget)).ToArray();
            var first = firstChance.Select(row => Project(row, budget)).ToArray();
            var json = JsonSerializer.Serialize(new
            {
                Events = Events.Select(row => Project(row, budget, keepError: false)).ToArray(),
                FailureEvents = nativeFailures,
                Failures = explicitFailures.Concat(observedFailures).ToArray(),
                FirstChanceSqlite = first,
                ExportTruncated = budget.Truncated,
            });
            if (System.Text.Encoding.UTF8.GetByteCount(json) <= 262144) return json;
            if (stackBytes == 0) break;
        }
        // Fixed-count, fixed-string summaries retain decisive lifecycle/native evidence.
        var fallback = new ExportBudget(0, 0);
        return JsonSerializer.Serialize(new
        {
            Events = Array.Empty<ProbeEvent>(),
            FailureEvents = native.Select(row => Project(row, fallback, keepError: true)).ToArray(),
            Failures = lifecycle.Select(row => Project(row, fallback)).ToArray(),
            FirstChanceSqlite = Array.Empty<Failure>(),
            ExportTruncated = true,
        });
    }

    private static Failure Project(Failure row, ExportBudget budget) => row with
    {
        Phase = ClipMetadata(row.Phase, 64)!,
        Error = Project(row.Error, budget),
        ObservationStack = budget.Stack(row.ObservationStack),
    };

    private static ProbeEvent Project(ProbeEvent row, ExportBudget budget, bool keepError) => row with
    {
        Phase = ClipMetadata(row.Phase, 64)!,
        Context = ClipMetadata(row.Context, 256),
        Error = keepError && row.Error is { } error ? Project(error, budget) : null,
        Native = row.Native with
        {
            Library = ClipMetadata(row.Native.Library, 128),
            State = ClipMetadata(row.Native.State, 32),
            UnavailableReason = ClipMetadata(row.Native.UnavailableReason, 128),
        },
    };

    private static ExceptionEvidence Project(ExceptionEvidence error, ExportBudget budget)
    {
        var stack = budget.Stack(error.Stack);
        var children = new List<ExceptionEvidence>();
        foreach (var child in error.Inner)
        {
            if (budget.NodesRemaining == 0) break;
            budget.NodesRemaining--;
            children.Add(Project(child, budget));
        }
        var omitted = error.Truncated || children.Count != error.Inner.Length;
        budget.Truncated |= omitted;
        return error with { Stack = stack, Inner = children.ToArray(), Truncated = omitted };
    }

    private static string? ClipMetadata(string? value, int bound) =>
        value is null ? null : value[..Math.Min(value.Length, bound)];

    private sealed class ExportBudget(int nodesRemaining, int stackBytesRemaining)
    {
        internal int NodesRemaining = Math.Max(0, nodesRemaining);
        internal bool Truncated;
        internal string? Stack(string? value)
        {
            if (value is null) return null;
            var bytes = System.Text.Encoding.UTF8.GetByteCount(value);
            if (bytes <= stackBytesRemaining) { stackBytesRemaining -= bytes; return value; }
            Truncated = true;
            return null;
        }
    }

    private NativeEvidence Snapshot(DbConnection? connection)
    {
        if (connection is not SqliteConnection sqlite || sqlite.Handle is not { } handle)
            return new(null, connection?.State.ToString(), null, null, null, null, false, "no-native-handle");
        var handleId = Id(handle);
        var visited = 0;
        var busy = 0;
        string? library = null;
        int? version = null;
        var addedReference = false;
        try
        {
            handle.DangerousAddRef(ref addedReference);
            library = SQLitePCL.raw.GetNativeLibraryName();
            if (library != "e_sqlcipher")
                return new(handleId, sqlite.State.ToString(), library, null, null, null, false, "unexpected-native-library");
            version = SQLitePCL.raw.sqlite3_libversion_number();
            IntPtr previous = IntPtr.Zero;
            var seen = new HashSet<IntPtr>();
            while (true)
            {
                // Read-only native traversal creates no owning statement wrappers.
                var statement = NativeMethods.sqlite3_next_stmt(handle.DangerousGetHandle(), previous);
                if (statement == IntPtr.Zero)
                    return new(handleId, sqlite.State.ToString(), library, version, visited, busy, true, null);
                if (visited >= 128 || !seen.Add(statement))
                    return new(handleId, sqlite.State.ToString(), library, version, visited, busy, false, "enumeration-bound");
                visited++;
                if (NativeMethods.sqlite3_stmt_busy(statement) != 0) busy++;
                previous = statement;
            }
        }
        catch (Exception error)
        {
            // Native-library or enumeration failure is incomplete evidence,
            // never a fabricated zero statement count.
            return new(handleId, sqlite.State.ToString(), library, version, visited, busy, false,
                error.GetType().FullName);
        }
        finally
        {
            if (addedReference) handle.DangerousRelease();
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("e_sqlcipher", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_next_stmt(IntPtr connection, IntPtr previous);

        [System.Runtime.InteropServices.DllImport("e_sqlcipher", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        internal static extern int sqlite3_stmt_busy(IntPtr statement);
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name != "Microsoft.EntityFrameworkCore") return;
        try
        {
            lock (subscriptions)
                subscriptions.Add(listener.Subscribe(this,
                    name => name == CoreEventId.QueryIterationFailed.Name));
        }
        catch { }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (ReferenceEquals(Current, this) && value.Value is DbContextErrorEventData error)
            CaptureObservedFailure("query-iteration-failed", error.Exception);
    }

    public void OnError(Exception error) { }
    public void OnCompleted() { }

    private void OnFirstChance(object? sender, FirstChanceExceptionEventArgs data)
    {
        // The provider's reader-disposal drain can catch an earlier SQLite error.
        // Keep its identity separate from subsequently propagated closing/startup/
        // disposal errors. A first-chance error alone is not an unhandled failure.
        if (!ReferenceEquals(Current, this) || data.Exception is not SqliteException) return;
        try
        {
            lock (firstChanceIdentities)
            {
                var busy = ((SqliteException)data.Exception).SqliteErrorCode == 5;
                var seen = busy ? firstChanceBusyIdentities : firstChanceIdentities;
                if (seen.Count >= (busy ? 8 : 16) || !seen.Add(Id(data.Exception))) return;
                var destination = busy ? firstChanceBusy : firstChanceSqlite;
                destination.Enqueue(new(Interlocked.Increment(ref sequence),
                        "first-chance-sqlite-not-necessarily-unhandled", Describe(data.Exception),
                        SanitizeStack(Environment.StackTrace)));
            }
        }
        catch { }
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        lock (subscriptions)
            foreach (var subscription in subscriptions)
                try { subscription.Dispose(); } catch { }
    }

    internal sealed record Identity(long Value);
    internal sealed record NativeEvidence(long? HandleId, string? State, string? Library,
        int? Version, int? StatementsVisited, int? BusyVisited, bool Complete, string? UnavailableReason);
    internal sealed record ExceptionEvidence(long Id, string Type, int? SqliteCode,
        int? SqliteExtendedCode, string? Stack, ExceptionEvidence[] Inner, bool Truncated = false);
    internal sealed record ProbeEvent(long Sequence, string Phase, long? ConnectionId,
        Guid? CommandId, string? Context, NativeEvidence Native, ExceptionEvidence? Error);
    internal sealed record Failure(long Sequence, string Phase, ExceptionEvidence Error,
        string? ObservationStack = null);
    private sealed class Scope(Action release) : IDisposable { public void Dispose() => release(); }

    private sealed class ConnectionObserver(SqliteStartupLifecycleProbe probe) : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData data) =>
            probe.Capture("opened", connection, context: data.Context?.GetType().FullName);
        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData data,
            CancellationToken ct = default)
        {
            ConnectionOpened(connection, data);
            return Task.CompletedTask;
        }
        public override InterceptionResult ConnectionClosing(DbConnection connection,
            ConnectionEventData data, InterceptionResult result)
        {
            probe.Capture("closing-before-provider-cleanup", connection, context: data.Context?.GetType().FullName);
            return result;
        }
        public override ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection,
            ConnectionEventData data, InterceptionResult result) => new(ConnectionClosing(connection, data, result));
        public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData data) =>
            probe.Capture("connection-failed-after-provider-cleanup", connection,
                context: data.Context?.GetType().FullName, exception: data.Exception);
        public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData data,
            CancellationToken ct = default)
        {
            ConnectionFailed(connection, data);
            return Task.CompletedTask;
        }
    }

    private sealed class CommandObserver(SqliteStartupLifecycleProbe probe) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result)
        {
            probe.Capture("reader-executing", command.Connection, data.CommandId, data.Context?.GetType().FullName);
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default) =>
            new(ReaderExecuting(command, data, result));
        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData data, DbDataReader result)
        {
            probe.Capture("reader-executed", command.Connection, data.CommandId, data.Context?.GetType().FullName);
            return result;
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default) =>
            new(ReaderExecuted(command, data, result));
        public override InterceptionResult DataReaderClosing(DbCommand command,
            DataReaderClosingEventData data, InterceptionResult result)
        {
            probe.Capture("reader-closing", command.Connection, data.CommandId, data.Context?.GetType().FullName);
            return result;
        }
        public override ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
            DataReaderClosingEventData data, InterceptionResult result) => new(DataReaderClosing(command, data, result));
        public override InterceptionResult DataReaderDisposing(DbCommand command,
            DataReaderDisposingEventData data, InterceptionResult result)
        {
            probe.Capture("reader-disposing-before-release", command.Connection, data.CommandId, data.Context?.GetType().FullName);
            return result;
        }
        public override void CommandFailed(DbCommand command, CommandErrorEventData data) =>
            probe.Capture("command-failed", command.Connection, data.CommandId,
                data.Context?.GetType().FullName, data.Exception);
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData data, CancellationToken ct = default)
        {
            CommandFailed(command, data);
            return Task.CompletedTask;
        }
    }
}
