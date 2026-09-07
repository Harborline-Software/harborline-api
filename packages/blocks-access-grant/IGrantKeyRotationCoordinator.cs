using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>
/// The host-implemented seam that EXECUTES forward-cutoff key-rotation when a grant is revoked (ADR 0117
/// amendment A4 — the security-engineering deep-review F-2 follow-on). Revocation drops the grant
/// (<c>RevokedAt</c> stamped, so the resolver denies NEW access immediately), and this coordinator rotates
/// the affected key so a principal whose grant was revoked cannot read data encrypted AFTER the cutoff even
/// if it later compromises the pre-rotation key — closing the forward-secrecy gap v1 previously only FLAGGED.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a host seam, not a direct dependency.</b> The real rotation primitive is
/// <c>Harborline.Api.Foundation.SickBay.IKeyRotationScheduler.ScheduleAsync(tenant, fieldPurpose, triggerReason)</c>
/// (ADR 0068 <c>KeyRotationTrigger.RoleChange</c> + ADR 0118 D4 + ADR 0046-A6 §A6.1), which lives behind the
/// heavyweight <c>foundation-sick-bay</c> dependency chain (recovery / mission-space / kernel-audit). The
/// grant Module composes that substrate WITHOUT pulling its dependency graph in — exactly the host-seam
/// pattern this Module already uses for the store-write effects (<see cref="IGrantIssuanceContext"/> /
/// <see cref="IGrantRevocationContext"/>). The host implements this over the real
/// <c>IKeyRotationScheduler</c>, passing <c>KeyRotationTrigger.RoleChange</c> as the trigger reason.
/// </para>
/// <para>
/// <b>Consumed by the host's <see cref="IGrantRevocationContext"/>, not the handler.</b> The host owns the
/// concrete unit of work (the EF <c>DbContext</c>), so it is the host's revocation context that stages BOTH
/// the <c>RevokedAt</c> store stamp AND this rotation onto that one unit of work — keeping the
/// <see cref="GrantRevocationHandler"/> store-and-side-effect-wiring-free (the same discipline the issuance
/// handler follows). The handler only decides the outcome + supplies the affected roles.
/// </para>
/// <para>
/// <b>The named exception (A4) still stands.</b> Rotation is a FORWARD cutoff — it does not retroactively
/// re-encrypt or remote-wipe. For a <c>residency: Cache</c> grantee, plaintext already cached on the device
/// stays readable after revoke; that bounded residency-cache window is A4's explicitly-named exception, NOT
/// closed by rotation. What rotation adds over the v1 flag-only behaviour is forward-secrecy against a future
/// compromise of the (now-rotated-away) key.
/// </para>
/// <para>
/// <b>Idempotent + atomic.</b> The host context stages rotation onto the revoke step's atomic advance, keyed
/// by the same step idempotency source-reference as the <c>RevokedAt</c> stamp, so a crash-resume /
/// redelivered approve re-schedules at most once (the engine's idempotency row gates re-entry). A coordinator
/// implementation over <c>IKeyRotationScheduler</c> SHOULD itself be safe to call more than once for the same
/// <see cref="GrantRevocationRequest"/> (scheduling a rotation that is already pending is a no-op upstream).
/// </para>
/// </remarks>
public interface IGrantKeyRotationCoordinator
{
    /// <summary>
    /// Executes (schedules) forward-cutoff key-rotation for the keys covering the roles the revoked grant
    /// conferred, in the granting tenant. Called inside the revoke step's atomic advance on approve.
    /// </summary>
    /// <param name="request">The revocation being applied (carries the grant id, tenant, and revoker).</param>
    /// <param name="revokedRoles">
    /// The roles the revoked grant conferred — the affected key purposes to rotate (the host maps each role
    /// to its field-purpose / key when calling the scheduler). Empty for an inert grant ⇒ nothing to rotate.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task ExecuteRotationOnRevokeAsync(
        GrantRevocationRequest request,
        IReadOnlyList<RoleReference> revokedRoles,
        CancellationToken ct = default);
}
