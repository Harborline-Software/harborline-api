using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Foundation.Assets.Common;
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
/// <b>Unbound, it refuses.</b> The composed singleton is not bound to any change. Recording through it
/// throws, so a caller that forgets <see cref="Within"/> fails loudly instead of recording nothing.
/// </para>
/// </remarks>
public sealed class KernelAuditEnrollmentCompensatingControlRecorder : IEnrollmentCompensatingControlRecorder
{
    private readonly IOperationSigner _signer;
    private readonly TimeProvider _time;
    private readonly DbContext? _write;

    /// <summary>
    /// Construct over the node's operation signer (attributes the audit envelope to the node principal) and the
    /// clock that stamps each record. Bind it to a change with <see cref="Within"/> before recording.
    /// </summary>
    public KernelAuditEnrollmentCompensatingControlRecorder(IOperationSigner signer, TimeProvider time)
        : this(signer, time, write: null)
    {
    }

    private KernelAuditEnrollmentCompensatingControlRecorder(IOperationSigner signer, TimeProvider time, DbContext? write)
    {
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _write = write;
    }

    /// <inheritdoc />
    public IEnrollmentCompensatingControlRecorder Within(object write) => write is DbContext db
        ? new KernelAuditEnrollmentCompensatingControlRecorder(_signer, _time, db)
        : throw new ArgumentException("The enrollment audit joins an EF DbContext write.", nameof(write));

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
        await NodeAuditOutbox.StageSignedAsync(write, _signer, tenantId, eventType, _time.GetUtcNow(), body, ct)
            .ConfigureAwait(false);
    }
}
