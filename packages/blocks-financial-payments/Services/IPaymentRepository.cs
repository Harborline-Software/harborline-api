using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.FinancialPayments.Services;

/// <summary>
/// CRUD surface over the payments substrate. Persistence-backed implementations
/// (SQLite, Postgres) shadow this binding; <see cref="InMemoryPaymentRepository"/>
/// backs the v1 desktop path.
///
/// <para>
/// <b>Cohort-2 PR 0c tenant-keying retrofit (pattern-009-tenant-keying-retrofit
/// candidate; ADR 0092 Step 1).</b> Every method takes <see cref="TenantId"/>
/// as the FIRST positional parameter. Read methods filter by tenant and return
/// null / empty on cross-tenant (uniform-404 per ADR 0092). Write methods
/// assert <c>payment.TenantId == tenantId</c>; mismatch throws
/// <see cref="ArgumentException"/>.
/// </para>
///
/// <para>
/// PR 0c is the REPOSITORY-layer companion to W#68 PR 3 Option A's
/// SERVICE-layer tenant isolation amendment. Together they provide
/// defense-in-depth: cross-tenant access is rejected at both layers.
/// </para>
/// </summary>
public interface IPaymentRepository : ITenantScopedRepository<Payment, PaymentId>
{
    /// <summary>
    /// Insert a new payment. Throws if a payment with the same id already exists.
    /// <see cref="ArgumentException"/> when <c>payment.TenantId</c> does not match
    /// <paramref name="tenantId"/>.
    /// </summary>
    Task AddAsync(TenantId tenantId, Payment payment, DateTimeOffset admittedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a payment by id. Returns null when missing OR scoped to a different
    /// tenant (uniform-404). Cross-tenant reads emit
    /// <c>AuditEventType.TenantBoundaryViolation</c> when audit emission is wired.
    /// </summary>
    Task<Payment?> GetAsync(TenantId tenantId, PaymentId id, DateTimeOffset admittedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace an existing payment record. Throws if the id is unknown OR
    /// scoped to a different tenant (cross-tenant overwrite attempt;
    /// <see cref="ArgumentException"/>).
    /// </summary>
    Task UpdateAsync(TenantId tenantId, Payment payment, DateTimeOffset admittedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find a payment by its external-ref tag (e.g. ERPNext <c>PE-0001</c> sync key),
    /// scoped to (<paramref name="tenantId"/>, <paramref name="chartId"/>). Returns
    /// null when no live payment matches or it is scoped to a different tenant
    /// (uniform-404). The idempotency lookup the ERPNext payment importer keys on.
    /// </summary>
    Task<Payment?> GetByExternalRefAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        string externalRef,
        CancellationToken cancellationToken = default);

    /// <summary>List all payments in a chart for <paramref name="tenantId"/>, ordered by <c>PaymentDate</c> descending.</summary>
    Task<IReadOnlyList<Payment>> ListByChartAsync(TenantId tenantId, ChartOfAccountsId chartId, CancellationToken cancellationToken = default);

    /// <summary>List all payments for a specific party (customer or vendor) within a chart, scoped to <paramref name="tenantId"/>.</summary>
    Task<IReadOnlyList<Payment>> ListByPartyAsync(TenantId tenantId, ChartOfAccountsId chartId, PartyId partyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Find a payment by its <see cref="Payment.SourceReference"/> idempotency key, scoped to
    /// <paramref name="tenantId"/> (ADR 0122 §D4 T2 payment-write idempotency — SourceReference ALONE,
    /// mirroring the JournalEntry posting-idempotency dedupe from P1). Returns the existing payment on a
    /// re-driven record (network retry / double-submit), or null when none matches or it is scoped to a
    /// different tenant (uniform-404). A null/empty <paramref name="sourceReference"/> never matches
    /// (manual records carry no source reference) — returns null without a store read.
    /// </summary>
    Task<Payment?> FindBySourceReferenceAsync(
        TenantId tenantId,
        string sourceReference,
        CancellationToken cancellationToken = default);
}
