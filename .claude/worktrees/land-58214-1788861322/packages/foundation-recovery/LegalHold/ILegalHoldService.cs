using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// The place/release lifecycle for legal holds (ADR 0142 §D2), with the SoD polarity
/// <b>inverted</b> versus the erasure floor: <em>placing</em> a hold is conservative
/// and low-privilege (a single authorized actor, because over-holding is the safe
/// failure direction and a litigation hold must land fast), while <em>releasing</em>
/// a hold is the dangerous, guarded action (≥2 distinct approvers, because release
/// re-exposes the held subject to crypto-shred). Both transitions are mandatorily
/// audited.
/// </summary>
public interface ILegalHoldService
{
    /// <summary>
    /// Place a hold. Appends the hold and emits a
    /// <see cref="Harborline.Api.Kernel.Audit.AuditEventType.LegalHoldPlaced"/> record.
    /// Single-actor by design (no multi-approver gate on placement).
    /// </summary>
    Task<LegalHoldEntry> PlaceAsync(LegalHoldPlaceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Release a hold. Enforces the ≥2-distinct-approver floor (ADR 0068 §3.1),
    /// verifies the hold exists and is not already released, appends the release, and
    /// emits a <see cref="Harborline.Api.Kernel.Audit.AuditEventType.LegalHoldReleased"/>
    /// record.
    /// </summary>
    /// <exception cref="LegalHoldReleaseRejectedException">
    /// The approval floor was not met, the hold does not exist, or it is already released.
    /// Fail-closed: a rejected release leaves the hold ACTIVE.
    /// </exception>
    Task<LegalHoldRelease> ReleaseAsync(LegalHoldReleaseRequest request, CancellationToken ct = default);
}
