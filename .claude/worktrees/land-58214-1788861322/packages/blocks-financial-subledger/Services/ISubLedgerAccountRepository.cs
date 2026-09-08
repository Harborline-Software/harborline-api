using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.FinancialSubLedger.Services;

/// <summary>
/// CRUD surface over the <see cref="SubLedgerAccount"/> store (ADR 0120 PR-B).
///
/// <para>
/// <b>Tenant-keying posture (ADR 0092):</b> every method takes
/// <see cref="TenantId"/> as the FIRST positional parameter. Read methods
/// filter by tenant and return null / empty on cross-tenant (uniform-404
/// invariant — no diagnostic leak). Write methods assert
/// <c>account.TenantId == tenantId</c>; mismatch throws
/// <see cref="ArgumentException"/>.
/// </para>
///
/// <para>
/// <b>Composite keying:</b> the in-memory store keys on
/// <c>(TenantId, SubLedgerAccountId)</c> — matching the financial-cluster
/// posture established in AR / AP / Payments.
/// </para>
/// </summary>
public interface ISubLedgerAccountRepository : ITenantScopedRepository<SubLedgerAccount, SubLedgerAccountId>
{
    /// <summary>
    /// Insert or update a sub-ledger account. Throws on a tombstoned target.
    /// <see cref="ArgumentException"/> when <c>account.TenantId</c> does not
    /// match <paramref name="tenantId"/>.
    /// </summary>
    Task UpsertAsync(TenantId tenantId, SubLedgerAccount account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get an account by id. Returns null when missing OR scoped to a
    /// different tenant (uniform-404 invariant — no diagnostic leak).
    /// </summary>
    Task<SubLedgerAccount?> GetAsync(
        TenantId tenantId,
        SubLedgerAccountId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find an account by external-ref tag (e.g. ERPNext ref) scoped to
    /// <c>(tenantId, chartId)</c>. Returns null on miss OR cross-tenant.
    /// Used by the ERPNext importer for idempotent mint-or-get.
    /// </summary>
    Task<SubLedgerAccount?> GetByExternalRefAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        string externalRef,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List all active sub-ledger accounts in a chart for
    /// <paramref name="tenantId"/>. Active = <see cref="SubLedgerAccount.IsActive"/> is true.
    /// </summary>
    Task<IReadOnlyList<SubLedgerAccount>> ListByChartAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List all sub-ledger accounts (active and soft-closed) in a chart for
    /// <paramref name="tenantId"/>. Needed for R1 reconciliation sweeps.
    /// </summary>
    Task<IReadOnlyList<SubLedgerAccount>> ListAllByChartAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List all sub-ledger accounts for a specific <paramref name="partyId"/>
    /// within a chart, scoped to <paramref name="tenantId"/>.
    /// </summary>
    Task<IReadOnlyList<SubLedgerAccount>> ListByPartyAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        PartyId partyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List all sub-ledger accounts whose <see cref="SubLedgerAccount.ControlAccountId"/>
    /// equals <paramref name="controlAccountId"/> in a chart, scoped to
    /// <paramref name="tenantId"/>. Used by R1 reconciliation.
    /// </summary>
    Task<IReadOnlyList<SubLedgerAccount>> ListByControlAccountAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        GLAccountId controlAccountId,
        CancellationToken cancellationToken = default);
}
