using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.IdentityAtlas.Enrollment;

/// <summary>
/// The compensating-control RECORDER for enrollment (Phase C) — the thin port through which the
/// duty-significant ENROLLMENT operations (member admit/revoke, permission grant, ownership transfer) are
/// recorded into the platform's immutable audit trail. It <b>records</b>; it decides nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is, and what it is not (ticket 272 slice 4).</b> ADR 0067 clause 2 replaces separation of duty
/// as a construction constraint with a check "at assignment, with a recorded override": a hard block at
/// composition definition, a soft block that may be allowed "with a mandatory recorded reason, logged where
/// an auditor reads it", and a batch revalidation. Deciding that conflict is the job of
/// <c>Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyEngine</c>, the one place
/// clause 2 is evaluated. THIS type is the other half of the sentence — the log an auditor reads. It
/// evaluates no rule, compares no duties and produces no verdict; it writes what an enrollment operation
/// did, so a lone operator's roster changes are reviewable after the fact. It was once named for separation
/// of duty, which read as if it decided one.
/// </para>
/// <para>
/// <b>Why a seam and not a direct dependency (the load-bearing architecture point).</b> The unified audit
/// trail (<c>Harborline.Api.Kernel.Audit.IAuditTrail</c>) lives in <c>kernel-audit</c>, which references
/// <c>kernel</c>/<c>kernel-event-bus</c> (kernel-runtime). <c>foundation-identity-atlas</c> is DELIBERATELY
/// kept free of kernel-runtime (cerebrum [2026-06-20] — "all PBAC + roster types in foundation-identity-atlas:
/// cycle-safe, refs foundation Crypto, NOT kernel-runtime"). So the enrollment layer owns this CONTRACT, and
/// the <c>kernel-audit</c>-backed adapter (which signs + appends to <c>IAuditTrail</c>) lives at the host
/// composition root that already references both. Same pattern the roster's <c>Func</c>-snapshot trust
/// provider and the pure <c>AdmissionCoordinator</c> use — seam-first, kernel-free at this layer.
/// </para>
/// <para>
/// <b>The "second set of eyes" compensating control.</b> A lone (or too-few-people) operator cannot divide
/// the work, so the SYSTEM is the second set of eyes: every duty-significant membership / permission change is
/// recorded in a tamper-evident, signed, append-only, undeletable log a reviewer can read (gated by
/// <c>audit:read</c>). The enrollment roster is itself a genesis-rooted signed log; this seam makes its
/// mutations ALSO land in the same kernel audit trail the financial posts use, so one queryable surface shows
/// membership changes alongside financial activity.
/// </para>
/// <para>
/// <b>Fail-safe, not fail-blocking.</b> Recording is a side-channel: a recording fault MUST NOT brick an
/// enrollment operation (the roster mutation itself is already attested + persisted). Implementations should
/// surface faults via their own logging/telemetry; the default <see cref="NullEnrollmentCompensatingControlRecorder"/> is a no-op so a
/// host with no audit wiring still functions (single-user self-setup, tests). A production host wires the
/// kernel-audit-backed adapter.
/// </para>
/// </remarks>
public interface IEnrollmentCompensatingControlRecorder
{
    /// <summary>Record that a member was admitted to the roster (the admit-flow). duty-significant.</summary>
    /// <param name="tenantId">The tenant the roster mutation is scoped to.</param>
    /// <param name="teamId">The team (roster) the member joined (string form of the team Guid).</param>
    /// <param name="admitterPartyId">The admin who signed the admission.</param>
    /// <param name="admittedPartyId">The party admitted.</param>
    /// <param name="admittedPublicKeyBase64Url">The admitted member's bound public key (base64url; non-secret).</param>
    /// <param name="grantedPermissions">The permission strings the admitted member received.</param>
    /// <param name="admissionMode">The admission channel (e.g. <c>proximity</c>, <c>invite</c>).</param>
    /// <param name="correlationId">Optional correlation-id from the originating request.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RecordMemberAdmittedAsync(
        TenantId tenantId,
        string teamId,
        string admitterPartyId,
        string admittedPartyId,
        string admittedPublicKeyBase64Url,
        IReadOnlyList<string> grantedPermissions,
        string admissionMode,
        string? correlationId = null,
        CancellationToken ct = default);

    /// <summary>Record that a member was revoked from the roster's live state. duty-significant.</summary>
    /// <param name="tenantId">The tenant the roster mutation is scoped to.</param>
    /// <param name="teamId">The team (roster) the member was revoked from.</param>
    /// <param name="revokerPartyId">The admin who revoked.</param>
    /// <param name="revokedPartyId">The party removed from live state.</param>
    /// <param name="correlationId">Optional correlation-id from the originating request.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RecordMemberRevokedAsync(
        TenantId tenantId,
        string teamId,
        string revokerPartyId,
        string revokedPartyId,
        string? correlationId = null,
        CancellationToken ct = default);

    /// <summary>Record a fine-grained permission grant/change on a member's roster edge. duty-significant.</summary>
    /// <param name="tenantId">The tenant the roster mutation is scoped to.</param>
    /// <param name="teamId">The team (roster) the grant occurred in.</param>
    /// <param name="granterPartyId">The member who granted.</param>
    /// <param name="targetPartyId">The member whose permission set changed.</param>
    /// <param name="resultingPermissions">The target's permission strings AFTER the grant.</param>
    /// <param name="correlationId">Optional correlation-id from the originating request.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RecordPermissionsGrantedAsync(
        TenantId tenantId,
        string teamId,
        string granterPartyId,
        string targetPartyId,
        IReadOnlyList<string> resultingPermissions,
        string? correlationId = null,
        CancellationToken ct = default);

    /// <summary>Record a transfer of root-grant holdership (the no-bricking-floor satisfier). duty-significant.</summary>
    /// <param name="tenantId">The tenant the roster mutation is scoped to.</param>
    /// <param name="teamId">The team (roster) the transfer occurred in.</param>
    /// <param name="fromPartyId">The party that held the root grant before.</param>
    /// <param name="toPartyId">The party that holds the root grant after.</param>
    /// <param name="correlationId">Optional correlation-id from the originating request.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RecordOwnershipTransferredAsync(
        TenantId tenantId,
        string teamId,
        string fromPartyId,
        string toPartyId,
        string? correlationId = null,
        CancellationToken ct = default);
}

/// <summary>
/// No-op <see cref="IEnrollmentCompensatingControlRecorder"/> — the bundled default for hosts with no audit wiring (single-user
/// self-setup, tests). Consistent with the <c>Null*</c> defaults already in this package
/// (<c>NullKeyStore</c>, <c>NullTeamRegistry</c>, <c>NullTrusteeRegistry</c>). A production host wires the
/// kernel-audit-backed adapter instead.
/// </summary>
public sealed class NullEnrollmentCompensatingControlRecorder : IEnrollmentCompensatingControlRecorder
{
    /// <summary>The shared singleton no-op instance.</summary>
    public static readonly NullEnrollmentCompensatingControlRecorder Instance = new();

    /// <inheritdoc />
    public ValueTask RecordMemberAdmittedAsync(
        TenantId tenantId, string teamId, string admitterPartyId, string admittedPartyId,
        string admittedPublicKeyBase64Url, IReadOnlyList<string> grantedPermissions, string admissionMode,
        string? correlationId = null, CancellationToken ct = default) => default;

    /// <inheritdoc />
    public ValueTask RecordMemberRevokedAsync(
        TenantId tenantId, string teamId, string revokerPartyId, string revokedPartyId,
        string? correlationId = null, CancellationToken ct = default) => default;

    /// <inheritdoc />
    public ValueTask RecordPermissionsGrantedAsync(
        TenantId tenantId, string teamId, string granterPartyId, string targetPartyId,
        IReadOnlyList<string> resultingPermissions, string? correlationId = null,
        CancellationToken ct = default) => default;

    /// <inheritdoc />
    public ValueTask RecordOwnershipTransferredAsync(
        TenantId tenantId, string teamId, string fromPartyId, string toPartyId,
        string? correlationId = null, CancellationToken ct = default) => default;
}
