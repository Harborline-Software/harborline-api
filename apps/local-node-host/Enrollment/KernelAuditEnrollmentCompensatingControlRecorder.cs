using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Audit.Payloads;

namespace Harborline.Api.LocalNodeHost.Enrollment;

/// <summary>
/// The host-side ADAPTER that wires the enrollment layer's <see cref="IEnrollmentCompensatingControlRecorder"/> seam to the unified
/// kernel audit trail (<see cref="IAuditTrail"/>) — enrollment Phase C compensating control #1, the "second
/// set of eyes" (<c>project_sod_compensating_controls</c>). It signs each duty-significant enrollment operation
/// (member admit/revoke, permission grant, ownership transfer) and appends it to the SAME append-only,
/// tamper-evident, undeletable trail the financial posts use — one queryable surface, gated by
/// <c>audit:read</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives at the host, not in foundation-identity-atlas.</b> <see cref="IAuditTrail"/> lives in
/// <c>kernel-audit</c> (refs kernel-runtime). <c>foundation-identity-atlas</c> is deliberately kept kernel-free
/// (cerebrum [2026-06-20]), so it owns the <see cref="IEnrollmentCompensatingControlRecorder"/> CONTRACT and the host owns this ADAPTER
/// — the only place that references both. Same seam-first split the roster's trust-snapshot <c>Func</c> and the
/// pure <see cref="AdmissionCoordinator"/> use.
/// </para>
/// <para>
/// <b>Fail-safe-but-LOUD (not fail-blocking, not fail-SILENT).</b> The roster mutation is already attested +
/// persisted by the time this is called; recording is an additive side-channel. An
/// <see cref="IAuditTrail.AppendAsync"/> fault MUST NOT brick an enrollment operation, so each record call
/// swallows the exception rather than throwing back into the admit/revoke path. But a swallowed fault is NOT
/// silent: it is routed to <see cref="_onFault"/> — which the shipping host wires to a WARN/degraded-mode
/// signal (#1295 F2). An unauditable enrollment control op is therefore DETECTABLE (a "second set of eyes" that records
/// nothing without raising an alarm is no control at all). The fault callback receives the
/// <see cref="AuditEventType"/> so the host can escalate the highest-stakes op
/// (<see cref="AuditEventType.OwnershipTransferred"/> — the root-grant moving) above a plain warning.
/// </para>
/// </remarks>
public sealed class KernelAuditEnrollmentCompensatingControlRecorder : IEnrollmentCompensatingControlRecorder
{
    private readonly IAuditTrail _trail;
    private readonly IOperationSigner _signer;
    private readonly TimeProvider _time;
    private readonly Action<AuditEventType, Exception>? _onFault;

    /// <summary>
    /// Construct over the unified audit trail + the node's operation signer (attributes the audit envelope to
    /// the node principal). <paramref name="onFault"/> receives the <see cref="AuditEventType"/> of the
    /// unauditable op and the append fault so the host can emit a LOUD signal (WARN / degraded-mode; a stronger
    /// alarm for <see cref="AuditEventType.OwnershipTransferred"/>) WITHOUT the fault propagating into the
    /// enrollment operation. A shipping host MUST wire this (#1295 F2 — fail-safe-but-loud); leaving it null
    /// makes the sink fail-SILENT, which defeats the compensating control.
    /// </summary>
    public KernelAuditEnrollmentCompensatingControlRecorder(
        IAuditTrail trail,
        IOperationSigner signer,
        TimeProvider? time = null,
        Action<AuditEventType, Exception>? onFault = null)
    {
        _trail = trail ?? throw new ArgumentNullException(nameof(trail));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _onFault = onFault;
    }

    /// <inheritdoc />
    public ValueTask RecordMemberAdmittedAsync(
        TenantId tenantId, string teamId, string admitterPartyId, string admittedPartyId,
        string admittedPublicKeyBase64Url, IReadOnlyList<string> grantedPermissions, string admissionMode,
        string? correlationId = null, CancellationToken ct = default)
    {
        var payload = new EnrollmentCompensatingControlPayloads.MemberAdmittedPayload(
            tenantId, teamId, admitterPartyId, admittedPartyId, admittedPublicKeyBase64Url,
            grantedPermissions, admissionMode, correlationId);
        return EmitAsync(tenantId, AuditEventType.MemberAdmitted, payload.ToBody(), _time.GetUtcNow(), ct);
    }

    /// <inheritdoc />
    public ValueTask RecordMemberRevokedAsync(
        TenantId tenantId, string teamId, string revokerPartyId, string revokedPartyId,
        string? correlationId = null, CancellationToken ct = default)
    {
        var payload = new EnrollmentCompensatingControlPayloads.MemberRevokedPayload(
            tenantId, teamId, revokerPartyId, revokedPartyId, correlationId);
        return EmitAsync(tenantId, AuditEventType.MemberRevoked, payload.ToBody(), _time.GetUtcNow(), ct);
    }

    /// <inheritdoc />
    public ValueTask RecordPermissionsGrantedAsync(
        TenantId tenantId, string teamId, string granterPartyId, string targetPartyId,
        IReadOnlyList<string> resultingPermissions, string? correlationId = null,
        CancellationToken ct = default)
    {
        var payload = new EnrollmentCompensatingControlPayloads.PermissionsGrantedPayload(
            tenantId, teamId, granterPartyId, targetPartyId, resultingPermissions, correlationId);
        return EmitAsync(tenantId, AuditEventType.PermissionsGranted, payload.ToBody(), _time.GetUtcNow(), ct);
    }

    /// <inheritdoc />
    public ValueTask RecordOwnershipTransferredAsync(
        TenantId tenantId, string teamId, string fromPartyId, string toPartyId,
        string? correlationId = null, CancellationToken ct = default)
    {
        var payload = new EnrollmentCompensatingControlPayloads.OwnershipTransferredPayload(
            tenantId, teamId, fromPartyId, toPartyId, correlationId);
        return EmitAsync(tenantId, AuditEventType.OwnershipTransferred, payload.ToBody(), _time.GetUtcNow(), ct);
    }

    private async ValueTask EmitAsync(
        TenantId tenantId, AuditEventType eventType, IReadOnlyDictionary<string, object?> body,
        DateTimeOffset occurredAt, CancellationToken ct)
    {
        try
        {
            var signed = await _signer
                .SignAsync(new AuditPayload(body), occurredAt, Guid.NewGuid(), ct)
                .ConfigureAwait(false);
            await _trail.AppendAsync(
                new AuditRecord(
                    AuditId: Guid.NewGuid(),
                    TenantId: tenantId,
                    EventType: eventType,
                    OccurredAt: occurredAt,
                    Payload: signed,
                    AttestingSignatures: ImmutableArray<AttestingSignature>.Empty),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-safe-but-LOUD (#1295 F2): a recording fault must not brick the enrollment op, but it must
            // NOT be silent either — route it to the host's loud signal (WARN/degraded-mode; stronger for
            // OwnershipTransferred) carrying the event type so the host can escalate the high-stakes op. A null
            // callback would make this fail-SILENT (the pre-fix posture) — the shipping host always wires it.
            _onFault?.Invoke(eventType, ex);
        }
    }
}
