namespace Harborline.Api.Foundation.Definitions;

/// <summary>A store whose pack projection state can be staged behind the activation barrier.</summary>
public interface IPackProjectionParticipant
{
    /// <summary>Enlists once, retaining the original state until the whole projection commits.</summary>
    void StageProjection(PackProjectionTransaction transaction);
}

/// <summary>The single durable transaction shared by every persistent projection participant.</summary>
public interface IPackProjectionDurableUnit : IDisposable
{
    /// <summary>Commits all staged durable writes; a failure must leave the transaction uncommitted.</summary>
    void Commit();
}

/// <summary>
/// Protects the existing pack registries while one projection prepares private copies. The ambient
/// value identifies only lease ownership, never the staged state or authorization of a caller.
/// </summary>
public static class PackProjectionActivationBarrier
{
    private static readonly object Gate = new();
    private static readonly AsyncLocal<Lease?> Ownership = new();
    private static int readers;
    private static bool writing;

    /// <summary>Holds a stable published projection across a read or ordinary registry mutation.</summary>
    public static IDisposable Read(CancellationToken cancellationToken = default) => Enter(false, cancellationToken);

    internal static IDisposable Write(CancellationToken cancellationToken) => Enter(true, cancellationToken);

    private static IDisposable Enter(bool write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previous = Ownership.Value;
        if (previous is { Active: true })
        {
            if (write && !previous.Writing)
                throw new InvalidOperationException("A projection read lease cannot be upgraded to activation.");
            return EmptyLease.Instance;
        }
        lock (Gate)
        {
            while (writing || (write && readers != 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(Gate, 50);
            }
            if (write) writing = true;
            else readers++;
            var lease = new Lease(write, previous);
            Ownership.Value = lease;
            return lease;
        }
    }

    private sealed class EmptyLease : IDisposable
    {
        internal static readonly EmptyLease Instance = new();
        public void Dispose() { }
    }

    private sealed class Lease(bool write, Lease? previous) : IDisposable
    {
        internal bool Active { get; private set; } = true;
        internal bool Writing { get; } = write;
        public void Dispose()
        {
            lock (Gate)
            {
                if (!Active) return;
                Active = false;
                if (Writing) writing = false;
                else readers--;
                Ownership.Value = previous;
                Monitor.PulseAll(Gate);
            }
        }
    }
}

/// <summary>The actual operation that produced a secondary projection diagnostic.</summary>
public enum PackProjectionDiagnosticKind
{
    /// <summary>A rollback callback failed; rollback completion is unconfirmed.</summary>
    Rollback,
    /// <summary>A participant cleanup callback failed.</summary>
    Cleanup,
    /// <summary>The durable resource did not confirm disposal.</summary>
    DurableRelease,
    /// <summary>The activation lease did not confirm disposal.</summary>
    LeaseRelease,
    /// <summary>An eligible deferred refusal record actually failed.</summary>
    DeferredRefusalRecord,
    /// <summary>An eligible observer of an actual commit failed.</summary>
    CommitReaction,
    /// <summary>Unconfirmed resource release retired callbacks without running them.</summary>
    DeliverySuppressed,
}

/// <summary>A tagged exact secondary exception, or a delivery suppression with no fabricated exception.</summary>
public sealed record PackProjectionDiagnostic(PackProjectionDiagnosticKind Kind, Exception? Error);

/// <summary>An immutable snapshot after the first reaction, independent of accepted or staged outcomes.</summary>
public sealed record PackProjectionDiagnostics(
    bool Committed, bool ResourceReleaseConfirmed, IReadOnlyList<PackProjectionDiagnostic> Failures);

/// <summary>One owner stages, commits or discards, releases, then reacts to a projection.</summary>
public sealed class PackProjectionTransaction : IDisposable
{
    private readonly IDisposable lease;
    private readonly HashSet<object> participants = new(ReferenceEqualityComparer.Instance);
    private readonly List<Action> rollback = [];
    private readonly List<Action> cleanup = [];
    private readonly List<Func<Task>> reactions = [];
    private readonly List<Func<Task>> released = [];
    private readonly List<PackProjectionDiagnostic> failures = [];
    private IPackProjectionDurableUnit? durable;
    private bool committed;
    private bool disposed;
    private bool reacted;
    private bool protectedCleanup;
    private bool failed;
    private bool resourceReleaseConfirmed;
    private Exception? failureCause;
    private Exception? postCommitCleanupFailure;

    /// <summary>Begins an isolated projection behind the shared activation barrier.</summary>
    public PackProjectionTransaction(CancellationToken cancellationToken = default)
        => lease = PackProjectionActivationBarrier.Write(cancellationToken);

    /// <summary>The first completed reaction snapshot; later no-op reactions retain it.</summary>
    public PackProjectionDiagnostics? Diagnostics { get; private set; }

    /// <summary>Opts a live joined attempt into complete owner cleanup before any participant enlistment.</summary>
    public void BeginJoinedInstall()
    {
        RequireActive();
        protectedCleanup = true;
    }

    /// <summary>Irreversibly prevents Commit and enables complete cleanup; performs no cleanup itself.</summary>
    public void MarkFailed(Exception? cause = null)
    {
        RequireLive();
        if (!failed) failureCause = cause;
        failed = true;
        protectedCleanup = true;
    }

    private void RequireLive()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (committed) throw new InvalidOperationException("The projection has already committed.");
    }

    private void RequireActive()
    {
        RequireLive();
        if (failed) throw new InvalidOperationException("The projection contains a failed joined install and cannot commit or stage more work.", failureCause);
    }

    /// <summary>Enlists a supported participant only while active.</summary>
    public void Enlist(object? participant)
    {
        RequireActive();
        if (participant is null) return;
        try
        {
            if (participant is not IPackProjectionParticipant supported)
                throw new InvalidOperationException($"Projection store '{participant.GetType().FullName}' cannot stage atomically.");
            supported.StageProjection(this);
        }
        catch (Exception exception)
        {
            if (protectedCleanup) MarkFailed(exception);
            throw;
        }
    }

    /// <summary>Stages one private reference copy, retaining its rollback until owner disposal.</summary>
    public void Stage(object participant, Func<Action> prepare)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(prepare);
        RequireActive();
        try { if (participants.Add(participant)) rollback.Add(prepare()); }
        catch (Exception exception)
        {
            if (protectedCleanup) MarkFailed(exception);
            throw;
        }
    }

    /// <summary>Joins one durable unit while active.</summary>
    public T Durable<T>(Func<T> create) where T : class, IPackProjectionDurableUnit
    {
        ArgumentNullException.ThrowIfNull(create);
        RequireActive();
        try
        {
            if (durable is null) durable = create();
            return durable as T ?? throw new InvalidOperationException("Projection participants must share one durable transaction.");
        }
        catch (Exception exception)
        {
            if (protectedCleanup) MarkFailed(exception);
            throw;
        }
    }

    /// <summary>Registers owed cleanup even after failure, before disposal.</summary>
    public void Finally(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        ObjectDisposedException.ThrowIf(disposed, this);
        cleanup.Add(release);
    }

    /// <summary>Defers success observers until actual Commit and confirmed resource release.</summary>
    public void AfterCommit(Func<Task> reaction)
    {
        ArgumentNullException.ThrowIfNull(reaction);
        RequireActive();
        reactions.Add(reaction);
    }

    /// <summary>Registers a refusal record after either outcome and confirmed release.</summary>
    public void AfterRelease(Func<Task> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(disposed, this);
        released.Add(record);
    }

    /// <summary>Runs eligible callbacks once; uncertain release retires them without delivery or retry.</summary>
    public async Task<AggregateException?> ReactAsync()
    {
        if (!disposed) throw new InvalidOperationException("Release the projection lease before notifying observers.");
        if (reacted) return null;
        reacted = true;
        if (postCommitCleanupFailure is not null)
            Add(PackProjectionDiagnosticKind.Cleanup, postCommitCleanupFailure);
        if (resourceReleaseConfirmed)
        {
            foreach (var record in released)
                await CollectAsync(record, PackProjectionDiagnosticKind.DeferredRefusalRecord).ConfigureAwait(false);
            if (committed)
                foreach (var reaction in reactions)
                    await CollectAsync(reaction, PackProjectionDiagnosticKind.CommitReaction).ConfigureAwait(false);
        }
        else if (released.Count != 0 || reactions.Count != 0)
            failures.Add(new(PackProjectionDiagnosticKind.DeliverySuppressed, null));
        released.Clear();
        reactions.Clear();
        Diagnostics = new PackProjectionDiagnostics(committed, resourceReleaseConfirmed,
            Array.AsReadOnly(failures.ToArray()));
        var errors = failures.Where(item => item.Error is not null).Select(item => item.Error!).ToArray();
        return errors.Length == 0 ? null : new AggregateException(
            committed ? "Projection committed; post-commit notification or cleanup failed." : "Projection discarded; secondary work failed.", errors);
    }

    private void Add(PackProjectionDiagnosticKind kind, Exception error)
    {
        if (!ReferenceEquals(error, failureCause)) failures.Add(new(kind, error));
    }

    private async Task CollectAsync(Func<Task> reaction, PackProjectionDiagnosticKind kind)
    {
        try { await reaction().ConfigureAwait(false); }
        #pragma warning disable CA1031 // reviewed-suppression: t1098-callback-collection
        catch (Exception exception) { Add(kind, exception); }
        #pragma warning restore CA1031
    }

    /// <summary>Commits once; a failed durable call poisons every later Commit.</summary>
    public void Commit()
    {
        RequireActive();
        try
        {
            durable?.Commit();
            committed = true;
        }
        catch (Exception exception)
        {
            MarkFailed(exception);
            throw;
        }
    }

    /// <summary>Only the owner restores references and attempts durable and lease release.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (protectedCleanup)
        {
            if (!committed)
                for (var index = rollback.Count - 1; index >= 0; index--)
                    Attempt(rollback[index], PackProjectionDiagnosticKind.Rollback);
            for (var index = cleanup.Count - 1; index >= 0; index--)
                Attempt(cleanup[index], PackProjectionDiagnosticKind.Cleanup);
            var durableReleased = durable is null || Attempt(durable.Dispose, PackProjectionDiagnosticKind.DurableRelease);
            var leaseReleased = Attempt(lease.Dispose, PackProjectionDiagnosticKind.LeaseRelease);
            resourceReleaseConfirmed = durableReleased && leaseReleased;
            return;
        }
        try
        {
            if (!committed)
                for (var index = rollback.Count - 1; index >= 0; index--) rollback[index]();
        }
        finally
        {
            var durableReleased = durable is null;
            try
            {
                for (var index = cleanup.Count - 1; index >= 0; index--) cleanup[index]();
                durable?.Dispose();
                durableReleased = true;
            }
            catch (Exception exception) when (committed) { postCommitCleanupFailure = exception; }
            finally
            {
                lease.Dispose();
                resourceReleaseConfirmed = durableReleased;
            }
        }
    }

    private bool Attempt(Action action, PackProjectionDiagnosticKind kind)
    {
        try { action(); return true; }
        #pragma warning disable CA1031 // reviewed-suppression: t1098-cleanup-attempt
        catch (Exception exception) { Add(kind, exception); return false; }
        #pragma warning restore CA1031
    }
}
