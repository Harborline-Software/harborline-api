namespace Harborline.Api.Kernel.Crdt;

/// <summary>
/// Doctype-generic CRDT projection that owns document mutation, delta merge, and the concurrency gate.
/// </summary>
/// <typeparam name="TSchema">The doctype schema bound to the projection document.</typeparam>
public sealed class CrdtProjection<TSchema> : ICrdtProjectionEventSource, IAsyncDisposable
    where TSchema : ICrdtProjectionSchema
{
    private readonly ICrdtDocument _document;
    private readonly TSchema _schema;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly List<Task> _pendingReconciles = [];
    private readonly object _pendingGate = new();

    /// <summary>Creates a projection over a fresh document described by <paramref name="schema"/>.</summary>
    public CrdtProjection(ICrdtEngine engine, TSchema schema)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _document = engine.CreateDocument(schema.DocumentId);
        schema.Bind(_document, OnChanged);
    }

    /// <summary>Raised after a local mutation produces a CRDT operation.</summary>
    public event EventHandler? LocalDeltaProduced;

    /// <summary>Gets the current opaque document state vector under the projection gate.</summary>
    public ReadOnlyMemory<byte> CurrentStateVector
    {
        get
        {
            lock (_gate)
            {
                return _document.VectorClock;
            }
        }
    }

    /// <summary>Runs a local schema mutation under the projection gate and optionally signals the produced delta.</summary>
    public void Mutate(Action<TSchema> mutation, bool signalLocalDelta = true)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_gate)
        {
            mutation(_schema);
        }

        if (signalLocalDelta)
        {
            LocalDeltaProduced?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Signals that a previously completed local mutation produced a delta.</summary>
    public void SignalLocalDeltaProduced() => LocalDeltaProduced?.Invoke(this, EventArgs.Empty);

    /// <summary>Reads schema state under the projection gate.</summary>
    public TResult Read<TResult>(Func<TSchema, TResult> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
        {
            return read(_schema);
        }
    }

    /// <summary>Encodes operations not represented by <paramref name="peerVectorClock"/>.</summary>
    public ReadOnlyMemory<byte> EncodeDelta(ReadOnlyMemory<byte> peerVectorClock)
    {
        lock (_gate)
        {
            return _document.EncodeDelta(peerVectorClock);
        }
    }

    /// <summary>Applies an inbound delta under the gate, returning a recoverable failure instead of throwing.</summary>
    public CrdtDeltaApplyResult ApplyDelta(
        string documentId,
        ulong opSequence,
        ReadOnlyMemory<byte> delta)
    {
        try
        {
            lock (_gate)
            {
                _document.ApplyDelta(delta);
            }

            return new(true, null);
        }
        catch (Exception ex)
        {
            return new(false, ex);
        }
    }

    /// <summary>Runs whole-document reconciliation through the bound schema.</summary>
    public Task ReconcileAsync(CancellationToken ct) =>
        ReconcileAsync(CrdtProjectionChange.All, ct);

    /// <summary>Runs change-scoped reconciliation through the bound schema.</summary>
    public async Task ReconcileAsync(CrdtProjectionChange change, CancellationToken ct)
    {
        await _reconcileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _schema.ReconcileAsync(change, ct).ConfigureAwait(false);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    /// <summary>Waits for every change-driven reconciliation scheduled so far.</summary>
    public async Task DrainPendingReconcilesAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_pendingGate)
            {
                _pendingReconciles.RemoveAll(static task => task.IsCompleted);
                pending = _pendingReconciles.ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private void OnChanged(CrdtProjectionChange change)
    {
        var task = ReconcileAsync(change, CancellationToken.None);
        lock (_pendingGate)
        {
            _pendingReconciles.RemoveAll(static pending => pending.IsCompleted);
            _pendingReconciles.Add(task);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DrainPendingReconcilesAsync().ConfigureAwait(false);
        _schema.Unbind();
        await _document.DisposeAsync().ConfigureAwait(false);
        _reconcileGate.Dispose();
    }
}
