using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.Leases.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using PeopleModels = Harborline.Api.Blocks.People.Foundation.Models;

namespace Harborline.Api.Blocks.Leases.Services;

/// <summary>
/// PM-pack ERPNext capture extension (ADR 0120 PR-D).
///
/// <para>
/// When CIC supplies an ERPNext dump that contains a custom Lease / Rent Contract
/// DocType, the generic ERPNext import pipeline (blocks-migration-erpnext) surfaces
/// the DocType in its <c>unmapped-unknown</c> census bucket. This service is the
/// <b>PM-pack hook</b> that processes those records AFTER the generic pipeline
/// has finished — minting (or resolving) one <see cref="SubLedgerAccount"/> and
/// writing a <see cref="LeaseSubLedgerLink"/> that ties each canonical
/// <see cref="Lease"/> to its AR sub-ledger account.
/// </para>
///
/// <para>
/// <b>D3 pack-boundary (ADR 0111):</b> this service lives in the PM pack
/// (<c>blocks-leases</c>). It delegates account creation to
/// <see cref="LeaseSubLedgerService.ActivateAsync"/>, which references only the
/// LOW identity assembly (<c>blocks-financial-subledger</c>). No AR, AP, payments,
/// or projection types cross this boundary.
/// </para>
///
/// <para>
/// <b>Idempotency:</b> <see cref="CaptureAsync"/> is safe to call multiple times
/// for the same lease. <see cref="LeaseSubLedgerService.ActivateAsync"/> is itself
/// idempotent (ExternalRef keyed mint-or-get); the capture service returns the
/// pre-existing <see cref="SubLedgerAccountId"/> without side-effects on re-import.
/// </para>
///
/// <para>
/// <b>Caller contract (ADR 0100 C6):</b> the caller resolves the ERPNext
/// <c>customer</c> name to a canonical party id and the ERPNext
/// <c>AR account</c> to a canonical <see cref="GLAccountId"/> before calling
/// <see cref="CaptureAsync"/>. This service does NOT resolve parties or accounts
/// from raw ERPNext strings — it only wires the already-resolved ids into the
/// PM-pack link record.
/// </para>
/// </summary>
public sealed class ErpNextLeaseCaptureService
{
    private readonly LeaseSubLedgerService _subledger;

    public ErpNextLeaseCaptureService(LeaseSubLedgerService subledger)
    {
        _subledger = subledger ?? throw new ArgumentNullException(nameof(subledger));
    }

    /// <summary>
    /// Process one ERPNext Lease/Rent-Contract source record: mint (or resolve) the
    /// sub-ledger account and record a <see cref="LeaseSubLedgerLink"/>.
    ///
    /// <para>
    /// If a link already exists for <paramref name="leaseId"/> the call is a no-op
    /// and returns the pre-existing <see cref="SubLedgerAccountId"/>. This makes the
    /// capture safe to call from an import re-run or a migration reconcile sweep.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Tenant scope (ADR 0092).</param>
    /// <param name="chartId">Chart-of-accounts scope.</param>
    /// <param name="leaseId">The canonical <see cref="Lease.Id"/> already resolved from the ERPNext record.</param>
    /// <param name="arControlAccountId">
    /// The AR GL control account already resolved from the ERPNext source (Asset-class,
    /// enforced by the Kind-guard in <see cref="LeaseSubLedgerService.ActivateAsync"/>).
    /// </param>
    /// <param name="primaryTenantPartyId">
    /// The primary tenant's party id already resolved from the ERPNext
    /// <c>customer</c> field.
    /// </param>
    /// <param name="source">The ERPNext source record (for idempotency key + provenance).</param>
    /// <param name="now">Clock instant for <c>CreatedAtUtc</c>.</param>
    /// <param name="cancellationToken"/>
    /// <returns>
    /// The <see cref="SubLedgerAccountId"/> of the (newly minted or pre-existing) account.
    /// </returns>
    public Task<SubLedgerAccountId> CaptureAsync(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        LeaseId leaseId,
        GLAccountId arControlAccountId,
        PeopleModels.PartyId primaryTenantPartyId,
        ErpNextLeaseContractSource source,
        Instant now,
        CancellationToken cancellationToken = default)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        // Delegate to the PM-pack service: idempotent mint + link write.
        // The Kind-guard (SubLedgerAccount.CreateWithKindGuard, GLAccountType.Asset)
        // is enforced inside ActivateAsync — mis-bound AR control accounts throw
        // ArgumentException at this seam.
        return _subledger.ActivateAsync(
            tenantId:               tenantId,
            chartId:                chartId,
            leaseId:                leaseId,
            arControlAccountId:     arControlAccountId,
            primaryTenantPartyId:   primaryTenantPartyId,
            now:                    now,
            cancellationToken:      cancellationToken);
    }
}
