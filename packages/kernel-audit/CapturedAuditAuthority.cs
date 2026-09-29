using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;

namespace Harborline.Api.Kernel.Audit;

/// <summary>
/// DES-0029 kernel-core-ck-6: the two halves of an authorized append, split so the audit entry can commit in the
/// SAME transaction as the act it records (a durable outbox row) and reach the trail later, when the live
/// <see cref="AuthorizationDecision"/> no longer exists. <see cref="Capture"/> performs exactly the checks and
/// snapshot copy <see cref="IAuthorizedAuditTrail.AppendAuthorizedAsync"/> performs; the captured record is then
/// appended unchanged by <see cref="ICapturedAuditTrail.AppendCapturedAsync"/>.
/// </summary>
public static class CapturedAuditAuthority
{
    /// <summary>
    /// Captures the authority snapshot of the decision that allowed an act, checked against the header of the
    /// audit entry that will record it: the same checks <see cref="IAuthorizedAuditTrail.AppendAuthorizedAsync"/>
    /// makes, so a captured entry can never name an act other than the one decided.
    /// </summary>
    /// <exception cref="AuthorizedAuditRefusedException">The header and the decision disagree, or the decision is a denial.</exception>
    public static AuthoritySnapshot Capture(
        TenantId tenant,
        ActorId actor,
        DateTimeOffset occurredAt,
        AuthorizationTarget target,
        PermissionAtom act,
        AuthorizationDecision decision,
        SeparationOfDutyDecision? approval = null)
    {
        // The capture runs the one CopyFromDecision seam over the entry's header; the placeholder payload is never
        // signed, stored or appended, only the snapshot leaves this method.
        var header = new AuditRecord(
            Guid.Empty, tenant, new AuditEventType("authority-capture"), occurredAt,
            new SignedOperation<AuditPayload>(new AuditPayload(new Dictionary<string, object?>()), default, occurredAt, Guid.Empty, default),
            [], Actor: actor, Target: target, Act: act);
        return AuthorizedAuditRecord.CopyFromDecision(header, decision, approval).AuthoritySnapshot!;
    }
}

/// <summary>Appends a record whose authority was captured while its decision was live (see <see cref="CapturedAuditAuthority"/>).</summary>
/// <remarks>
/// The captured snapshot is trusted as stored. A host persists it in the same encrypted store, and the same
/// transaction, as the authorization state the act changed, so the outbox confers no power beyond write access
/// to that store.
/// </remarks>
public interface ICapturedAuditTrail
{
    /// <summary>Appends <paramref name="captured"/> as-is; refuses a record that carries no authority snapshot.</summary>
    ValueTask AppendCapturedAsync(AuditRecord captured, CancellationToken ct = default);
}
