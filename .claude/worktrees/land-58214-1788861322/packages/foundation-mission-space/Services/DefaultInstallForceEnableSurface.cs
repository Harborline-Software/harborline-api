using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.MissionSpace.Audit;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Foundation.MissionSpace;

/// <summary>
/// Reference <see cref="IInstallForceEnableSurface"/> per ADR 0063-A1.11.
/// Audit-emission shape mirrors W#40's <c>FeatureForceEnabled</c> per
/// Phase 3 halt-condition #5 (parity with the W#40 force-enable surface).
/// </summary>
public sealed class DefaultInstallForceEnableSurface : IInstallForceEnableSurface
{
    private readonly TimeProvider _time;
    private readonly IAuditTrail? _auditTrail;
    private readonly IOperationSigner? _signer;
    private readonly TenantId _tenantId;

    /// <summary>Audit-disabled overload (test / bootstrap).</summary>
    public DefaultInstallForceEnableSurface(TimeProvider? time = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Audit-enabled overload — W#32 both-or-neither contract.</summary>
    public DefaultInstallForceEnableSurface(
        IAuditTrail auditTrail,
        IOperationSigner signer,
        TenantId tenantId,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(auditTrail);
        ArgumentNullException.ThrowIfNull(signer);
        if (tenantId == default)
        {
            throw new ArgumentException("tenantId is required when audit emission is wired.", nameof(tenantId));
        }
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _auditTrail = auditTrail;
        _signer = signer;
        _tenantId = tenantId;
    }

    /// <inheritdoc />
    public async ValueTask<InstallForceRecord> RequestAsync(InstallForceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.OperatorPrincipalId);
        ArgumentException.ThrowIfNullOrEmpty(request.Reason);
        ArgumentException.ThrowIfNullOrEmpty(request.EnvelopeHash);
        if (request.OverrideTargets.Count == 0)
        {
            throw new ArgumentException("OverrideTargets must contain at least one dimension.", nameof(request));
        }
        ct.ThrowIfCancellationRequested();

        var record = new InstallForceRecord
        {
            OperatorPrincipalId = request.OperatorPrincipalId,
            Reason = request.Reason,
            OverrideTargets = request.OverrideTargets,
            EnvelopeHash = request.EnvelopeHash,
            Platform = request.Platform,
            RecordedAt = _time.GetUtcNow(),
        };

        if (_auditTrail is not null && _signer is not null)
        {
            var payload = MissionSpaceAuditPayloads.InstallForceEnabled(
                record.OperatorPrincipalId,
                record.Reason,
                record.OverrideTargets.Select(d => d.ToString()).ToArray(),
                record.EnvelopeHash,
                record.Platform);
            await EmitAsync(payload, record.RecordedAt, ct).ConfigureAwait(false);
        }

        return record;
    }

    private async Task EmitAsync(AuditPayload payload, DateTimeOffset occurredAt, CancellationToken ct)
    {
        var signed = await _signer!.SignAsync(payload, occurredAt, Guid.NewGuid(), ct).ConfigureAwait(false);
        var auditRecord = new AuditRecord(
            AuditId: Guid.NewGuid(),
            TenantId: _tenantId,
            EventType: AuditEventType.InstallForceEnabled,
            OccurredAt: occurredAt,
            Payload: signed,
            AttestingSignatures: ImmutableArray<AttestingSignature>.Empty);
        await _auditTrail!.AppendAsync(auditRecord, ct).ConfigureAwait(false);
    }
}
