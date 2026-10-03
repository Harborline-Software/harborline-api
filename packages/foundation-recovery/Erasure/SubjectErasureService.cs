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
public sealed class SubjectErasureService : ISubjectErasureService, ISubjectErasureRecovery
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
    private readonly ISubjectErasureRecoveryRegistry? _recovery;

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
        _recovery = registry as ISubjectErasureRecoveryRegistry;
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

        var evidence = new SubjectErasureEvidence(approvers, request.LegalBasis, now);

        // --- Idempotency and crash-resume (DES-0029 ck-6). ---
        // MarkErasedAsync returns false when the subject was already shredded. On a durable recovery registry
        // (T-1048) the mark records the approval evidence in the same commit, so the host's recovery pass can
        // finish an interrupted erasure without this request, and completion is the registry's own record.
        // Otherwise a completed erasure is recognised by its SubjectErased audit, written LAST under a
        // deterministic id. Anything short of completion is an erasure a crash interrupted, and this call finishes it.
        var firstErasure = _recovery is not null
            ? await _recovery.MarkErasedAsync(request.Tenant, request.Subject, evidence, ct).ConfigureAwait(false)
            : await _registry.MarkErasedAsync(request.Tenant, request.Subject, ct).ConfigureAwait(false);

        if (!firstErasure)
        {
            var existing = await _tombstones
                .FindAsync(request.Tenant, SubjectPseudonym.Derive(request.Tenant, request.Subject), ct)
                .ConfigureAwait(false);
            if (existing is not null && await IsCompletedAsync(existing, request.Subject, ct).ConfigureAwait(false))
            {
                // Destroy the stored subject key even on an idempotent retry (key deletion is idempotent).
                await _keyDestroyer.DestroySubjectKeysAsync(request.Tenant, request.Subject, ct).ConfigureAwait(false);
                return new SubjectErasureResult(SubjectErasureOutcome.AlreadyErased, existing);
            }

            // A retry finishes the erasure the FIRST mark approved: its recorded evidence, never this request's.
            if (_recovery is not null
                && await _recovery.FindEvidenceAsync(request.Tenant, request.Subject, ct).ConfigureAwait(false) is { } recorded)
            {
                evidence = recorded;
            }
        }

        var tombstone = await FinishAsync(request.Tenant, request.Subject, evidence, ct).ConfigureAwait(false);
        return new SubjectErasureResult(SubjectErasureOutcome.Erased, tombstone);
    }

    /// <inheritdoc />
    public async Task<int> RecoverInterruptedAsync(int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (_recovery is null) return 0;
        var now = _clock.UtcNow();
        var completed = 0;
        foreach (var due in await _recovery.ListDueAsync(now, limit, ct).ConfigureAwait(false))
        {
            try
            {
                // The request passed the hold gate before its mark. A hold placed since defers the rest, as a
                // retried request would be refused; the erasure stays owed and is retried with backoff.
                if (_holds is not null
                    && await _holds.IsSubjectHeldAsync(due.Tenant, due.Subject, ct).ConfigureAwait(false))
                {
                    await _recovery.DeferAsync(due.Tenant, due.Subject, now, ct).ConfigureAwait(false);
                    continue;
                }

                await FinishAsync(due.Tenant, due.Subject, due.Evidence, ct).ConfigureAwait(false);
                completed++;
            }
            // Any fault defers the row, an OperationCanceledException included unless it is this pass's own cancellation:
            // one failing erasure must not stall or end the pass.
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Never abandoned: the evidence stays, and the row is retried once its backoff elapses, so a row
                // that keeps failing does not hold back newer rows.
                await _recovery.DeferAsync(due.Tenant, due.Subject, now, ct).ConfigureAwait(false);
            }
        }

        return completed;
    }

    /// <summary>
    /// Finishes a marked erasure. Every step is idempotent: key destruction, the write-once tombstone, the purge,
    /// then the SubjectErased audit, which on a recovery registry is secured in the commit that clears the evidence.
    /// </summary>
    private async Task<SubjectTombstone> FinishAsync(
        TenantId tenant, SubjectId subject, SubjectErasureEvidence evidence, CancellationToken ct)
    {
        // Destroy the stored subject key even on a resumed erasure: an earlier attempt may have marked the
        // erasure but been interrupted before key deletion.
        await _keyDestroyer.DestroySubjectKeysAsync(tenant, subject, ct).ConfigureAwait(false);

        // --- The shred is now in effect (DeriveSubjectKeyAsync fails closed). ---
        // Write the pseudonymized tombstone in place of the subject's identifying fields (write-once: a resumed
        // erasure keeps the tombstone its first attempt wrote).
        var pseudonym = SubjectPseudonym.Derive(tenant, subject);
        var existing = await _tombstones.FindAsync(tenant, pseudonym, ct).ConfigureAwait(false);
        var tombstone = existing ?? new SubjectTombstone(
            TenantId: tenant,
            Pseudonym: pseudonym,
            ErasedAt: evidence.ApprovedAt,
            ApprovingActors: evidence.ApprovingActors.ToImmutableArray(),
            LegalBasis: evidence.LegalBasis);
        if (existing is null)
            await _tombstones.WriteAsync(tombstone, ct).ConfigureAwait(false);

        // --- F1 (Slice-1d): drop derived CLEARTEXT caches of the now-shredded subject. ---
        // The durable ciphertext is already undecryptable (the sub-key is gone). But a derived,
        // partially-invertible cleartext cache (the KG-search vec0 acceleration table) would survive the
        // crypto-shred until explicitly purged. The registry and tombstone already record the shred durably; the
        // purge runs before the audit so the audit marks the erasure COMPLETE, and a purge a crash interrupted is
        // re-run. A fault propagates — a failed cleartext-cache purge is a compliance signal.
        foreach (var propagator in _propagators)
        {
            await propagator.PropagateErasureAsync(tenant, subject, ct).ConfigureAwait(false);
        }

        // --- Audit the erasure itself (mandatory; no plaintext in the payload), exactly once. ---
        // The trail gets it now. On a recovery registry the same signed record is also staged durably in the commit
        // that clears the evidence, so the evidence is never cleared on the strength of an in-memory append.
        var audit = await FindErasedAuditAsync(tombstone, ct).ConfigureAwait(false);
        if (audit is null)
        {
            audit = await SignErasedAsync(tombstone, ct).ConfigureAwait(false);
            await _auditTrail.AppendAsync(audit, ct).ConfigureAwait(false);
        }
        if (_recovery is not null)
            await _recovery.CompleteAsync(subject, audit, ct).ConfigureAwait(false);

        return tombstone;
    }

    private async Task<bool> IsCompletedAsync(SubjectTombstone tombstone, SubjectId subject, CancellationToken ct) =>
        _recovery is not null
            ? await _recovery.IsCompletedAsync(tombstone.TenantId, subject, ct).ConfigureAwait(false)
            : await FindErasedAuditAsync(tombstone, ct).ConfigureAwait(false) is not null;

    /// <summary>The SubjectErased entry's id: one per erased subject, so a resumed erasure never records two.</summary>
    private static Guid ErasedAuditId(SubjectTombstone tombstone) => new(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes("subject-erased|" + tombstone.TenantId.Value + "|" + tombstone.Pseudonym))
        .AsSpan(0, 16));

    private async Task<AuditRecord?> FindErasedAuditAsync(SubjectTombstone tombstone, CancellationToken ct)
    {
        await foreach (var record in _auditTrail.QueryAsync(
                new AuditQuery(tombstone.TenantId, AuditEventType.SubjectErased, AuditId: ErasedAuditId(tombstone)), ct)
            .ConfigureAwait(false))
            return record;
        return null;
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

    private async Task<AuditRecord> SignErasedAsync(SubjectTombstone tombstone, CancellationToken ct)
    {
        var payload = SubjectErasureAuditPayloadFactory.Erased(tombstone);
        var signed = await _signer.SignAsync(payload, tombstone.ErasedAt, Guid.NewGuid(), ct).ConfigureAwait(false);
        var record = new AuditRecord(
            AuditId: ErasedAuditId(tombstone),
            TenantId: tombstone.TenantId,
            EventType: AuditEventType.SubjectErased,
            OccurredAt: tombstone.ErasedAt,
            Payload: signed,
            AttestingSignatures: ImmutableArray<AttestingSignature>.Empty);
        return record;
    }
}
