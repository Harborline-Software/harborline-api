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

/// <summary>
/// One pack projection: stage private registry copies and one uncommitted durable unit, run the
/// existing admission/projector, then publish by releasing the barrier after the durable commit.
/// Discarding a plan restores original references without replaying definitions or lifecycle writes.
/// </summary>
public sealed class PackProjectionTransaction : IDisposable
{
    private readonly IDisposable lease;
    private readonly HashSet<object> participants = new(ReferenceEqualityComparer.Instance);
    private readonly List<Action> rollback = [];
    private readonly List<Action> cleanup = [];
    private readonly List<Func<Task>> reactions = [];
    private IPackProjectionDurableUnit? durable;
    private bool committed;
    private bool disposed;

    /// <summary>Begins an isolated projection. All registry readers participate in the same barrier.</summary>
    public PackProjectionTransaction(CancellationToken cancellationToken = default)
        => lease = PackProjectionActivationBarrier.Write(cancellationToken);

    /// <summary>Requires transaction support for each configured runtime or durable store.</summary>
    public void Enlist(object? participant)
    {
        if (participant is null) return;
        if (participant is not IPackProjectionParticipant supported)
            throw new InvalidOperationException($"Projection store '{participant.GetType().FullName}' cannot stage atomically.");
        supported.StageProjection(this);
    }

    /// <summary>
    /// Stages a store once. Prepare must allocate every clone before replacing any reference, and
    /// return a rollback consisting only of reference assignments (no callbacks or persistence).
    /// </summary>
    public void Stage(object participant, Func<Action> prepare)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (committed) throw new InvalidOperationException("The projection has already committed.");
        if (participants.Add(participant)) rollback.Add(prepare());
    }

    /// <summary>Joins the one durable unit; a second database transaction is refused.</summary>
    public T Durable<T>(Func<T> create) where T : class, IPackProjectionDurableUnit
    {
        if (durable is null) durable = create();
        return durable as T ?? throw new InvalidOperationException("Projection participants must share one durable transaction.");
    }

    /// <summary>Resets a participant's private transaction handle on either outcome; reference assignments only.</summary>
    public void Finally(Action release) => cleanup.Add(release);

    /// <summary>Defers observers until the committed projection is visible and the write lease is released.</summary>
    public void AfterCommit(Func<Task> reaction) => reactions.Add(reaction);

    /// <summary>Runs post-commit observers outside the activation lease.</summary>
    public async Task ReactAsync()
    {
        if (!disposed) throw new InvalidOperationException("Release the projection lease before notifying observers.");
        if (!committed) return;
        foreach (var reaction in reactions) await reaction().ConfigureAwait(false);
    }

    /// <summary>Commits durable state before exposing the prepared registry references.</summary>
    public void Commit()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (committed) throw new InvalidOperationException("The projection has already committed.");
        durable?.Commit();
        committed = true;
    }

    /// <summary>Discards staged state, or releases the successfully committed projection to readers.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (!committed)
                for (var index = rollback.Count - 1; index >= 0; index--) rollback[index]();
        }
        finally
        {
            try
            {
                for (var index = cleanup.Count - 1; index >= 0; index--) cleanup[index]();
                durable?.Dispose();
            }
            finally { lease.Dispose(); }
        }
    }
}
