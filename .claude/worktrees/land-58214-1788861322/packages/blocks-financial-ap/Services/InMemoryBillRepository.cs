using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Blocks.FinancialAp.Services;

/// <summary>
/// In-memory <see cref="IBillRepository"/>. Mirrors AR's
/// <c>InMemoryInvoiceRepository</c>; secondary queries scan the values
/// — fine for the in-memory v1.
///
/// <para>
/// <b>Cohort-2 PR 0b tenant-keying retrofit.</b> Every <c>Get*</c> / <c>List*</c>
/// filters by the <c>tenantId</c> argument; rows belonging to a different
/// tenant are treated as not-found (uniform-404 per ADR 0092). When audit
/// emission is wired, a cross-tenant <c>Get</c> hit emits
/// <c>AuditEventType.TenantBoundaryViolation</c> before returning null.
/// Writes (<c>UpsertAsync</c> / <c>SoftDeleteAsync</c>) assert
/// <c>entity.TenantId == tenantId</c>; mismatch throws
/// <see cref="ArgumentException"/>.
/// </para>
/// </summary>
public sealed class InMemoryBillRepository : IBillRepository
{
    private readonly ConcurrentDictionary<BillId, Bill> _bills = new();
    private readonly IAuditTrail? _auditTrail;
    private readonly IOperationSigner? _signer;
    private readonly TenantId _auditTenant;
    private readonly TimeProvider? _time;

    /// <summary>Creates the repository without audit emission (tests, demos).</summary>
    public InMemoryBillRepository()
    {
    }

    /// <summary>
    /// Creates the repository with audit emission wired through
    /// <paramref name="auditTrail"/> + <paramref name="signer"/>;
    /// <paramref name="auditTenant"/> is the tenant attribution applied to
    /// emitted records.
    /// </summary>
    public InMemoryBillRepository(
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
        TenantId tenantId, Bill bill, DateTimeOffset admittedAt, CancellationToken cancellationToken = default)
    {
        if (bill is null) throw new ArgumentNullException(nameof(bill));
        if (!bill.TenantId.Equals(tenantId))
        {
            throw new ArgumentException(
                $"Bill '{bill.Id.Value}' carries TenantId '{bill.TenantId.Value}' but caller passed tenantId '{tenantId.Value}'.",
                nameof(bill));
        }

        if (_bills.TryGetValue(bill.Id, out var existing))
        {
            if (existing.DeletedAtUtc is not null)
            {
                throw new InvalidOperationException($"Bill '{bill.Id.Value}' is tombstoned; further mutations are not permitted.");
            }
            if (!existing.TenantId.Equals(tenantId))
            {
                await EmitTenantBoundaryViolationAsync(
                    bill.Id.Value, tenantId, existing.TenantId, admittedAt, cancellationToken).ConfigureAwait(false);
                throw new ArgumentException(
                    $"Bill id '{bill.Id.Value}' already exists under a different tenant.",
                    nameof(bill));
            }
        }

        _bills[bill.Id] = bill;
    }

    /// <inheritdoc />
    public async Task<Bill?> GetAsync(
        TenantId tenantId, BillId id, DateTimeOffset admittedAt, CancellationToken cancellationToken = default)
    {
        if (!_bills.TryGetValue(id, out var bill)) return null;
        if (bill.DeletedAtUtc is not null) return null;
        if (!bill.TenantId.Equals(tenantId))
        {
            await EmitTenantBoundaryViolationAsync(
                id.Value, tenantId, bill.TenantId, admittedAt, cancellationToken).ConfigureAwait(false);
            return null;
        }
        return bill;
    }

    /// <inheritdoc />
    public Task<Bill?> GetByVendorBillNumberAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        PartyId vendorId,
        string billNumber,
        CancellationToken cancellationToken = default)
    {
        var hit = _bills.Values.FirstOrDefault(b =>
            b.DeletedAtUtc is null
            && b.TenantId.Equals(tenantId)
            && b.ChartId == chartId
            && b.VendorId == vendorId
            && string.Equals(b.BillNumber, billNumber, StringComparison.Ordinal));
        return Task.FromResult<Bill?>(hit);
    }

    /// <inheritdoc />
    public Task<Bill?> GetByExternalRefAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        string externalRef,
        CancellationToken cancellationToken = default)
    {
        var hit = _bills.Values.FirstOrDefault(b =>
            b.DeletedAtUtc is null
            && b.TenantId.Equals(tenantId)
            && b.ChartId == chartId
            && string.Equals(b.ExternalRef, externalRef, StringComparison.Ordinal));
        return Task.FromResult<Bill?>(hit);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Bill>> ListByChartAsync(TenantId tenantId, ChartOfAccountsId chartId, CancellationToken cancellationToken = default)
    {
        var rows = _bills.Values
            .Where(b => b.DeletedAtUtc is null && b.TenantId.Equals(tenantId) && b.ChartId == chartId)
            .ToList();
        return Task.FromResult<IReadOnlyList<Bill>>(rows);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Bill>> ListByVendorAsync(TenantId tenantId, ChartOfAccountsId chartId, PartyId vendorId, CancellationToken cancellationToken = default)
    {
        var rows = _bills.Values
            .Where(b => b.DeletedAtUtc is null && b.TenantId.Equals(tenantId) && b.ChartId == chartId && b.VendorId == vendorId)
            .ToList();
        return Task.FromResult<IReadOnlyList<Bill>>(rows);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Bill>> ListBySubLedgerAccountAsync(
        TenantId tenantId,
        SubLedgerAccountId subLedgerAccountId,
        CancellationToken cancellationToken = default)
    {
        // Defence-in-depth: filter on BOTH SubLedgerAccountId AND TenantId
        // per ADR 0092 posture (no ambient tenant context on this path).
        var rows = _bills.Values
            .Where(b =>
                b.DeletedAtUtc is null
                && b.TenantId.Equals(tenantId)
                && b.SubLedgerAccountId.HasValue
                && b.SubLedgerAccountId.Value == subLedgerAccountId)
            .ToList();
        return Task.FromResult<IReadOnlyList<Bill>>(rows);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Bill>> QueryOpenAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        PartyId? vendorId = null,
        string? propertyId = null,
        CancellationToken cancellationToken = default)
    {
        var rows = _bills.Values.Where(b =>
            b.DeletedAtUtc is null
            && b.TenantId.Equals(tenantId)
            && b.ChartId == chartId
            && b.Status.IsOpen()
            && (vendorId is null || b.VendorId == vendorId.Value)
            && (propertyId is null || string.Equals(b.PropertyId, propertyId, StringComparison.Ordinal)))
            .ToList();
        return Task.FromResult<IReadOnlyList<Bill>>(rows);
    }

    /// <inheritdoc />
    public async Task<bool> SoftDeleteAsync(
        TenantId tenantId, BillId id, PartyId actor, string? reason, DateTimeOffset admittedAt,
        CancellationToken cancellationToken = default)
    {
        if (!_bills.TryGetValue(id, out var bill)) return false;
        if (!bill.TenantId.Equals(tenantId))
        {
            await EmitTenantBoundaryViolationAsync(
                id.Value, tenantId, bill.TenantId, admittedAt, cancellationToken).ConfigureAwait(false);
            return false;
        }
        if (bill.DeletedAtUtc is not null) return true;

        var now = new Instant(admittedAt);
        _bills[id] = bill with
        {
            DeletedAtUtc = now,
            DeletedBy = actor,
            DeletedReason = reason,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = bill.Version + 1,
        };
        return true;
    }

    // ── Audit emission (ADR 0092 §A6 canonical payload shape — sec-eng SPOT-CHECK GREEN template) ──
    //
    // Mirror of cohort 2 PR 0a InMemoryInvoiceRepository payload shape:
    //   entity_type, entity_id, requested_tenant, actual_tenant, correlation_id
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
            ["entity_type"]       = "Bill",
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
