using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Import.Census;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.FinancialLedger.Migration;

/// <summary>
/// Pass 4.4 ORCHESTRATOR of the ERPNext → Harborline-native migration
/// (post-MVP WBS Workstream A4.4; migration-importer spec §4.4). Runs the SHIPPED
/// per-record <see cref="IErpnextJournalEntryImporter"/> over the STANDALONE
/// (non-opening, submitted) journal entries — the symmetric complement to Pass 3
/// (the opening-balance pass), which owns the opening subset.
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>
///     <b>Standalone filter.</b> Only the <c>!</c><see cref="ErpnextJournalEntrySource.IsOpening"/>
///     entries are this pass's concern; opening JEs are owned by Pass 3
///     (the opening-balance pass). The census conserves over the
///     SUBMITTED-STANDALONE subset the pass actually imports — the pass reports the
///     opening count and the non-submitted count it partitioned out so the
///     orchestrator's report shows no record vanished from the full source set
///     (ADR 0100 C2; the full-source census is the A7 orchestrator's responsibility).
///   </item>
///   <item>
///     <b>DocStatus partition.</b> ERPNext <c>docstatus</c> semantics: <c>0</c> =
///     Draft, <c>1</c> = Submitted, <c>2</c> = Cancelled. Only <c>docstatus == 1</c>
///     (Submitted) standalone entries are in this pass's import scope — those are the
///     records the Pass-6 reconciliation gate counts (ADR 0100 §"Enforcement
///     invariant": "zero unaccounted <c>docstatus==1</c> records"). Draft/Cancelled
///     standalone entries are partitioned out and COUNTED
///     (<see cref="StandaloneJournalEntryImportResult.NonSubmittedCount"/>), never
///     silently dropped (ADR 0100 C2 "no record vanishes"). They are NOT routed into
///     the census because they were never in scope to import — like opening entries,
///     they are a reported-but-deferred partition, not a reject. (A reject is for an
///     in-scope record that FAILED; a non-submitted entry was never in scope.)
///   </item>
///   <item>
///     <b>Per-JE transaction.</b> Pass 4.4's commit boundary is per-JE (ADR 0100 C2
///     table): each submitted standalone entry posts independently, so one
///     rejected/imbalanced entry does not roll back the entries that already succeeded.
///   </item>
///   <item>
///     <b>Idempotent re-import.</b> The shipped importer is idempotent on
///     <c>ExternalRef == source.Name</c> (posted entries are immutable). A re-run of
///     the same source set returns <see cref="ImportOutcome{T}.Skipped"/> for the
///     already-posted entries — never a duplicate insert (ADR 0100 C1).
///   </item>
///   <item>
///     <b>Census conservation.</b> Every submitted-standalone-JE outcome is recorded
///     into an <see cref="ImportCensus"/>; the pass calls
///     <see cref="ImportCensus.AssertConserved"/> over the submitted-standalone subset
///     so a vanished or double-counted standalone entry is a loud failure (ADR 0100 C2).
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
public sealed class ErpnextStandaloneJournalEntryPass
{
    private readonly IErpnextJournalEntryImporter _journalImporter;

    public ErpnextStandaloneJournalEntryPass(IErpnextJournalEntryImporter journalImporter)
    {
        _journalImporter = journalImporter ?? throw new ArgumentNullException(nameof(journalImporter));
    }

    /// <summary>The ERPNext DocType this pass imports — for census + reject provenance.</summary>
    public const string DocType = "Journal Entry";

    /// <summary>The ERPNext <c>docstatus</c> value for a Submitted document — the only state Pass 4.4 imports.</summary>
    private const int DocStatusSubmitted = 1;

    /// <summary>
    /// Runs Pass 4.4 over the supplied journal-entry set for one tenant + chart.
    /// </summary>
    /// <param name="tenantId">
    /// The single target tenant every standalone entry is scoped to (ADR 0100 C3 —
    /// threaded from the CLI, never derived from source data).
    /// </param>
    /// <param name="journalEntries">
    /// The full ERPNext "Journal Entry" set. The pass imports only the non-opening,
    /// <c>docstatus == 1</c> subset; opening entries are counted as deferred-to-Pass-3,
    /// and non-submitted standalone entries are counted as non-submitted — both
    /// reported, not imported.
    /// </param>
    /// <param name="targetChart">The destination chart-of-accounts the entries post into.</param>
    /// <param name="authority">Server-derived authority for this import act.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A conserved <see cref="StandaloneJournalEntryImportResult"/>.</returns>
    /// <exception cref="ImportCensusViolationException">
    /// Thrown only if the census fails conservation over the submitted-standalone
    /// subset — a defensive invariant that should never fire given the exhaustive
    /// recording below.
    /// </exception>
    public async Task<StandaloneJournalEntryImportResult> RunAsync(
        TenantId tenantId,
        IReadOnlyList<ErpnextJournalEntrySource> journalEntries,
        ChartOfAccountsId targetChart,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journalEntries);

        var census = new ImportCensus();
        var submitted = new List<ErpnextJournalEntrySource>(journalEntries.Count);
        var openingCount = 0;
        var nonSubmittedCount = 0;

        foreach (var je in journalEntries)
        {
            if (je.IsOpening)
            {
                // Owned by Pass 3 (ErpnextOpeningBalancePass); reported, not imported here.
                openingCount++;
            }
            else if (je.DocStatus != DocStatusSubmitted)
            {
                // Draft/Cancelled standalone entry: out of import scope (the
                // reconciliation gate counts docstatus==1 only). Counted, not dropped.
                nonSubmittedCount++;
            }
            else
            {
                submitted.Add(je);
            }
        }

        var outcomes = new List<ImportOutcome<JournalEntry>>(submitted.Count);

        foreach (var source in submitted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The shipped importer is the single owner of resolve → balance → post,
            // and returns the foundation-import DU directly. Pass 4.4 records EVERY
            // outcome into the census — no result is discarded (ADR 0100 C2; the
            // test-eng silent-drop verdict). Imbalance and unresolved-account come
            // back as the Rejected arm; an already-posted entry as Skipped.
            var outcome = await _journalImporter
                .UpsertFromErpnextAsync(tenantId, source, targetChart, authority, cancellationToken)
                .ConfigureAwait(false);
            census.Record(outcome);
            outcomes.Add(outcome);
        }

        // Conservation gate (ADR 0100 C2): the submitted-standalone subset is fully accounted for.
        census.AssertConserved(submitted.Count);

        return new StandaloneJournalEntryImportResult(
            census,
            outcomes,
            openingCount,
            nonSubmittedCount);
    }
}
