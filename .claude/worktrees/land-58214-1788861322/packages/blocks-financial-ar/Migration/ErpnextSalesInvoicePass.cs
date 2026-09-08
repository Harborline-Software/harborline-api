using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Import.Census;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialAr.Migration;

/// <summary>
/// A4.1 ORCHESTRATOR of the ERPNext → Harborline-native migration
/// (post-MVP WBS Workstream A4.1). Runs the SHIPPED per-record
/// <see cref="IErpnextSalesInvoiceImporter"/> over an ERPNext
/// <c>Sales Invoice</c> source set for one tenant + chart. Symmetric in shape
/// with the ledger Pass 3/4.4 orchestrators: tenant-first, census-conserving,
/// reject-not-drop, run-twice idempotent.
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>
///     <b>Customer resolution at the orchestration boundary.</b> The per-record
///     importer takes an already-resolved customer <c>PartyId</c> (ADR 0100 C6 —
///     the importer consumes only the source DTO + opaque ids, never resolves a
///     party itself; party resolution is Pass-1's job upstream). The pass
///     resolves each source's customer via the supplied resolver delegate; a
///     source whose customer cannot be resolved is a structured
///     <see cref="ImportOutcome{T}.Rejected"/> with
///     <see cref="ImportRejectReason.UnresolvedReference"/> — never a thrown
///     exception that aborts the pass, and never a silent drop (ADR 0100 C2/C5).
///   </item>
///   <item>
///     <b>Per-invoice commit boundary.</b> Each invoice upserts independently, so
///     one rejected invoice does not roll back the invoices that already
///     succeeded (ADR 0100 C2 table).
///   </item>
///   <item>
///     <b>Idempotent re-import (ADR 0100 C1).</b> The shipped importer dedupes on
///     <c>ExternalRef == "erpnext:sinv:{Name}"</c> with a source-<c>Modified</c>
///     version gate. A re-run of the same source set at the same/older
///     <c>Modified</c> returns <see cref="ImportOutcome{T}.Skipped"/> for every
///     prior invoice — never a duplicate insert (count-stable).
///   </item>
///   <item>
///     <b>Census conservation (ADR 0100 C2).</b> Every invoice outcome — including
///     the unresolved-customer rejects produced at the orchestration layer — is
///     recorded into an <see cref="ImportCensus"/>; the pass calls
///     <see cref="ImportCensus.AssertConserved"/> over the full source set so a
///     vanished or double-counted invoice is a loud failure.
///   </item>
/// </list>
/// <para>
/// Access-mode-agnostic (ADR 0100 C6): consumes already-parsed
/// <see cref="ErpnextSalesInvoiceSource"/> records, so the same orchestrator runs
/// against a MariaDB-dump-sourced set OR a hand-built fixture set. Tenant-scoped
/// (ADR 0100 C3): the single resolved tenant id is threaded identically into every
/// upsert — no pass derives a tenant from source data.
/// </para>
/// </remarks>
public sealed class ErpnextSalesInvoicePass
{
    private readonly IErpnextSalesInvoiceImporter _invoiceImporter;

    public ErpnextSalesInvoicePass(IErpnextSalesInvoiceImporter invoiceImporter)
    {
        _invoiceImporter = invoiceImporter ?? throw new ArgumentNullException(nameof(invoiceImporter));
    }

    /// <summary>The ERPNext DocType this pass imports — for census + reject provenance.</summary>
    public const string DocType = "Sales Invoice";

    /// <summary>
    /// Runs A4.1 over the supplied sales-invoice source set for one tenant + chart.
    /// </summary>
    /// <param name="tenantId">
    /// The single target tenant every invoice is scoped to (ADR 0100 C3 —
    /// threaded from the CLI, never derived from source data).
    /// </param>
    /// <param name="invoices">The full ERPNext "Sales Invoice" source set.</param>
    /// <param name="targetChart">The destination chart-of-accounts.</param>
    /// <param name="arAccountId">The AR control account every imported invoice posts against.</param>
    /// <param name="defaultIncomeAccountId">Income account used when a source line carries no income account.</param>
    /// <param name="resolveCustomer">
    /// Resolves a source record to its canonical customer <c>PartyId</c>. Returns
    /// <see langword="null"/> when the customer cannot be resolved — the pass turns
    /// a null into a structured reject, so customer resolution stays the
    /// orchestration boundary's concern, not the importer's.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A conserved <see cref="SalesInvoiceImportResult"/>.</returns>
    /// <exception cref="ImportCensusViolationException">
    /// Thrown only if the census fails conservation over the source set — a
    /// defensive invariant that should never fire given the exhaustive recording below.
    /// </exception>
    public async Task<SalesInvoiceImportResult> RunAsync(
        TenantId tenantId,
        IReadOnlyList<ErpnextSalesInvoiceSource> invoices,
        ChartOfAccountsId targetChart,
        GLAccountId arAccountId,
        GLAccountId defaultIncomeAccountId,
        Func<ErpnextSalesInvoiceSource, PartyId?> resolveCustomer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(resolveCustomer);

        var census = new ImportCensus();
        var outcomes = new List<ImportOutcome<Invoice>>(invoices.Count);

        foreach (var source in invoices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var customerId = resolveCustomer(source);
            if (customerId is null)
            {
                // Unresolved customer: reject at the orchestration layer (the importer
                // never resolves a party). Counted, never dropped (ADR 0100 C2/C5).
                var rejected = new ImportOutcome<Invoice>.Rejected(
                    ImportFailure.Of(
                        externalRef: source.Name,
                        docType: DocType,
                        reason: ImportRejectReason.UnresolvedReference,
                        fieldName: "customer",
                        ruleViolated: "customer could not be resolved to a canonical Harborline party"));
                census.Record(rejected);
                outcomes.Add(rejected);
                continue;
            }

            var outcome = await _invoiceImporter
                .UpsertSalesInvoiceAsync(
                    tenantId,
                    source,
                    targetChart,
                    customerId.Value,
                    arAccountId,
                    defaultIncomeAccountId,
                    cancellationToken)
                .ConfigureAwait(false);
            census.Record(outcome);
            outcomes.Add(outcome);
        }

        // Conservation gate (ADR 0100 C2): the full source set is fully accounted for.
        census.AssertConserved(invoices.Count);

        return new SalesInvoiceImportResult(census, outcomes);
    }
}
