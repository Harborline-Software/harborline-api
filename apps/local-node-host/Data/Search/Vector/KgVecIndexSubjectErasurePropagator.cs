using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The KG-search F1 wire (ADR 0135 KG-search F3-lift amendment, Slice 1d; the binding Slice-1b verdict gate
/// F1). Drops a crypto-shredded subject's DERIVED CLEARTEXT residue from the local KG vector index when an
/// erasure takes effect — closing the gap the Slice-1b deep-review named: <see cref="NodeVecIndexer.PurgeSubjectAsync"/>
/// existed but had no erasure-flow caller, so a real-<c>vec0</c> host's cleartext acceleration cache would
/// outlive the crypto-shred.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this closes.</b> Crypto-shred makes a subject's DURABLE <c>encrypted_embedding</c> undecryptable
/// the instant the sub-key is gone — the shred boundary is already correct without this propagator. But the
/// derived, partially-invertible CLEARTEXT bit code in the <c>vec0</c> acceleration table is a SEPARATE,
/// unencrypted cache; only an explicit purge drops it. This propagator is registered into the foundation
/// erasure flow (<see cref="ISubjectErasureService.EraseAsync"/> ⇒
/// <see cref="ISubjectErasurePropagator.PropagateErasureAsync"/>), so a subject-shred removes both the durable
/// rows AND their cleartext <c>vec0</c> residue — a shred leaves no cleartext embedding of the subject.
/// </para>
/// <para>
/// <b>Scope + safety.</b> <see cref="NodeVecIndexer.PurgeSubjectAsync"/> deletes ONLY rows matching
/// <c>(tenant, subject)</c> and their derived <c>vec0</c> entries; other subjects' encrypted rows are
/// untouched and stay decryptable. It is idempotent (a re-run purges zero rows). On a brute-force-only host
/// (no acceleration sink), the purge of the durable rows is still correct and there is no cleartext cache to
/// drop — the propagator is a safe no-cleartext-residue operation on every host.
/// </para>
/// </remarks>
public sealed class KgVecIndexSubjectErasurePropagator : ISubjectErasurePropagator
{
    private readonly NodeVecIndexer _indexer;

    /// <summary>Construct bound to the node's KG vector indexer (the owner of the purge path).</summary>
    public KgVecIndexSubjectErasurePropagator(NodeVecIndexer indexer)
    {
        _indexer = indexer ?? throw new System.ArgumentNullException(nameof(indexer));
    }

    /// <inheritdoc />
    public async Task PropagateErasureAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default)
    {
        await _indexer.PurgeSubjectAsync(tenant.Value, subject.Value, ct).ConfigureAwait(false);
    }
}
