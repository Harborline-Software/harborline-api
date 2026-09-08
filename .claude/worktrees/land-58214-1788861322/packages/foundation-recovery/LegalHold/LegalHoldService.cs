using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Foundation.Recovery.LegalHold;

/// <summary>
/// Reference <see cref="ILegalHoldService"/> (ADR 0142 §D2 / §D6). Places holds
/// single-actor, releases them behind the ADR 0068 §3.1 ≥2-distinct-approver floor,
/// and emits a MANDATORY audit record for each transition — an unaudited hold change
/// is not permitted, so the constructor requires both an <see cref="IAuditTrail"/>
/// and an <see cref="IOperationSigner"/> (mirroring the erasure service).
/// </summary>
public sealed class LegalHoldService : ILegalHoldService
{
    /// <summary>The ADR 0068 §3.1 approval floor for a RELEASE: at least this many DISTINCT approvers.</summary>
    public const int MinimumReleaseApprovers = 2;

    private readonly ILegalHoldStore _store;
    private readonly IAuditTrail _auditTrail;
    private readonly IOperationSigner _signer;
    private readonly IRecoveryClock _clock;

    /// <param name="store">The append-only hold store.</param>
    /// <param name="auditTrail">Audit trail — hold place/release emission is mandatory.</param>
    /// <param name="signer">Operation signer for the audit payload.</param>
    /// <param name="clock">Clock; defaults to the system clock.</param>
    public LegalHoldService(
        ILegalHoldStore store,
        IAuditTrail auditTrail,
        IOperationSigner signer,
        IRecoveryClock clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _auditTrail = auditTrail ?? throw new ArgumentNullException(nameof(auditTrail));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async Task<LegalHoldEntry> PlaceAsync(LegalHoldPlaceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Matter))
        {
            throw new ArgumentException("A legal hold requires a non-empty matter.", nameof(request));
        }

        var entry = new LegalHoldEntry(
            HoldId: LegalHoldId.New(),
            TenantId: request.Tenant,
            HeldRef: request.HeldRef,
            Matter: request.Matter,
            PlacedBy: request.PlacedBy,
            PlacedAtUtc: _clock.UtcNow());

        await _store.AppendHoldAsync(entry, ct).ConfigureAwait(false);
        await EmitAsync(AuditEventType.LegalHoldPlaced, LegalHoldAuditPayloadFactory.Placed(entry),
            entry.TenantId, entry.PlacedAtUtc, ct).ConfigureAwait(false);
        return entry;
    }

    /// <inheritdoc />
    public async Task<LegalHoldRelease> ReleaseAsync(LegalHoldReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // --- Gate: multi-actor approval floor (ADR 0068 §3.1). ---
        // ≥2 DISTINCT approvers; a null / empty / single-actor / all-same-actor chain
        // fails closed, leaving the hold ACTIVE.
        var approvers = request.Approvers ?? Array.Empty<ActorId>();
        var distinct = approvers.Select(a => a.Value).Distinct(StringComparer.Ordinal).Count();
        if (distinct < MinimumReleaseApprovers)
        {
            throw new LegalHoldReleaseRejectedException("approval floor not met");
        }

        var hold = await _store.FindHoldAsync(request.Tenant, request.HoldId, ct).ConfigureAwait(false);
        if (hold is null)
        {
            throw new LegalHoldReleaseRejectedException("hold not found");
        }
        if (await _store.IsReleasedAsync(request.Tenant, request.HoldId, ct).ConfigureAwait(false))
        {
            throw new LegalHoldReleaseRejectedException("already released");
        }

        var release = new LegalHoldRelease(
            HoldId: request.HoldId,
            TenantId: request.Tenant,
            Approvers: approvers.ToImmutableArray(),
            Reason: request.Reason ?? string.Empty,
            ReleasedAtUtc: _clock.UtcNow());

        await _store.AppendReleaseAsync(release, ct).ConfigureAwait(false);
        await EmitAsync(AuditEventType.LegalHoldReleased, LegalHoldAuditPayloadFactory.Released(release),
            release.TenantId, release.ReleasedAtUtc, ct).ConfigureAwait(false);
        return release;
    }

    private async Task EmitAsync(
        AuditEventType eventType, AuditPayload payload, TenantId tenant, DateTimeOffset occurredAt, CancellationToken ct)
    {
        var signed = await _signer.SignAsync(payload, occurredAt, Guid.NewGuid(), ct).ConfigureAwait(false);
        var record = new AuditRecord(
            AuditId: Guid.NewGuid(),
            TenantId: tenant,
            EventType: eventType,
            OccurredAt: occurredAt,
            Payload: signed,
            AttestingSignatures: ImmutableArray<AttestingSignature>.Empty);
        await _auditTrail.AppendAsync(record, ct).ConfigureAwait(false);
    }
}
