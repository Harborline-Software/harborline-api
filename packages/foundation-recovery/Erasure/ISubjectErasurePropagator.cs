using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// A downstream sink that must drop a crypto-shredded subject's <em>derived</em> residue when an erasure
/// takes effect (ADR 0135 GDPR direction; the Slice-1d F1 wire of the KG-search amendment #1369).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this seam exists (the F1 gap).</b> Crypto-shredding destroys the per-subject sub-key, so the
/// subject's DURABLE ciphertext-at-rest (every <see cref="EncryptedField"/>) becomes permanently
/// undecryptable after the stored subject key is deleted, without touching the ciphertext bytes
/// (<see cref="ISubjectErasureRegistry"/>). But some hosts maintain a <em>derived, partially-invertible
/// CLEARTEXT cache</em> of that ciphertext for performance — e.g. the KG-search <c>vec0</c> acceleration
/// table holds the cleartext binary-quantized embedding code. Crypto-shred alone leaves that cleartext
/// cache resident: the durable encrypted embedding goes dark, but the cleartext bit code in <c>vec0</c>
/// would survive until something explicitly purges it. An embedding is partially invertible, so a resident
/// cleartext code for an erased subject is a GDPR Art-17 residue — a real leak.
/// </para>
/// <para>
/// <b>The layering.</b> The erasure subsystem (this package) cannot depend on the app-tier index. So the
/// erasure service notifies registered propagators AFTER the shred is in effect (the registry is marked,
/// the tombstone is written, the erasure is audited) and the app registers a propagator that drops its
/// derived cleartext cache. The contract is: a propagator deletes only DERIVED residue keyed to the
/// subject — it NEVER decrypts the durable layer (it cannot — the sub-key is already gone) and it NEVER
/// touches another subject's data.
/// </para>
/// <para>
/// <b>Fail-loud, not fail-open.</b> The erasure has already taken legal effect by the time a propagator
/// runs — the durable boundary is closed. A propagator that throws does NOT un-erase the subject, but it
/// DOES signal that a derived cleartext cache may still hold residue, which a compliance host must surface.
/// The erasure service therefore lets a propagator fault PROPAGATE (it is not swallowed) so a host wiring a
/// derived-cache propagator learns immediately that the cache purge failed. Order the propagators so the
/// most security-critical cache (the cleartext one) is purged first.
/// </para>
/// </remarks>
public interface ISubjectErasurePropagator
{
    /// <summary>
    /// Drop the derived residue of a just-crypto-shredded subject. Called by
    /// <see cref="ISubjectErasureService.EraseAsync"/> ONCE per subject — only on the first-time erasure,
    /// never on the idempotent already-erased no-op. Must be idempotent itself (a re-run is a no-op) and
    /// must scope strictly to <paramref name="subject"/> within <paramref name="tenant"/>.
    /// </summary>
    /// <param name="tenant">The tenant the erased subject belongs to.</param>
    /// <param name="subject">The crypto-shredded data subject whose derived residue must be purged.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PropagateErasureAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default);
}
