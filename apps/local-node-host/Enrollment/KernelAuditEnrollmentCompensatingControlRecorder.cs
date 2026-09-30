using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Audit.Payloads;
using Harborline.Api.LocalNodeHost.Data.Audit;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// The host-side ADAPTER that wires the enrollment layer's <see cref="IEnrollmentCompensatingControlRecorder"/> seam to the unified
/// kernel audit trail (<see cref="IAuditTrail"/>) — enrollment Phase C compensating control #1, the "second
/// set of eyes" (<c>project_sod_compensating_controls</c>). It signs each duty-significant enrollment operation
/// (member admit/revoke, permission grant, ownership transfer) into the SAME append-only, tamper-evident,
/// undeletable trail the financial posts use — one queryable surface, gated by <c>audit:read</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives at the host, not in foundation-identity-atlas.</b> <see cref="IAuditTrail"/> lives in
/// <c>kernel-audit</c> (refs kernel-runtime). <c>foundation-identity-atlas</c> is deliberately kept kernel-free
/// (cerebrum [2026-06-20]), so it owns the <see cref="IEnrollmentCompensatingControlRecorder"/> CONTRACT and the host owns this ADAPTER
/// — the only place that references both.
/// </para>
/// <para>
/// <b>Durable with the change (T-986, owner ruling 2026-09-29).</b> The recorder signs the audit envelope and
/// stages it, as a <see cref="NodeAuditOutbox"/> entry, on the <see cref="DbContext"/> that commits the roster
/// record (<see cref="Within"/>), so the envelope is in <c>local-node.db</c> the moment the change commits and
/// never without it. The outbox then delivers it to the trail; delivery is not durability. A signing or staging
/// fault throws into the change, which then does not commit: an enrollment change cannot land unaudited. This
/// replaces the earlier fail-safe-but-loud append after the change, which a restart or an append fault lost.
/// </para>
/// <para>
/// <b>Bound with the admitting decision (T-1000).</b> A change a request decision allowed (a roster revocation)
/// binds with <see cref="AuthorizedEnrollmentWrite"/>: the entry is stamped at the decided instant, carries the
/// decision's actor, target, act and captured authority, and carries the change's reason.
/// </para>
/// <para>
/// <b>Unbound, it refuses.</b> The composed singleton is not bound to any change. Recording through it
/// throws, so a caller that forgets <see cref="Within"/> fails loudly instead of recording nothing.
/// </para>
/// </remarks>
public sealed class KernelAuditEnrollmentCompensatingControlRecorder : IEnrollmentCompensatingControlRecorder
{
    private readonly IOperationSigner _signer;
    private readonly TimeProvider _time;
    private readonly DbContext? _write;
    private readonly AuthorizedEnrollmentWrite? _authorized;

    /// <summary>
    /// Construct over the node's operation signer (attributes the audit envelope to the node principal) and the
    /// clock that stamps each record. Bind it to a change with <see cref="Within"/> before recording.
    /// </summary>
    public KernelAuditEnrollmentCompensatingControlRecorder(IOperationSigner signer, TimeProvider time)
        : this(signer, time, write: null, authorized: null)
    {
    }

    private KernelAuditEnrollmentCompensatingControlRecorder(
        IOperationSigner signer, TimeProvider time, DbContext? write, AuthorizedEnrollmentWrite? authorized)
    {
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _write = write;
        _authorized = authorized;
    }

    /// <summary>
    /// This recorder, bound to <paramref name="write"/>: a <see cref="DbContext"/> whose one save commits the
    /// change, or an <see cref="AuthorizedEnrollmentWrite"/> that adds the decision that allowed it. What the
    /// bound recorder records is staged on that context and commits with the change or not at all.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="write"/> is neither.</exception>
    public IEnrollmentCompensatingControlRecorder Within(object write) => write switch
    {
        AuthorizedEnrollmentWrite authorized => new KernelAuditEnrollmentCompensatingControlRecorder(
            _signer, _time, authorized.Write, authorized),
        DbContext db => new KernelAuditEnrollmentCompensatingControlRecorder(_signer, _time, db, authorized: null),
        _ => throw new ArgumentException("The enrollment audit joins an EF DbContext write.", nameof(write)),
    };

    /// <inheritdoc />
    public ValueTask RecordMemberAdmittedAsync(
        TenantId tenantId, string teamId, string admitterPartyId, string admittedPartyId,
        string admittedPublicKeyBase64Url, IReadOnlyList<string> grantedPermissions, string admissionMode,
        string? correlationId = null, CancellationToken ct = default)
    {
        var payload = new EnrollmentCompensatingControlPayloads.MemberAdmittedPayload(
            tenantId, teamId, admitterPartyId, admittedPartyId, admittedPublicKeyBase64Url,
            grantedPermissions, admissionMode, correlationId);
        return StageAsync(tenantId, AuditEventType.MemberAdmitted, payload.ToBody(), ct);
    }

    /// <inheritdoc />
    public ValueTask RecordMemberRevokedAsync(
        TenantId tenantId, string teamId, string revokerPartyId, string revokedPartyId,
        string? correlationId = null, CancellationToken ct = default)
    {
        var payload = new EnrollmentCompensatingControlPayloads.MemberRevokedPayload(
            tenantId, teamId, revokerPartyId, revokedPartyId, correlationId);
        return StageAsync(tenantId, AuditEventType.MemberRevoked, payload.ToBody(), ct);
    }

    /// <inheritdoc />
    public ValueTask RecordPermissionsGrantedAsync(
        TenantId tenantId, string teamId, string granterPartyId, string targetPartyId,
        IReadOnlyList<string> resultingPermissions, string? correlationId = null,
        CancellationToken ct = default)
    {
        var payload = new EnrollmentCompensatingControlPayloads.PermissionsGrantedPayload(
            tenantId, teamId, granterPartyId, targetPartyId, resultingPermissions, correlationId);
        return StageAsync(tenantId, AuditEventType.PermissionsGranted, payload.ToBody(), ct);
    }

    /// <inheritdoc />
    public ValueTask RecordOwnershipTransferredAsync(
        TenantId tenantId, string teamId, string fromPartyId, string toPartyId,
        string? correlationId = null, CancellationToken ct = default)
    {
        var payload = new EnrollmentCompensatingControlPayloads.OwnershipTransferredPayload(
            tenantId, teamId, fromPartyId, toPartyId, correlationId);
        return StageAsync(tenantId, AuditEventType.OwnershipTransferred, payload.ToBody(), ct);
    }

    private async ValueTask StageAsync(
        TenantId tenantId, AuditEventType eventType, IReadOnlyDictionary<string, object?> body, CancellationToken ct)
    {
        var write = _write ?? throw new InvalidOperationException(
            $"The enrollment audit of {eventType.Value} must join the change it records: bind the recorder with Within.");
        if (_authorized is not { } authorized)
        {
            await NodeAuditOutbox.StageSignedAsync(write, _signer, tenantId, eventType, _time.GetUtcNow(), body, ct: ct)
                .ConfigureAwait(false);
            return;
        }

        // The act read the clock once, at its decision (ticket 216); the entry records that instant.
        var entry = new Dictionary<string, object?>(body, StringComparer.Ordinal);
        if (authorized.Reason is { } reason) entry["reason"] = reason;
        await NodeAuditOutbox.StageSignedAsync(
                write, _signer, tenantId, eventType, authorized.Decision.DecidedAt, entry, authorized.Decision, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// T-1000: the write an enrollment recorder binds to when a request decision allowed the change: the context whose
/// one save commits the roster record, the decision, and the change's reason. Passed to
/// <see cref="IEnrollmentCompensatingControlRecorder.Within"/>.
/// </summary>
/// <param name="Write">The context whose one save commits the change and its staged audit entry.</param>
/// <param name="Decision">The allowed decision; the entry records its instant, actor, target, act and authority.</param>
/// <param name="Reason">A stable, log-safe reason code stored in the entry body as <c>reason</c>; none when null.</param>
public sealed record AuthorizedEnrollmentWrite(DbContext Write, AuthorizationDecision Decision, string? Reason = null);
