using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Blocks.FinancialAr.Services;

/// <summary>
/// In-memory <see cref="IInvoiceRepository"/>. State lives in a single
/// <c>ConcurrentDictionary</c> keyed by <see cref="InvoiceId"/>;
/// secondary queries (by chart, by number, by customer) scan the values
/// — fine for the in-memory v1 with O(invoices) on a single tenant. A
/// SQLite-backed implementation lands in the follow-on substrate
/// hand-off and shadows this binding.
///
/// <para>
/// <b>Cohort-2 PR 0a tenant-keying retrofit.</b> Every <c>Get*</c> /
/// <c>List*</c> filters by the <c>tenantId</c> argument; rows belonging
/// to a different tenant are treated as not-found (uniform-404 per ADR
/// 0092 §"Diagnostic non-leak invariant"). When audit emission is wired
/// (via the <see cref="IAuditTrail"/> + <see cref="IOperationSigner"/>
/// ctor), a cross-tenant <c>Get</c> hit emits
/// <c>AuditEventType.TenantBoundaryViolation</c> before returning null.
/// Writes (<c>UpsertAsync</c> / <c>SoftDeleteAsync</c>) assert
/// <c>entity.TenantId == tenantId</c> at the boundary; mismatch throws
/// <see cref="ArgumentException"/>.
/// </para>
/// </summary>
public sealed class InMemoryInvoiceRepository : IInvoiceRepository
{
    private readonly ConcurrentDictionary<InvoiceId, Invoice> _invoices = new();
    private readonly IAuditTrail? _auditTrail;
    private readonly IOperationSigner? _signer;
    private readonly TenantId _auditTenant;
    private readonly TimeProvider? _time;

    /// <summary>Creates the repository without audit emission (tests, demos).</summary>
    public InMemoryInvoiceRepository()
    {
    }

    /// <summary>
    /// Creates the repository with audit emission wired through
    /// <paramref name="auditTrail"/> + <paramref name="signer"/>;
    /// <paramref name="auditTenant"/> is the tenant attribution applied to
    /// emitted records (typically the system tenant or the request's
    /// resolved tenant).
    /// </summary>
    public InMemoryInvoiceRepository(
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

    /// <inheritdoc />
    public async Task UpsertAsync(
        TenantId tenantId, Invoice invoice, DateTimeOffset admittedAt, CancellationToken cancellationToken = default)
    {
        if (invoice is null) throw new ArgumentNullException(nameof(invoice));
        if (!invoice.TenantId.Equals(tenantId))
        {
            throw new ArgumentException(
                $"Invoice '{invoice.Id.Value}' carries TenantId '{invoice.TenantId.Value}' but caller passed tenantId '{tenantId.Value}'.",
                nameof(invoice));
        }

        if (_invoices.TryGetValue(invoice.Id, out var existing))
        {
            if (existing.DeletedAtUtc is not null)
            {
                throw new InvalidOperationException($"Invoice '{invoice.Id.Value}' is tombstoned; further mutations are not permitted.");
            }
            if (!existing.TenantId.Equals(tenantId))
            {
                // Cross-tenant write attempt against an existing row — caller
                // bug. Surface as ArgumentException (same family as the
                // boundary check above).
                await EmitTenantBoundaryViolationAsync(
                    invoice.Id.Value, tenantId, existing.TenantId, admittedAt, cancellationToken).ConfigureAwait(false);
                throw new ArgumentException(
                    $"Invoice id '{invoice.Id.Value}' already exists under a different tenant.",
                    nameof(invoice));
            }
        }

        // Drafts may carry an empty InvoiceNumber (PR 3 mints on Issue).
        // Issued+ invoices MUST match the canonical numbering format —
        // a malformed number would surface as a bad ERPNext-importer
        // payload or a misuse of `Invoice.Create` with hand-rolled string.
        if (invoice.Status != Models.InvoiceStatus.Draft
            && !InvoiceNumberFormat.IsWellFormed(invoice.InvoiceNumber))
        {
            throw new InvalidOperationException(
                $"Invoice '{invoice.Id.Value}' is in status '{invoice.Status}' but its InvoiceNumber '{invoice.InvoiceNumber}' does not match the canonical format 'INV-YYYY-MM-DD-{{Replica}}-{{NNNN}}'.");
        }

        _invoices[invoice.Id] = invoice;
    }

    /// <inheritdoc />
    public async Task<Invoice?> GetAsync(
        TenantId tenantId, InvoiceId id, DateTimeOffset admittedAt, CancellationToken cancellationToken = default)
    {
        if (!_invoices.TryGetValue(id, out var inv)) return null;
        if (inv.DeletedAtUtc is not null) return null;
        if (!inv.TenantId.Equals(tenantId))
        {
            await EmitTenantBoundaryViolationAsync(
                id.Value, tenantId, inv.TenantId, admittedAt, cancellationToken).ConfigureAwait(false);
            return null;
        }
        return inv;
    }

    /// <inheritdoc />
    public Task<Invoice?> GetByNumberAsync(TenantId tenantId, ChartOfAccountsId chartId, string invoiceNumber, CancellationToken cancellationToken = default)
    {
        var hit = _invoices.Values.FirstOrDefault(i =>
            i.DeletedAtUtc is null
            && i.TenantId.Equals(tenantId)
            && i.ChartId == chartId
            && string.Equals(i.InvoiceNumber, invoiceNumber, StringComparison.Ordinal));
        return Task.FromResult<Invoice?>(hit);
    }

    /// <inheritdoc />
    public Task<Invoice?> GetByExternalRefAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        string externalRef,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(externalRef)) return Task.FromResult<Invoice?>(null);
        var hit = _invoices.Values.FirstOrDefault(i =>
            i.DeletedAtUtc is null
            && i.TenantId.Equals(tenantId)
            && i.ChartId == chartId
            && string.Equals(i.ExternalRef, externalRef, StringComparison.Ordinal));
        return Task.FromResult<Invoice?>(hit);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Invoice>> ListByChartAsync(TenantId tenantId, ChartOfAccountsId chartId, CancellationToken cancellationToken = default)
    {
        var rows = _invoices.Values
            .Where(i => i.DeletedAtUtc is null && i.TenantId.Equals(tenantId) && i.ChartId == chartId)
            .ToList();
        return Task.FromResult<IReadOnlyList<Invoice>>(rows);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Invoice>> ListByCustomerAsync(TenantId tenantId, ChartOfAccountsId chartId, PartyId customerId, CancellationToken cancellationToken = default)
    {
        var rows = _invoices.Values
            .Where(i => i.DeletedAtUtc is null && i.TenantId.Equals(tenantId) && i.ChartId == chartId && i.CustomerId == customerId)
            .ToList();
        return Task.FromResult<IReadOnlyList<Invoice>>(rows);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Invoice>> ListBySubLedgerAccountAsync(
        TenantId tenantId,
        SubLedgerAccountId subLedgerAccountId,
        CancellationToken cancellationToken = default)
    {
        // Defence-in-depth: filter on BOTH SubLedgerAccountId AND TenantId.
        // SubLedgerAccountId alone is opaque-GUID unique, but the TenantId
        // WHERE clause matches the ADR 0092 posture (no ambient tenant context —
        // caller-supplied TenantId is the only isolation mechanism on this path).
        var rows = _invoices.Values
            .Where(i =>
                i.DeletedAtUtc is null
                && i.TenantId.Equals(tenantId)
                && i.SubLedgerAccountId.HasValue
                && i.SubLedgerAccountId.Value == subLedgerAccountId)
            .ToList();
        return Task.FromResult<IReadOnlyList<Invoice>>(rows);
    }

    /// <inheritdoc />
    public async Task<bool> SoftDeleteAsync(
        TenantId tenantId, InvoiceId id, PartyId actor, string? reason, DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default)
    {
        if (!_invoices.TryGetValue(id, out var inv)) return false;
        if (!inv.TenantId.Equals(tenantId))
        {
            await EmitTenantBoundaryViolationAsync(
                id.Value, tenantId, inv.TenantId, admittedAt, cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (inv.DeletedAtUtc is not null) return true; // idempotent

        var now = new Instant(admittedAt);
        _invoices[id] = inv with
        {
            DeletedAtUtc = now,
            DeletedBy = actor,
            DeletedReason = reason,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = inv.Version + 1,
        };
        return true;
    }

    /// <inheritdoc />
    public async Task<bool?> SoftDeleteIfStatusAsync(
        TenantId tenantId,
        InvoiceId id,
        PartyId actor,
        string? reason,
        Models.InvoiceStatus requiredStatus,
        DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default)
    {
        if (!_invoices.TryGetValue(id, out var inv)) return false;
        if (!inv.TenantId.Equals(tenantId))
        {
            await EmitTenantBoundaryViolationAsync(
                id.Value, tenantId, inv.TenantId, admittedAt, cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (inv.DeletedAtUtc is not null) return true; // already tombstoned — idempotent

        // TOCTOU guard (F-2 sec-eng fix-forward 2026-06-12): re-check status at
        // delete time. A concurrent IssueAsync between the handler's status check and
        // this call can change Draft → Issued; tombstoning a GL-posted invoice would
        // strand its journal entry. Return null so the handler maps to 409 Conflict.
        if (inv.Status != requiredStatus) return null;

        var now = new Instant(admittedAt);
        _invoices[id] = inv with
        {
            DeletedAtUtc  = now,
            DeletedBy     = actor,
            DeletedReason = reason,
            UpdatedAtUtc  = now,
            UpdatedBy     = actor,
            Version       = inv.Version + 1,
        };
        return true;
    }

    // ── Audit emission (ADR 0092 §A6 canonical payload shape — sec-eng SPOT-CHECK GREEN template) ──
    //
    // Payload carries:
    //   entity_type     — fixed string per repository
    //   entity_id       — opaque entity identifier value
    //   requested_tenant — the tenant the CALLER passed (sec-eng AMBER amendment: was "observed_tenant")
    //   actual_tenant    — the tenant the ENTITY actually carries (sec-eng AMBER amendment A1)
    //   correlation_id   — current Activity.Id when set, Guid fallback otherwise (sec-eng AMBER amendment A2)
    //
    // No entity-specific content (amounts, terminal-state strings, display
    // names) per ADR 0092 §A6 diagnostic non-leak invariant.
    private async ValueTask EmitTenantBoundaryViolationAsync(
        string entityId,
        TenantId requestedTenant,
        TenantId actualTenant,
        DateTimeOffset? admittedAt,
        CancellationToken ct)
    {
        if (_auditTrail is null || _signer is null) return;

        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
        var payload = new AuditPayload(new Dictionary<string, object?>
        {
            ["entity_type"]       = "Invoice",
            ["entity_id"]         = entityId,
            ["requested_tenant"]  = requestedTenant.Value,
            ["actual_tenant"]     = actualTenant.Value,
            ["correlation_id"]    = correlationId,
        });
        var occurredAt = admittedAt ?? throw new InvalidOperationException("An audit instant is required when audit emission is wired.");
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
