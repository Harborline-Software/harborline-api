using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Kernel.Audit;

/// <summary>
/// In-memory <see cref="IAuditEventReader"/> reference implementation.
/// Shares the in-memory backing store with <see cref="InMemoryAuditTrail"/>
/// via constructor injection, providing consistent read-after-write
/// behaviour in test fixtures and development hosts without maintaining two
/// parallel stores.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR 0094 (IAuditEventReader) + ADR 0091 (ITenantContext) + ADR 0092
/// (substrate tenant-keyed repository contract) + ADR 0049 (audit-trail
/// substrate write side).
/// </para>
///
/// <para>
/// <b>DI lifetime constraint (ADR 0094 Amendment 2.5).</b>
/// <see cref="InMemoryAuditEventReader"/> constructor-injects
/// <see cref="InMemoryAuditTrail"/> as the CONCRETE class so it can call
/// the internal <c>Snapshot()</c> method to access the writer's in-memory
/// backing field directly. The host MUST register
/// <see cref="InMemoryAuditTrail"/> as Scoped or Singleton — NOT Transient.
/// A Transient registration would give the reader a fresh, empty
/// <see cref="InMemoryAuditTrail"/> on each resolution, silently losing
/// every record appended via the writer's instance. The
/// <c>AddHarborlineKernelAuditReaderInMemory()</c> extension registers the
/// trail as Scoped; overrides MUST maintain Scoped or Singleton. See
/// <see cref="DependencyInjection.ServiceCollectionExtensions"/> for the
/// startup lifetime assertion.
/// </para>
///
/// <para>
/// <b>Audit emission is recursion-safe.</b> When
/// <see cref="GetByIdAsync"/> detects a cross-tenant probe it emits via the
/// write-side <see cref="IAuditTrail"/> injected as the <c>emitter</c>
/// constructor parameter — NOT by calling any method on itself. The
/// emitted record is later readable by callers with the correct tenant; it
/// is never read BY the emitter as part of the emission path.
/// </para>
///
/// <para>
/// <b>Restart-volatile.</b> Process restart loses all stored records.
/// </para>
/// </remarks>
public sealed class InMemoryAuditEventReader : IAuditEventReader
{
    private readonly InMemoryAuditTrail _trail;
    private readonly IAuditTrail _emitter;
    private readonly IOperationSigner _signer;

    /// <summary>
    /// Initialises the reader with the shared in-memory store.
    /// </summary>
    /// <param name="trail">
    /// The CONCRETE <see cref="InMemoryAuditTrail"/> instance shared with the
    /// write side. Must be the same DI-scope instance as the registered
    /// writer.
    /// </param>
    /// <param name="emitter">
    /// The write-side <see cref="IAuditTrail"/> used to emit
    /// <c>TenantBoundaryViolation</c> audit records on cross-tenant probes.
    /// Typically resolves to the same underlying
    /// <see cref="InMemoryAuditTrail"/> via the DI container.
    /// </param>
    /// <param name="signer">
    /// The operation signer used to produce signed audit payloads when
    /// emitting <c>TenantBoundaryViolation</c> records.
    /// </param>
    public InMemoryAuditEventReader(
        InMemoryAuditTrail trail,
        IAuditTrail emitter,
        IOperationSigner signer)
    {
        ArgumentNullException.ThrowIfNull(trail);
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentNullException.ThrowIfNull(signer);
        _trail = trail;
        _emitter = emitter;
        _signer = signer;
    }

    /// <inheritdoc />
    public async Task<AuditRecord?> GetByIdAsync(
        TenantId tenantId,
        Guid auditId,
        DateTimeOffset admittedAt,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var snapshot = _trail.Snapshot();
        var found = snapshot.FirstOrDefault(r => r.AuditId == auditId);

        if (found is null)
        {
            // Not found — no audit emission; absence is not a probe signal.
            return null;
        }

        if (found.TenantId == tenantId)
        {
            return found;
        }

        // Cross-tenant probe: record exists but belongs to a different tenant.
        // Emit TenantBoundaryViolation then return null (uniform-empty per
        // ADR 0092 §A3 — no diagnostic leak).
        await EmitTenantBoundaryViolationAsync(
            entityId: auditId.ToString("D"),
            requestedTenant: tenantId,
            actualTenant: found.TenantId,
            admittedAt: admittedAt,
            ct: ct).ConfigureAwait(false);

        return null;
    }

    /// <inheritdoc />
    public async Task<AuditEventPage> ListAsync(
        TenantId tenantId,
        AuditEventReaderQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();

        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        // Cross-tenant cursor reuse: if the cursor belongs to a different
        // tenant, return uniform-empty (ADR 0092 §A3; Bridge signature-check
        // fires first per cohort-4 hand-off Decision 5).
        if (query.Cursor is { } c && c.TenantId != tenantId)
        {
            return new AuditEventPage(
                Records: Array.Empty<AuditRecord>(),
                NextCursor: null,
                HasMore: false);
        }

        var snapshot = _trail.Snapshot();

        // Apply tenant + filter predicates.
        var filtered = ApplyFilters(snapshot, tenantId, query);

        // Sort reverse-chronological: OccurredAt DESC, AuditId DESC (byte-lex).
        filtered = filtered
            .OrderByDescending(r => r.OccurredAt)
            .ThenByDescending(r => r.AuditId, GuidComparer.Instance)
            .ToList();

        // Apply cursor walking predicate (ADR 0094 Amendment 2.3):
        // include R iff R.OccurredAt < C.OccurredAt
        //              OR (R.OccurredAt == C.OccurredAt AND R.AuditId < C.AuditId)
        if (query.Cursor is { } cursor)
        {
            filtered = filtered
                .Where(r =>
                    r.OccurredAt < cursor.OccurredAt ||
                    (r.OccurredAt == cursor.OccurredAt &&
                     GuidComparer.Instance.Compare(r.AuditId, cursor.AuditId) < 0))
                .ToList();
        }

        // Take one extra to detect HasMore.
        var page = filtered.Take(pageSize + 1).ToList();
        var hasMore = page.Count > pageSize;
        var records = hasMore ? page.Take(pageSize).ToList() : page;

        AuditEventCursor? nextCursor = null;
        if (hasMore && records.Count > 0)
        {
            var last = records[^1];
            nextCursor = new AuditEventCursor(last.OccurredAt, last.AuditId, tenantId);
        }

        return await Task.FromResult(new AuditEventPage(
            Records: records,
            NextCursor: nextCursor,
            HasMore: hasMore));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AuditRecord> StreamAsync(
        TenantId tenantId,
        AuditEventReaderQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var snapshot = _trail.Snapshot();
        var filtered = ApplyFilters(snapshot, tenantId, query)
            .OrderByDescending(r => r.OccurredAt)
            .ThenByDescending(r => r.AuditId, GuidComparer.Instance);

        foreach (var record in filtered)
        {
            ct.ThrowIfCancellationRequested();
            yield return record;
            await Task.Yield();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Applies tenant + query field filters (excluding cursor and page size —
    /// those are applied by callers separately).
    /// </summary>
    private static List<AuditRecord> ApplyFilters(
        AuditRecord[] snapshot,
        TenantId tenantId,
        AuditEventReaderQuery query)
    {
        var result = new List<AuditRecord>(snapshot.Length);
        foreach (var record in snapshot)
        {
            if (record.TenantId != tenantId) continue;
            if (query.EventType is { } et && !record.EventType.Equals(et)) continue;
            if (query.From is { } from && record.OccurredAt < from) continue;
            if (query.To is { } to && record.OccurredAt > to) continue;
            if (query.CorrelationId is { } cid)
            {
                if (!record.Payload.Payload.Body.TryGetValue("correlation_id", out var v) ||
                    !string.Equals(v?.ToString(), cid, StringComparison.Ordinal))
                {
                    continue;
                }
            }
            result.Add(record);
        }
        return result;
    }

    /// <summary>
    /// Emits a <c>TenantBoundaryViolation</c> audit record via the write-side
    /// <see cref="IAuditTrail"/> (NOT through this reader — recursion-safe).
    /// Canonical 5-field payload per ADR 0092 §A6 + ADR 0094 §Decision drivers:
    /// entity_type, entity_id, requested_tenant, actual_tenant, correlation_id.
    /// </summary>
    private async ValueTask EmitTenantBoundaryViolationAsync(
        string entityId,
        TenantId requestedTenant,
        TenantId actualTenant,
        DateTimeOffset admittedAt,
        CancellationToken ct)
    {
        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        var payload = new AuditPayload(new Dictionary<string, object?>
        {
            ["entity_type"]       = "AuditRecord",
            ["entity_id"]         = entityId,
            ["requested_tenant"]  = requestedTenant.Value,
            ["actual_tenant"]     = actualTenant.Value,
            ["correlation_id"]    = correlationId,
        });

        var signed = await _signer.SignAsync(payload, admittedAt, Guid.NewGuid(), ct)
            .ConfigureAwait(false);

        var record = new AuditRecord(
            AuditId: Guid.NewGuid(),
            TenantId: requestedTenant,
            EventType: AuditEventType.TenantBoundaryViolation,
            OccurredAt: admittedAt,
            Payload: signed,
            AttestingSignatures: Array.Empty<AttestingSignature>());

        await _emitter.AppendAsync(record, ct).ConfigureAwait(false);
    }

    // ── Guid comparator (byte-lex order per ADR 0094 §AuditEventCursor) ───────

    private sealed class GuidComparer : IComparer<Guid>
    {
        public static readonly GuidComparer Instance = new();
        private GuidComparer() { }

        public int Compare(Guid x, Guid y)
        {
            var xBytes = x.ToByteArray();
            var yBytes = y.ToByteArray();
            for (var i = 0; i < 16; i++)
            {
                var cmp = xBytes[i].CompareTo(yBytes[i]);
                if (cmp != 0) return cmp;
            }
            return 0;
        }
    }
}
