using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Recovery.Audit;
using Harborline.Api.Foundation.Recovery.LegalHold;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// Reference <see cref="ISubjectErasureService"/> — the manual, legal-sign-off +
/// multi-actor + mandatory-minimum-window crypto-shred path (ADR 0135 GDPR
/// direction; ADR 0068 §1.3 discipline). Audit emission is MANDATORY for this
/// service (unlike the field decryptor's optional audit) — an unaudited erasure is
/// not permitted, so the constructor requires both an <see cref="IAuditTrail"/>
/// and an <see cref="IOperationSigner"/>.
/// </summary>
public sealed class SubjectErasureService : ISubjectErasureService
{
    /// <summary>The ADR 0068 §3 approval floor: at least this many DISTINCT approvers.</summary>
    public const int MinimumApprovers = 2;

    private readonly ISubjectErasureRegistry _registry;
    private readonly ISubjectTombstoneStore _tombstones;
    private readonly IAuditTrail _auditTrail;
    private readonly IOperationSigner _signer;
    private readonly ITenantKeyDestroyer _keyDestroyer;
    private readonly IRecoveryClock _clock;
    private readonly TimeSpan _minimumWindow;
    private readonly IReadOnlyList<ISubjectErasurePropagator> _propagators;
    private readonly ILegalHoldRegistry? _holds;

    /// <param name="registry">The crypto-shred register whose tombstone destroys the per-subject key.</param>
    /// <param name="tombstones">The append-only pseudonymized-tombstone store.</param>
    /// <param name="auditTrail">Audit trail — erasure emission is mandatory.</param>
    /// <param name="signer">Operation signer for the audit payload.</param>
    /// <param name="keyDestroyer">Stored-key hierarchy destruction seam.</param>
    /// <param name="minimumWindow">The mandatory-minimum dwell between request-filed and execution (ADR 0068 §1.3 minimum window). Defaults to 24h when null; pass <see cref="TimeSpan.Zero"/> ONLY in tests that exercise the no-dwell path.</param>
    /// <param name="clock">Clock; defaults to the system clock.</param>
    /// <param name="propagators">
    /// Derived-cleartext-cache purge sinks (the Slice-1d F1 wire). After a first-time shred takes effect
    /// (registry marked + tombstone written + erasure audited), each is invoked to drop the subject's derived
    /// residue (e.g. the KG-search cleartext <c>vec0</c> embedding cache). Null/empty = no derived caches on
    /// this host. A propagator fault PROPAGATES (it is not swallowed) — the durable shred has already taken
    /// legal effect, but a failed cleartext-cache purge is a compliance signal a host must learn about.
    /// </param>
    /// <param name="holds">
    /// The fail-closed pre-shred legal-hold gate (ADR 0142 §D3). When supplied, it is consulted FIRST on
    /// every <see cref="EraseAsync"/> call: a subject under an active hold returns
    /// <see cref="SubjectErasureOutcome.BlockedByLegalHold"/> with NO key destruction ("hold-wins ABOVE
    /// floor-wins"). When <c>null</c>, this host has NO legal-hold subsystem and no hold gate applies (the
    /// legacy behaviour). A host that runs the retention-shred scheduler MUST wire a durable registry (its DI
    /// injects the same registry here), so the scheduled path is always gated; the null path exists only for
    /// hosts/tests that do no holds. An ACTIVE-but-unreachable registry fails closed (treats as held) — see
    /// <see cref="ILegalHoldRegistry"/>.
    /// </param>
    public SubjectErasureService(
        ISubjectErasureRegistry registry,
        ISubjectTombstoneStore tombstones,
        IAuditTrail auditTrail,
        IOperationSigner signer,
        ITenantKeyDestroyer keyDestroyer,
        IRecoveryClock clock,
        TimeSpan? minimumWindow = null,
        IEnumerable<ISubjectErasurePropagator>? propagators = null,
        ILegalHoldRegistry? holds = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _tombstones = tombstones ?? throw new ArgumentNullException(nameof(tombstones));
        _auditTrail = auditTrail ?? throw new ArgumentNullException(nameof(auditTrail));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _keyDestroyer = keyDestroyer ?? throw new ArgumentNullException(nameof(keyDestroyer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _minimumWindow = minimumWindow ?? TimeSpan.FromHours(24);
        if (_minimumWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumWindow), "Minimum window cannot be negative.");
        }
        _propagators = propagators?.ToArray() ?? Array.Empty<ISubjectErasurePropagator>();
        _holds = holds;
    }

    /// <inheritdoc />
    public async Task<SubjectErasureResult> EraseAsync(SubjectErasureRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // --- Gate 0: legal-hold pre-shred gate (ADR 0142 §D3 — hold-wins ABOVE floor-wins). ---
        // The DEFINITIVE, non-bypassable hold check lives HERE, inside the single key-destruction
        // choke point, so EVERY caller — the retention-shred release path, the manual GDPR PEP, any
        // future caller — is gated by construction, not by remembering to pre-check. A subject under
        // an active hold is refused with NO key destruction and NO tombstone; the attempt + block is
        // audited. Fail-closed: an unreachable registry answers "held" (see ILegalHoldRegistry).
        if (_holds is not null
            && await _holds.IsSubjectHeldAsync(request.Tenant, request.Subject, ct).ConfigureAwait(false))
        {
            await EmitShredBlockedAsync(request.Tenant, request.Subject, ct).ConfigureAwait(false);
            return new SubjectErasureResult(SubjectErasureOutcome.BlockedByLegalHold, Tombstone: null);
        }

        // --- Gate 1: multi-actor approval floor (ADR 0068 §3). ---
        // ≥2 DISTINCT approvers; a null / empty / single-actor / all-same-actor
        // chain fails closed. No "self-approval-only" shortcut.
        var approvers = request.ApprovingActors ?? Array.Empty<ActorId>();
        var distinctApprovers = approvers.Select(a => a.Value).Distinct(StringComparer.Ordinal).Count();
        if (distinctApprovers < MinimumApprovers)
        {
            throw new SubjectErasureRejectedException("approval floor not met");
        }

        // --- Gate 2: mandatory-minimum dwell window (ADR 0068 §1.3). ---
        // Erasure can never be a same-instant single step; the request must have
        // dwelled at least _minimumWindow before it executes.
        var now = _clock.UtcNow();
        if (now - request.RequestedAt < _minimumWindow)
        {
            throw new SubjectErasureRejectedException("minimum window not elapsed");
        }

        var pseudonym = SubjectPseudonym.Derive(request.Tenant, request.Subject);

        // --- Idempotency: an already-erased subject is a no-op. ---
        // MarkErasedAsync returns false when the subject was already shredded; in
        // that case we return the existing tombstone and DO NOT re-emit the audit.
        var firstErasure = await _registry
            .MarkErasedAsync(request.Tenant, request.Subject, ct)
            .ConfigureAwait(false);

        // Destroy the stored subject key even on an idempotent retry. If an earlier attempt marked the
        // erasure but was interrupted before key deletion, the retry must finish the irreversible step.
        await _keyDestroyer
            .DestroySubjectKeysAsync(request.Tenant, request.Subject, ct)
            .ConfigureAwait(false);

        if (!firstErasure)
        {
            var existing = await _tombstones
                .FindAsync(request.Tenant, pseudonym, ct)
                .ConfigureAwait(false);
            return new SubjectErasureResult(SubjectErasureOutcome.AlreadyErased, existing);
        }

        // --- The shred is now in effect (DeriveSubjectKeyAsync fails closed). ---
        // Write the pseudonymized tombstone in place of the subject's identifying
        // fields.
        var tombstone = new SubjectTombstone(
            TenantId: request.Tenant,
            Pseudonym: pseudonym,
            ErasedAt: now,
            ApprovingActors: approvers.ToImmutableArray(),
            LegalBasis: request.LegalBasis);
        await _tombstones.WriteAsync(tombstone, ct).ConfigureAwait(false);

        // --- Audit the erasure itself (mandatory; no plaintext in the payload). ---
        await EmitErasedAsync(tombstone, ct).ConfigureAwait(false);

        // --- F1 (Slice-1d): drop derived CLEARTEXT caches of the now-shredded subject. ---
        // The durable ciphertext is already undecryptable (the sub-key is gone). But a derived,
        // partially-invertible cleartext cache (the KG-search vec0 acceleration table) would survive the
        // crypto-shred until explicitly purged. Run AFTER the audit so the shred is durably recorded first;
        // run ONLY on this first-time erasure (the idempotent already-erased path above returned early).
        // A fault propagates — a failed cleartext-cache purge is a compliance signal, not a silent swallow.
        foreach (var propagator in _propagators)
        {
            await propagator.PropagateErasureAsync(request.Tenant, request.Subject, ct).ConfigureAwait(false);
        }

        return new SubjectErasureResult(SubjectErasureOutcome.Erased, tombstone);
    }

    private async Task EmitShredBlockedAsync(TenantId tenant, SubjectId subject, CancellationToken ct)
    {
        var payload = LegalHold.LegalHoldAuditPayloadFactory.ShredBlocked(tenant, subject);
        var occurredAt = _clock.UtcNow();
        var signed = await _signer.SignAsync(payload, occurredAt, Guid.NewGuid(), ct).ConfigureAwait(false);
        var record = new AuditRecord(
            AuditId: Guid.NewGuid(),
            TenantId: tenant,
            EventType: AuditEventType.SubjectShredBlockedByLegalHold,
            OccurredAt: occurredAt,
            Payload: signed,
            AttestingSignatures: ImmutableArray<AttestingSignature>.Empty);
        await _auditTrail.AppendAsync(record, ct).ConfigureAwait(false);
    }

    private async Task EmitErasedAsync(SubjectTombstone tombstone, CancellationToken ct)
    {
        var payload = SubjectErasureAuditPayloadFactory.Erased(tombstone);
        var signed = await _signer.SignAsync(payload, tombstone.ErasedAt, Guid.NewGuid(), ct).ConfigureAwait(false);
        var record = new AuditRecord(
            AuditId: Guid.NewGuid(),
            TenantId: tombstone.TenantId,
            EventType: AuditEventType.SubjectErased,
            OccurredAt: tombstone.ErasedAt,
            Payload: signed,
            AttestingSignatures: ImmutableArray<AttestingSignature>.Empty);
        await _auditTrail.AppendAsync(record, ct).ConfigureAwait(false);
    }
}
