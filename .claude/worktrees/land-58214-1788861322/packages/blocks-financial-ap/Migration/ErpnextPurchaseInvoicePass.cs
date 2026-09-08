using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Import.Census;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialAp.Migration;

/// <summary>
/// A4.2 ORCHESTRATOR of the ERPNext → Harborline-native migration
/// (post-MVP WBS Workstream A4.2). Runs the SHIPPED per-record
/// <see cref="IErpnextPurchaseInvoiceImporter"/> over an ERPNext
/// <c>Purchase Invoice</c> source set for one tenant + chart. Symmetric in shape
/// with the AR Pass A4.1 and the ledger Pass 3/4.4 orchestrators: tenant-first,
/// census-conserving, reject-not-drop, run-twice idempotent.
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>
///     <b>Supplier resolution at the orchestration boundary.</b> The per-record
///     importer takes an already-resolved vendor <c>PartyId</c> (ADR 0100 C6 —
///     the importer consumes only the source DTO + opaque ids, never resolves a
///     party itself; party resolution is Pass-1's job upstream). The pass
///     resolves each source's supplier via the supplied resolver delegate; a
///     source whose supplier cannot be resolved is a structured
///     <see cref="ImportOutcome{T}.Rejected"/> with
///     <see cref="ImportRejectReason.UnresolvedReference"/> — never a thrown
///     exception that aborts the pass, and never a silent drop (ADR 0100 C2/C5).
///   </item>
///   <item>
///     <b>Per-bill commit boundary.</b> Each bill upserts independently, so one
///     rejected bill does not roll back the bills that already succeeded
///     (ADR 0100 C2 table).
///   </item>
///   <item>
///     <b>Idempotent re-import (ADR 0100 C1/C7).</b> The shipped importer dedupes
///     on <c>ExternalRef == "erpnext:pinv:{Name}"</c> with a
///     <c>Bill.ExternalRefVersion</c> gate. A re-run of the same source set at the
///     same/older <c>Modified</c> returns <see cref="ImportOutcome{T}.Skipped"/>
///     for every prior bill — never a duplicate insert (count-stable).
///   </item>
///   <item>
///     <b>Census conservation (ADR 0100 C2).</b> Every bill outcome — including
///     the unresolved-supplier rejects produced at the orchestration layer — is
///     recorded into an <see cref="ImportCensus"/>; the pass calls
///     <see cref="ImportCensus.AssertConserved"/> over the full source set so a
///     vanished or double-counted bill is a loud failure.
///   </item>
/// </list>
/// <para>
/// Access-mode-agnostic (ADR 0100 C6): consumes already-parsed
/// <see cref="ErpnextPurchaseInvoiceSource"/> records, so the same orchestrator runs
/// against a MariaDB-dump-sourced set OR a hand-built fixture set. Tenant-scoped
/// (ADR 0100 C3): the single resolved tenant id is threaded identically into every
/// upsert — no pass derives a tenant from source data.
/// </para>
/// </remarks>
public sealed class ErpnextPurchaseInvoicePass
{
    private readonly IErpnextPurchaseInvoiceImporter _invoiceImporter;

    public ErpnextPurchaseInvoicePass(IErpnextPurchaseInvoiceImporter invoiceImporter)
    {
        _invoiceImporter = invoiceImporter ?? throw new ArgumentNullException(nameof(invoiceImporter));
    }

    /// <summary>The ERPNext DocType this pass imports — for census + reject provenance.</summary>
    public const string DocType = "Purchase Invoice";

    /// <summary>
    /// Runs A4.2 over the supplied purchase-invoice source set for one tenant + chart.
    /// </summary>
    /// <param name="tenantId">
    /// The single target tenant every bill is scoped to (ADR 0100 C3 — threaded
    /// from the CLI, never derived from source data).
    /// </param>
    /// <param name="invoices">The full ERPNext "Purchase Invoice" source set.</param>
    /// <param name="targetChart">The destination chart-of-accounts.</param>
    /// <param name="apAccountId">The AP control account every imported bill posts against.</param>
    /// <param name="defaultExpenseAccountId">Expense account used when a source line carries no expense account.</param>
    /// <param name="resolveSupplier">
    /// Resolves a source record to its canonical vendor <c>PartyId</c>. Returns
    /// <see langword="null"/> when the supplier cannot be resolved — the pass turns
    /// a null into a structured reject, so supplier resolution stays the
    /// orchestration boundary's concern, not the importer's.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A conserved <see cref="PurchaseInvoiceImportResult"/>.</returns>
    /// <exception cref="ImportCensusViolationException">
    /// Thrown only if the census fails conservation over the source set — a
    /// defensive invariant that should never fire given the exhaustive recording below.
    /// </exception>
    public async Task<PurchaseInvoiceImportResult> RunAsync(
        TenantId tenantId,
        IReadOnlyList<ErpnextPurchaseInvoiceSource> invoices,
        ChartOfAccountsId targetChart,
        GLAccountId apAccountId,
        GLAccountId defaultExpenseAccountId,
        Func<ErpnextPurchaseInvoiceSource, PartyId?> resolveSupplier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(resolveSupplier);

        var census = new ImportCensus();
        var outcomes = new List<ImportOutcome<Bill>>(invoices.Count);

        foreach (var source in invoices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var supplierId = resolveSupplier(source);
            if (supplierId is null)
            {
                // Unresolved supplier: reject at the orchestration layer (the importer
                // never resolves a party). Counted, never dropped (ADR 0100 C2/C5).
                var rejected = new ImportOutcome<Bill>.Rejected(
                    ImportFailure.Of(
                        externalRef: source.Name,
                        docType: DocType,
                        reason: ImportRejectReason.UnresolvedReference,
                        fieldName: "supplier",
                        ruleViolated: "supplier could not be resolved to a canonical Harborline party"));
                census.Record(rejected);
                outcomes.Add(rejected);
                continue;
            }

            var outcome = await _invoiceImporter
                .UpsertPurchaseInvoiceAsync(
                    tenantId,
                    source,
                    targetChart,
                    supplierId.Value,
                    apAccountId,
                    defaultExpenseAccountId,
                    cancellationToken)
                .ConfigureAwait(false);
            census.Record(outcome);
            outcomes.Add(outcome);
        }

        // Conservation gate (ADR 0100 C2): the full source set is fully accounted for.
        census.AssertConserved(invoices.Count);

        return new PurchaseInvoiceImportResult(census, outcomes);
    }
}
