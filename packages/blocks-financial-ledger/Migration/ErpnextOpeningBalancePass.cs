using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Import.Census;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// Pass 3 ORCHESTRATOR of the ERPNext → Harborline-native migration
/// (post-MVP WBS Workstream A3; migration-importer spec §4.3). Runs the SHIPPED
/// per-record <see cref="IErpnextJournalEntryImporter"/> over the OPENING-balance
/// journal entries (ERPNext <c>is_opening == "Yes"</c>) for one tenant + chart.
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>
///     <b>Opening filter.</b> Only <see cref="ErpnextJournalEntrySource.IsOpening"/>
///     entries are this pass's concern; non-opening JEs are routed by Pass 4.4
///     (<see cref="ErpnextJournalEntryImporter"/> standalone-JE orchestration). The
///     census conserves over the OPENING subset the pass is asked to import — the
///     pass reports both the opening count and the non-opening count it skipped so
///     the orchestrator's report shows no record vanished from the full source set
///     (ADR 0100 C2; the full-source census is the A7 orchestrator's responsibility).
///   </item>
///   <item>
///     <b>Per-JE balance gate.</b> An opening JE MUST balance (Σdebit == Σcredit;
///     spec §4.3). The gate runs <i>before</i> the upsert so an imbalanced opening
///     entry is a structured <see cref="ImportOutcome{T}.Rejected"/> with
///     <see cref="ImportRejectReason.ConstraintViolation"/> — never a thrown
///     exception that aborts the whole pass, and never a silent skip (ADR 0100
///     C2/C5 "no record vanishes"). The shipped importer ALSO rejects imbalance at
///     its <c>JournalEntry</c> ctor; this pre-gate makes the opening-balance
///     invariant a first-class, separately-reported reason at the orchestration
///     layer rather than relying on the downstream ctor message.
///   </item>
///   <item>
///     <b>Per-JE transaction.</b> Pass 3's commit boundary is per-JE (ADR 0100 C2
///     table): each opening entry posts independently, so one rejected/imbalanced
///     entry does not roll back the entries that already succeeded. The pass
///     completes if any opening JE succeeds.
///   </item>
///   <item>
///     <b>Census conservation.</b> Every opening-JE outcome is recorded into an
///     <see cref="ImportCensus"/>; the pass calls
///     <see cref="ImportCensus.AssertConserved"/> over the opening subset so a
///     vanished or double-counted opening entry is a loud failure (ADR 0100 C2).
///   </item>
///   <item>
///     <b>Opening trial-balance aggregate.</b> The pass tallies the Σdebit / Σcredit
///     of the opening entries that successfully imported — the opening trial balance
///     that Pass 6 (<c>A6</c>) reconciles. A non-zero net is surfaced for the
///     migration report, NOT silently corrected.
///   </item>
/// </list>
/// <para>
/// Access-mode-agnostic (ADR 0100 C6): consumes already-parsed
/// <see cref="ErpnextJournalEntrySource"/> records, so the same orchestrator runs
/// against a MariaDB-dump-sourced set OR a hand-built fixture set. Tenant-scoped
/// (ADR 0100 C3): the single resolved tenant id is threaded identically into every
/// upsert — no pass derives a tenant from source data.
/// </para>
/// </remarks>
public sealed class ErpnextOpeningBalancePass
{
    private readonly IErpnextJournalEntryImporter _journalImporter;

    public ErpnextOpeningBalancePass(IErpnextJournalEntryImporter journalImporter)
    {
        _journalImporter = journalImporter ?? throw new ArgumentNullException(nameof(journalImporter));
    }

    /// <summary>The ERPNext DocType this pass imports — for census + reject provenance.</summary>
    public const string DocType = "Journal Entry";

    /// <summary>
    /// Runs Pass 3 over the supplied journal-entry set for one tenant + chart.
    /// </summary>
    /// <param name="tenantId">
    /// The single target tenant every opening entry is scoped to (ADR 0100 C3 —
    /// threaded from the CLI, never derived from source data).
    /// </param>
    /// <param name="journalEntries">
    /// The full ERPNext "Journal Entry" set. The pass imports only the
    /// <see cref="ErpnextJournalEntrySource.IsOpening"/> subset; the rest are counted
    /// as deferred-to-Pass-4.4 and reported, not imported.
    /// </param>
    /// <param name="targetChart">The destination chart-of-accounts the entries post into.</param>
    /// <param name="authority">Server-derived authority for this import act.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A conserved <see cref="OpeningBalanceImportResult"/>.</returns>
    /// <exception cref="ImportCensusViolationException">
    /// Thrown only if the census fails conservation over the opening subset — a
    /// defensive invariant that should never fire given the exhaustive recording below.
    /// </exception>
    public async Task<OpeningBalanceImportResult> RunAsync(
        TenantId tenantId,
        IReadOnlyList<ErpnextJournalEntrySource> journalEntries,
        ChartOfAccountsId targetChart,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journalEntries);

        var census = new ImportCensus();
        var opening = new List<ErpnextJournalEntrySource>(journalEntries.Count);
        var nonOpeningCount = 0;

        foreach (var je in journalEntries)
        {
            if (je.IsOpening)
            {
                opening.Add(je);
            }
            else
            {
                nonOpeningCount++;
            }
        }

        var outcomes = new List<ImportOutcome<JournalEntry>>(opening.Count);
        decimal openingDebits = 0m;
        decimal openingCredits = 0m;

        foreach (var source in opening)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Per-JE balance gate (spec §4.3): an opening entry must balance.
            // Reject BEFORE the upsert so imbalance is a first-class, separately
            // reasoned reject at the orchestration layer (ADR 0100 C2/C5).
            var (debits, credits) = SumLines(source);
            if (debits != credits)
            {
                var rejected = new ImportOutcome<JournalEntry>.Rejected(
                    ImportFailure.Of(
                        externalRef: source.Name,
                        docType: DocType,
                        reason: ImportRejectReason.ConstraintViolation,
                        fieldName: "accounts",
                        ruleViolated:
                            $"opening journal entry is imbalanced: " +
                            $"total debits ({debits:F2}) != total credits ({credits:F2})"));
                census.Record(rejected);
                outcomes.Add(rejected);
                continue;
            }

            var outcome = await _journalImporter
                .UpsertFromErpnextAsync(tenantId, source, targetChart, authority, cancellationToken)
                .ConfigureAwait(false);
            census.Record(outcome);
            outcomes.Add(outcome);

            // Opening trial-balance aggregate: tally only entries that landed a
            // local record (Inserted/Updated/Skipped carry the posted entry). A
            // Rejected entry produced no local record, so it contributes nothing.
            if (!outcome.IsRejected)
            {
                openingDebits += debits;
                openingCredits += credits;
            }
        }

        // Conservation gate (ADR 0100 C2): the opening subset is fully accounted for.
        census.AssertConserved(opening.Count);

        return new OpeningBalanceImportResult(
            census,
            outcomes,
            nonOpeningCount,
            openingDebits,
            openingCredits);
    }

    /// <summary>Sums the debit and credit columns of a source JE's lines (account-currency).</summary>
    private static (decimal Debits, decimal Credits) SumLines(ErpnextJournalEntrySource source)
    {
        decimal debits = 0m;
        decimal credits = 0m;
        foreach (var line in source.Lines)
        {
            debits += line.DebitInAccountCurrency;
            credits += line.CreditInAccountCurrency;
        }

        return (debits, credits);
    }
}
