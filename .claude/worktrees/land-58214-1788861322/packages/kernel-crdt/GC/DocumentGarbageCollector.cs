using Harborline.Api.Kernel.Crdt.SnapshotScheduling;

namespace Harborline.Api.Kernel.Crdt.GarbageCollection;

/// <summary>
/// Default <see cref="IDocumentGarbageCollector"/> that delegates to an
/// <see cref="IShallowSnapshotManager"/> for shallow-snapshot collection.
/// </summary>
/// <remarks>
/// <para>
/// Strategy selection:
/// <list type="bullet">
///   <item>Evaluate the document's registered policy via
///     <see cref="IShallowSnapshotManager.RunEvaluationAsync"/>. If a snapshot was taken
///     for this document, the applied strategy is <see cref="GcStrategy.ShallowSnapshot"/>.</item>
///   <item>Otherwise the applied strategy is <see cref="GcStrategy.None"/>. YDotNet/yrs
///     performs its library garbage collection automatically when write transactions commit,
///     so there is no separate collector strategy to invoke.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class DocumentGarbageCollector : IDocumentGarbageCollector
{
    private readonly IShallowSnapshotManager _snapshots;
    private readonly TimeProvider _clock;

    /// <summary>Create a collector backed by the given snapshot manager and root clock.</summary>
    public DocumentGarbageCollector(IShallowSnapshotManager snapshots, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(clock);
        _snapshots = snapshots;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<GcResult> CollectAsync(ICrdtDocument doc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var bytesBefore = (ulong)doc.ToSnapshot().Length;
        var taken = await _snapshots.RunEvaluationAsync(ct).ConfigureAwait(false);
        var applied = taken.Any(r => string.Equals(r.DocumentId, doc.DocumentId, StringComparison.Ordinal))
            ? GcStrategy.ShallowSnapshot
            : GcStrategy.None;
        var bytesAfter = (ulong)doc.ToSnapshot().Length;

        return new GcResult(
            DocumentId: doc.DocumentId,
            AppliedStrategy: applied,
            BytesBefore: bytesBefore,
            BytesAfter: bytesAfter,
            At: _clock.GetUtcNow());
    }
}
