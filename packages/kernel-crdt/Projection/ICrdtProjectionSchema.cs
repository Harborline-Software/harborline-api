namespace Harborline.Api.Kernel.Crdt;

/// <summary>
/// Describes the document container and durable reconciliation behavior for one synced doctype.
/// </summary>
public interface ICrdtProjectionSchema
{
    /// <summary>Gets the stable CRDT document identifier shared by every replica.</summary>
    string DocumentId { get; }

    /// <summary>Binds the schema to the projection-owned document and its change callback.</summary>
    void Bind(ICrdtDocument document, Action<CrdtProjectionChange> changed);

    /// <summary>Releases change subscriptions established by <see cref="Bind"/>.</summary>
    void Unbind();

    /// <summary>Reconciles a converged CRDT change into the doctype's durable read model.</summary>
    ValueTask ReconcileAsync(CrdtProjectionChange change, CancellationToken ct);
}

/// <summary>Identifies the portion of a doctype changed by a CRDT operation.</summary>
/// <param name="Key">The changed map key, or <see langword="null"/> for a whole-document change.</param>
/// <param name="Index">The changed list index, or <see langword="null"/> when it is not list-scoped.</param>
public readonly record struct CrdtProjectionChange(string? Key, int? Index)
{
    /// <summary>Gets a whole-document reconciliation request.</summary>
    public static CrdtProjectionChange All { get; } = new(null, null);

    /// <summary>Creates a map-key reconciliation request.</summary>
    public static CrdtProjectionChange ForKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return new(key, null);
    }

    /// <summary>Creates a list-index reconciliation request.</summary>
    public static CrdtProjectionChange ForIndex(int index) => new(null, index);
}

/// <summary>Reports whether an inbound CRDT delta was accepted.</summary>
/// <param name="Succeeded">Whether the CRDT engine accepted the delta.</param>
/// <param name="Error">The recoverable engine error when <paramref name="Succeeded"/> is false.</param>
public readonly record struct CrdtDeltaApplyResult(bool Succeeded, Exception? Error);
