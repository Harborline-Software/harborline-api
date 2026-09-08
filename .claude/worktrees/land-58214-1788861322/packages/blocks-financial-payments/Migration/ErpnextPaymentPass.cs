using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Import.Census;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialPayments.Migration;

/// <summary>
/// A4.3 ORCHESTRATOR of the ERPNext → Harborline-native migration
/// (post-MVP WBS Workstream A4.3). Runs the SHIPPED per-record
/// <see cref="IErpnextPaymentImporter"/> over an ERPNext <c>Payment Entry</c>
/// source set for one tenant + chart. Symmetric in shape with Pass 3/4.4
/// (the ledger orchestrators): tenant-first, census-conserving, reject-not-drop,
/// run-twice idempotent.
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>
///     <b>Party resolution at the orchestration boundary.</b> The per-record
///     importer takes an already-resolved <c>PartyId</c> (ADR 0100 C6 — the
///     importer consumes only the source DTO + opaque ids, never resolves a
///     party itself). The pass resolves each source's party via the supplied
///     resolver delegate; a source whose party cannot be
///     resolved is a structured <see cref="ImportOutcome{T}.Rejected"/> with
///     <see cref="ImportRejectReason.UnresolvedReference"/> — never a thrown
///     exception that aborts the pass, and never a silent drop (ADR 0100 C2/C5).
///   </item>
///   <item>
///     <b>Per-payment commit boundary.</b> Each payment upserts independently, so
///     one rejected payment does not roll back the payments that already succeeded
///     (ADR 0100 C2 table).
///   </item>
///   <item>
///     <b>Idempotent re-import (ADR 0100 C1).</b> The shipped importer dedupes on
///     the ERPNext name with an <see cref="Payment.ExternalRefVersion"/> gate. A
///     re-run of the same source set at the same/older <c>Modified</c> returns
///     <see cref="ImportOutcome{T}.Skipped"/> for every prior payment — never a
///     duplicate insert (count-stable).
///   </item>
///   <item>
///     <b>Census conservation (ADR 0100 C2).</b> Every payment outcome — including
///     the unresolved-party rejects produced at the orchestration layer — is
///     recorded into an <see cref="ImportCensus"/>; the pass calls
///     <see cref="ImportCensus.AssertConserved"/> over the full source set so a
///     vanished or double-counted payment is a loud failure.
///   </item>
/// </list>
/// <para>
/// Access-mode-agnostic (ADR 0100 C6): consumes already-parsed
/// <see cref="ErpnextPaymentSource"/> records, so the same orchestrator runs against
/// a MariaDB-dump-sourced set OR a hand-built fixture set. Tenant-scoped
/// (ADR 0100 C3): the single resolved tenant id is threaded identically into every
/// upsert — no pass derives a tenant from source data.
/// </para>
/// </remarks>
public sealed class ErpnextPaymentPass
{
    private readonly IErpnextPaymentImporter _paymentImporter;

    public ErpnextPaymentPass(IErpnextPaymentImporter paymentImporter)
    {
        _paymentImporter = paymentImporter ?? throw new ArgumentNullException(nameof(paymentImporter));
    }

    /// <summary>The ERPNext DocType this pass imports — for census + reject provenance.</summary>
    public const string DocType = "Payment Entry";

    /// <summary>
    /// Runs A4.3 over the supplied payment source set for one tenant + chart.
    /// </summary>
    /// <param name="tenantId">
    /// The single target tenant every payment is scoped to (ADR 0100 C3 —
    /// threaded from the CLI, never derived from source data).
    /// </param>
    /// <param name="payments">The full ERPNext "Payment Entry" source set.</param>
    /// <param name="targetChart">The destination chart-of-accounts.</param>
    /// <param name="resolveParty">
    /// Resolves a source record to its canonical <c>PartyId</c> (the customer for a
    /// Receive, the vendor for a Pay). Returns <see langword="null"/> when the party
    /// cannot be resolved — the pass turns a null into a structured reject, so party
    /// resolution stays the orchestration boundary's concern, not the importer's.
    /// </param>
    /// <param name="writeAuthority">The authenticated principal, tenant, and instant sampled once for the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A conserved <see cref="PaymentImportResult"/>.</returns>
    /// <exception cref="ImportCensusViolationException">
    /// Thrown only if the census fails conservation over the source set — a
    /// defensive invariant that should never fire given the exhaustive recording below.
    /// </exception>
    public async Task<PaymentImportResult> RunAsync(
        TenantId tenantId,
        IReadOnlyList<ErpnextPaymentSource> payments,
        ChartOfAccountsId targetChart,
        Func<ErpnextPaymentSource, PartyId?> resolveParty,
        AuthorizationWriteContext writeAuthority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payments);
        ArgumentNullException.ThrowIfNull(resolveParty);
        if (writeAuthority.Tenant != tenantId)
            throw new ArgumentException("The payment-import tenant does not match the run authority.", nameof(writeAuthority));

        var census = new ImportCensus();
        var outcomes = new List<ImportOutcome<Payment>>(payments.Count);

        foreach (var source in payments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var partyId = resolveParty(source);
            if (partyId is null)
            {
                // Unresolved party: reject at the orchestration layer (the importer
                // never resolves a party). Counted, never dropped (ADR 0100 C2/C5).
                var rejected = new ImportOutcome<Payment>.Rejected(
                    ImportFailure.Of(
                        externalRef: source.Name,
                        docType: DocType,
                        reason: ImportRejectReason.UnresolvedReference,
                        fieldName: "party",
                        ruleViolated: "party could not be resolved to a canonical Harborline party"));
                census.Record(rejected);
                outcomes.Add(rejected);
                continue;
            }

            var outcome = await _paymentImporter
                .UpsertPaymentAsync(tenantId, source, targetChart, partyId.Value, writeAuthority, cancellationToken)
                .ConfigureAwait(false);
            census.Record(outcome);
            outcomes.Add(outcome);
        }

        // Conservation gate (ADR 0100 C2): the full source set is fully accounted for.
        census.AssertConserved(payments.Count);

        return new PaymentImportResult(census, outcomes);
    }
}
