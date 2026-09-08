namespace Harborline.Api.Kernel.Crdt;

/// <summary>Exposes the local-delta signal shared by generic CRDT projections.</summary>
public interface ICrdtProjectionEventSource
{
    /// <summary>Raised after a local mutation produces a CRDT operation.</summary>
    event EventHandler? LocalDeltaProduced;
}

/// <summary>Aggregates local-delta signals from all registered doctype projections.</summary>
public interface ICrdtProjectionRegistry : ICrdtProjectionEventSource
{
    /// <summary>Registers a doctype projection as a local-delta signal source.</summary>
    void Register(ICrdtProjectionEventSource projection);
}

/// <summary>Default process-level registry for doctype projection signals.</summary>
public sealed class CrdtProjectionRegistry : ICrdtProjectionRegistry
{
    private readonly HashSet<ICrdtProjectionEventSource> _projections =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    /// <inheritdoc />
    public event EventHandler? LocalDeltaProduced;

    /// <inheritdoc />
    public void Register(ICrdtProjectionEventSource projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        lock (_gate)
        {
            if (_projections.Add(projection))
            {
                projection.LocalDeltaProduced += OnLocalDeltaProduced;
            }
        }
    }

    private void OnLocalDeltaProduced(object? sender, EventArgs args) =>
        LocalDeltaProduced?.Invoke(sender ?? this, args);
}
