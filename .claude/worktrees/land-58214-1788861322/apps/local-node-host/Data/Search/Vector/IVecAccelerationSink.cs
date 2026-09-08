using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The derived <c>vec0</c> cleartext acceleration table writer (ADR 0135 KG-search F3-lift amendment, Slice 1b).
/// The real <c>sqlite-vec</c> KNN path needs a cleartext bit-vector column to scan; this sink writes/deletes that
/// derived row alongside the durable per-subject-encrypted <see cref="VecRow"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived + rebuildable + purged-on-shred.</b> The durable per-subject-encrypted blob is the G-6 boundary;
/// the <c>vec0</c> entry is a cache of its (file-encrypted only) cleartext code. So the indexer DELETES the
/// vec0 entry whenever it deletes / purges the durable row — a crypto-shredded subject leaves no cleartext
/// residue in the acceleration table. A host that has no <c>vec0</c> native available registers NO sink at all
/// (null), runs the brute-force engine (which decrypts per-subject on the fly, holding no cleartext at rest),
/// and therefore has nothing to purge.
/// </para>
/// <para>
/// <b>Why the sink is a seam.</b> Writing the vec0 row requires the loaded native (the <c>search_vec0</c>
/// virtual table only exists once <c>vec0</c> is loaded). Abstracting it keeps the indexer native-agnostic and
/// lets a host without the native opt fully out (no half-built cleartext table).
/// </para>
/// </remarks>
public interface IVecAccelerationSink
{
    /// <summary>Upserts the cleartext packed binary code for <paramref name="recordId"/> into the vec0 table.</summary>
    Task UpsertAsync(string recordId, string tenantId, byte[] packedCode, CancellationToken ct);

    /// <summary>Deletes the vec0 row for <paramref name="recordId"/> (on durable-row delete / subject purge).</summary>
    Task DeleteAsync(string recordId, CancellationToken ct);
}
