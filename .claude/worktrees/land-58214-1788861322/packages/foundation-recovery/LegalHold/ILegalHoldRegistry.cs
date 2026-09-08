using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// The fail-closed pre-shred gate (ADR 0142 §D3). Consulted as a MANDATORY
/// pre-condition on <em>every</em> path that destroys a per-subject key — the
/// scheduled retention→shred path AND the manual GDPR crypto-shred path
/// (<see cref="ISubjectErasureService"/>). It is the mirror image of the erasure
/// registry, with the fail-closed polarity <b>inverted</b>: erasure fails closed by
/// blocking key <em>derivation</em>; the hold fails closed by blocking key
/// <em>destruction</em>.
/// </summary>
/// <remarks>
/// <b>Fail-closed = unreachable ⇒ held.</b> If the underlying store cannot be
/// consulted (down, query error), the gate returns <c>true</c> (held / refuse to
/// shred) — never "assume nothing held". Failing to shred an eligible record is
/// recoverable (shred later); shredding a record under legal hold is an irreversible
/// spoliation event, so every ambiguity resolves toward <em>not shredding</em>.
/// </remarks>
public interface ILegalHoldRegistry
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="heldRef"/> is under an active hold for
    /// <paramref name="tenant"/>, OR when the registry cannot be consulted
    /// (fail-closed). Never throws for a store fault — an unreachable registry answers
    /// "held".
    /// </summary>
    ValueTask<bool> IsHeldAsync(TenantId tenant, HeldRef heldRef, CancellationToken ct = default);

    /// <summary>
    /// Convenience for the crypto-shred path: is the data <paramref name="subject"/>
    /// under an active subject-scoped hold (or is the registry unreachable)? Equivalent
    /// to <see cref="IsHeldAsync"/> with <see cref="HeldRef.ForSubject"/>. Coarser
    /// record/class holds that encompass the subject are checked by the caller that
    /// owns the record→subject mapping (ADR 0142 §D3 / OQ1).
    /// </summary>
    ValueTask<bool> IsSubjectHeldAsync(TenantId tenant, SubjectId subject, CancellationToken ct = default);
}
