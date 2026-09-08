using System.Collections.Immutable;
using System.Diagnostics;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Blocks.FinancialLedger.Services;

/// <summary>
/// Atomic write boundary for journal entries. Implementations wrap the
/// underlying persistence (SQLite in production) and surface a
/// commit-or-rollback semantic the
/// <see cref="JournalPostingService"/> can drive without knowing the
/// storage details.
///
/// <para>
/// <b>Cohort-2 PR 0d tenant-keying retrofit (pattern-009-tenant-keying-retrofit
/// 4th instance + ratification trigger; ADR 0092 Step 1).</b> Every method
/// takes <see cref="TenantId"/> as the FIRST positional parameter
/// (analyzer-enforced at ADR 0092 Step 4c). Write methods assert
/// <c>entry.TenantId == tenantId</c> at the boundary; mismatch throws
/// <see cref="ArgumentException"/>.
/// </para>
/// </summary>
public interface IJournalStore : ITenantScopedRepository<JournalEntry, JournalEntryId>
{
    /// <summary>
    /// Persist <paramref name="entry"/> + its lines as a single atomic
    /// unit. Throws on any failure — implementations MUST roll back any
    /// partial writes before propagating.
    /// <see cref="ArgumentException"/> when <c>entry.TenantId</c> does not
    /// match <paramref name="tenantId"/>.
    /// </summary>
    Task SaveAtomicAsync(
        TenantId tenantId,
        JournalEntry entry,
        AuthorizationDecision decision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Snapshot of persisted entries for <paramref name="tenantId"/> — for
    /// test assertions about rollback / partial-write behaviour.
    /// Cross-tenant rows are filtered out (uniform-empty per ADR 0092 §A3).
    /// Implementations may return a defensive copy.
    /// </summary>
    IReadOnlyList<JournalEntry> Snapshot(TenantId tenantId);

    /// <summary>
    /// Returns the already-posted entry whose <see cref="JournalEntry.SourceReference"/>
    /// equals <paramref name="sourceReference"/> for <paramref name="tenantId"/>, or
    /// <c>null</c> when none exists. The pre-write half of the ADR 0122 §D2 posting
    /// idempotency key: <see cref="JournalPostingService"/> consults this before the
    /// atomic commit so that a re-driven source event (same <c>SourceReference</c>)
    /// returns the existing JE rather than double-posting.
    ///
    /// <para>
    /// <b>Recoverable-store dedupe (SC-4-critical, ADR 0122 §D2).</b> The dedupe state
    /// IS the persisted record — this lookup reads the recoverable journal store
    /// (<c>local-node.db</c> on the node; the Bridge Postgres store on the Bridge),
    /// backed by the unique index on the <c>SourceReference</c> column. It is NOT a
    /// seed-keyed/in-memory KV index (the orphaned <c>kernel-ledger.PostingEngine</c>
    /// <c>_byIdempotencyKey</c> shape the SC-4 gate forbids). The index is the durable
    /// race backstop; this lookup is the cooperative fast path.
    /// </para>
    ///
    /// <para>
    /// Cross-tenant rows are filtered out (uniform-null per ADR 0092 §A3).
    /// <paramref name="sourceReference"/> is the globally-unique prefixed natural key
    /// (<c>bill:</c>/<c>invoice:</c>/<c>payment-clear:</c>…); manual JEs with a null
    /// <see cref="JournalEntry.SourceReference"/> are never deduped, so callers must not
    /// pass <c>null</c> here.
    /// </para>
    /// </summary>
    Task<JournalEntry?> FindBySourceReferenceAsync(
        TenantId tenantId,
        string sourceReference,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory <see cref="IJournalStore"/>. Saves to a backing list;
/// supports an injected failure-trigger predicate so tests can induce
/// commit-time exceptions to exercise the rollback path.
///
/// <para>
/// Cohort-2 PR 0d tenant-keying retrofit. Writes assert
/// <c>entry.TenantId == tenantId</c>; mismatch throws
/// <see cref="ArgumentException"/>. <see cref="Snapshot"/> filters by tenant.
/// Cross-tenant writes against an existing-id row emit
/// <c>AuditEventType.TenantBoundaryViolation</c> when audit emission is wired.
/// </para>
/// </summary>
public sealed class InMemoryJournalStore : IJournalStore
{
    private readonly List<JournalEntry> _entries = new();
    private readonly object _gate = new();
    private readonly IAuditTrail? _auditTrail;
    private readonly IOperationSigner? _signer;
    private readonly TenantId _auditTenant;
    private readonly TimeProvider? _time;

    /// <summary>Creates the store without audit emission (tests, demos).</summary>
    public InMemoryJournalStore()
    {
    }

    /// <summary>
    /// Creates the store with audit emission wired through
    /// <paramref name="auditTrail"/> + <paramref name="signer"/>;
    /// <paramref name="auditTenant"/> is the tenant attribution applied to
    /// emitted records.
    /// </summary>
    public InMemoryJournalStore(
        IAuditTrail auditTrail,
        IOperationSigner signer,
        TenantId auditTenant,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(auditTrail);
        ArgumentNullException.ThrowIfNull(signer);
        if (auditTenant == default)
        {
            throw new ArgumentException("TenantId is required for audit emission.", nameof(auditTenant));
        }
        _auditTrail = auditTrail;
        _signer = signer;
        _auditTenant = auditTenant;
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>
    /// If set, invoked before each save. Returning <c>true</c> raises
    /// an <see cref="InvalidOperationException"/> simulating a
    /// commit-time storage failure.
    /// </summary>
    public Func<JournalEntry, bool>? FailIf { get; set; }

    /// <inheritdoc />
    public async Task SaveAtomicAsync(
        TenantId tenantId,
        JournalEntry entry,
        AuthorizationDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(decision);
        decision.RequireAllowedReaction(
            Harborline.Api.Foundation.IdentityAtlas.Permissions.AuthorizationOperation.Parse(
                Harborline.Api.Foundation.IdentityAtlas.TeamRolePermissions.LedgerPost),
            tenantId,
            "journal-entry",
            entry.Id.Value);
        if (!entry.TenantId.Equals(tenantId))
        {
            await EmitTenantBoundaryViolationAsync(
                entry.Id.Value, tenantId, entry.TenantId, decision.Request.At, cancellationToken).ConfigureAwait(false);
            throw new ArgumentException(
                $"JournalEntry '{entry.Id.Value}' carries TenantId '{entry.TenantId.Value}' but caller passed tenantId '{tenantId.Value}'.",
                nameof(entry));
        }

        if (FailIf is not null && FailIf(entry))
        {
            // Simulate a mid-commit storage failure. NO partial state
            // mutation — the list is unchanged.
            throw new InvalidOperationException("InMemoryJournalStore: induced failure for rollback test.");
        }

        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<JournalEntry> Snapshot(TenantId tenantId)
    {
        lock (_gate)
        {
            return _entries.Where(e => e.TenantId.Equals(tenantId)).ToList();
        }
    }

    /// <inheritdoc />
    public Task<JournalEntry?> FindBySourceReferenceAsync(
        TenantId tenantId,
        string sourceReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceReference);
        lock (_gate)
        {
            // Tenant-scoped + SourceReference match (ADR 0122 §D2 posting idempotency).
            // ordinal string equality matches the persisted-column comparison the EF
            // stores perform; first-match is sufficient since the DB unique index
            // guarantees at most one row per (tenant, source-reference) in production.
            var existing = _entries.FirstOrDefault(e =>
                e.TenantId.Equals(tenantId) &&
                string.Equals(e.SourceReference, sourceReference, StringComparison.Ordinal));
            return Task.FromResult(existing);
        }
    }

    /// <summary>
    /// Replace an existing entry in the backing list with <paramref name="updated"/>.
    /// The replacement is matched on <see cref="JournalEntry.Id"/>. If no matching
    /// entry exists the method is a no-op (idempotent, same as UpdateAsync for v1
    /// in-memory fidelity). Tenant guard: throws <see cref="ArgumentException"/> when
    /// <c>updated.TenantId</c> does not match <paramref name="tenantId"/>.
    ///
    /// <para>
    /// Intended for v1 in-process state transitions (e.g. marking an original entry
    /// Reversed after a <c>POST /journal-entries/{id}/reverse</c>). A proper
    /// <c>IUpdatableJournalStore</c> interface will supersede this when the SQLite
    /// production implementation lands.
    /// </para>
    /// </summary>
    public void ReplaceEntry(TenantId tenantId, JournalEntry updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        if (!updated.TenantId.Equals(tenantId))
            throw new ArgumentException(
                $"JournalEntry '{updated.Id.Value}' carries TenantId '{updated.TenantId.Value}' " +
                $"but caller passed tenantId '{tenantId.Value}'.",
                nameof(updated));

        lock (_gate)
        {
            var idx = _entries.FindIndex(e => e.Id.Equals(updated.Id));
            if (idx >= 0)
                _entries[idx] = updated;
        }
    }

    // ── Audit emission (ADR 0092 §A6 canonical payload shape — cohort-2 PR 0a sec-eng GREEN template) ──
    //
    // Mirror of cohort 2 PR 0a InMemoryInvoiceRepository payload shape:
    //   entity_type, entity_id, requested_tenant, actual_tenant, correlation_id
    private async ValueTask EmitTenantBoundaryViolationAsync(
        string entityId,
        TenantId requestedTenant,
        TenantId actualTenant,
        DateTimeOffset occurredAt,
        CancellationToken ct)
    {
        if (_auditTrail is null || _signer is null) return;

        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        var payload = new AuditPayload(new Dictionary<string, object?>
        {
            ["entity_type"]       = "JournalEntry",
            ["entity_id"]         = entityId,
            ["requested_tenant"]  = requestedTenant.Value,
            ["actual_tenant"]     = actualTenant.Value,
            ["correlation_id"]    = correlationId,
        });
        var signed = await _signer.SignAsync(payload, occurredAt, Guid.NewGuid(), ct).ConfigureAwait(false);
        var record = new AuditRecord(
            AuditId: Guid.NewGuid(),
            TenantId: _auditTenant,
            EventType: AuditEventType.TenantBoundaryViolation,
            OccurredAt: occurredAt,
            Payload: signed,
            AttestingSignatures: ImmutableArray<AttestingSignature>.Empty);
        await _auditTrail.AppendAsync(record, ct).ConfigureAwait(false);
    }
}
