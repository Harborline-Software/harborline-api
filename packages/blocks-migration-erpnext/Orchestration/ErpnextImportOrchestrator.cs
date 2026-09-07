using System.Diagnostics;
using Harborline.Api.Blocks.FinancialAp.Migration;
using Harborline.Api.Blocks.FinancialAr.Migration;
using Harborline.Api.Blocks.FinancialLedger.Migration;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPayments.Migration;
using Harborline.Api.Blocks.FinancialPeriods.Migration;
using Harborline.Api.Blocks.FinancialTax.Migration;
using Harborline.Api.Blocks.Migration.Erpnext.Extraction;
using Harborline.Api.Blocks.Migration.Erpnext.Reconciliation;
using Harborline.Api.Blocks.Migration.Erpnext.Reporting;
using Harborline.Api.Blocks.Migration.Erpnext.Verification;
using Harborline.Api.Blocks.People.Foundation.Migration;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Import.Outcomes;

namespace Harborline.Api.Blocks.Migration.Erpnext.Orchestration;

/// <summary>
/// The top-level ERPNext-import orchestrator (A7): runs the six-pass algorithm (spec §4) against one
/// destination chart inside a single unit of work, gathers the reject bin / cost-center resolutions /
/// warnings / per-pass durations into a <see cref="MigrationReportInput"/>, and decides the commit vs
/// rollback branch at the end. Every per-pass class is composed here; the orchestrator owns the
/// sequencing, the cross-pass plumbing (party-resolution maps, the materialized journal-entry set),
/// and the halt gate — not the per-record import logic, which lives in the passes/upserters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure of disk + console.</b> The orchestrator never touches the filesystem or
/// <see cref="System.Console"/>: it consumes an injected <see cref="IErpnextSourceExtractor"/> (the
/// CLI host opened the dump), drives progress through the <see cref="IImportProgress"/> seam (the
/// host renders the TTY), and returns a fully-populated <see cref="MigrationReportInput"/> with
/// <see cref="ErpnextImportRunResult.ReportPath"/> always <see langword="null"/> — the host renders +
/// writes <c>migration-report.md</c>. This keeps the orchestrator unit-testable against synthetic
/// in-memory fixtures (ADR 0100; spec §4.6).
/// </para>
/// <para>
/// <b>Allowlist-clean progress (ADR 0100 C9).</b> Every <see cref="IImportProgress"/> call carries
/// only pass labels and opaque counts — never PII, monetary amounts, account values, or record
/// contents. The migration report (a CO-facing inspection artifact, not a log) legitimately carries
/// monetary diffs + opaque ids, but still no PII.
/// </para>
/// </remarks>
public sealed class ErpnextImportOrchestrator
{
    private readonly IErpnextSourceExtractor _extractor;
    private readonly ErpnextChartImportPass _chartPass;
    private readonly IPass2PartyUpserter _partyUpserter;
    private readonly IPass2PeriodUpserter _periodUpserter;
    private readonly IPass2TaxUpserter _taxUpserter;
    private readonly ErpnextOpeningBalancePass _openingBalancePass;
    private readonly ErpnextSalesInvoicePass _salesInvoicePass;
    private readonly ErpnextPurchaseInvoicePass _purchaseInvoicePass;
    private readonly ErpnextPaymentPass _paymentPass;
    private readonly ErpnextStandaloneJournalEntryPass _standaloneJournalEntryPass;
    private readonly ErpnextReconciliationPass _reconciliationPass;
    private readonly ErpnextVerificationPass _verificationPass;
    private readonly IImportUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Compose the orchestrator from the six passes (Pass 2 split into its three upserters), the source extractor, and the run-scoped unit of work.</summary>
    public ErpnextImportOrchestrator(
        IErpnextSourceExtractor extractor,
        ErpnextChartImportPass chartPass,
        IPass2PartyUpserter partyUpserter,
        IPass2PeriodUpserter periodUpserter,
        IPass2TaxUpserter taxUpserter,
        ErpnextOpeningBalancePass openingBalancePass,
        ErpnextSalesInvoicePass salesInvoicePass,
        ErpnextPurchaseInvoicePass purchaseInvoicePass,
        ErpnextPaymentPass paymentPass,
        ErpnextStandaloneJournalEntryPass standaloneJournalEntryPass,
        ErpnextReconciliationPass reconciliationPass,
        ErpnextVerificationPass verificationPass,
        IImportUnitOfWork unitOfWork,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(chartPass);
        ArgumentNullException.ThrowIfNull(partyUpserter);
        ArgumentNullException.ThrowIfNull(periodUpserter);
        ArgumentNullException.ThrowIfNull(taxUpserter);
        ArgumentNullException.ThrowIfNull(openingBalancePass);
        ArgumentNullException.ThrowIfNull(salesInvoicePass);
        ArgumentNullException.ThrowIfNull(purchaseInvoicePass);
        ArgumentNullException.ThrowIfNull(paymentPass);
        ArgumentNullException.ThrowIfNull(standaloneJournalEntryPass);
        ArgumentNullException.ThrowIfNull(reconciliationPass);
        ArgumentNullException.ThrowIfNull(verificationPass);
        ArgumentNullException.ThrowIfNull(unitOfWork);

        _extractor = extractor;
        _chartPass = chartPass;
        _partyUpserter = partyUpserter;
        _periodUpserter = periodUpserter;
        _taxUpserter = taxUpserter;
        _openingBalancePass = openingBalancePass;
        _salesInvoicePass = salesInvoicePass;
        _purchaseInvoicePass = purchaseInvoicePass;
        _paymentPass = paymentPass;
        _standaloneJournalEntryPass = standaloneJournalEntryPass;
        _reconciliationPass = reconciliationPass;
        _verificationPass = verificationPass;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Run the full import for one request inside one unit of work and return its outcome + report.
    /// </summary>
    /// <param name="request">Everything the run needs: tenant, target chart, actor, as-of, control accounts, options, snapshots.</param>
    /// <param name="progress">Progress seam; <see cref="NullImportProgress.Instance"/> is used when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The terminal status + halt reason + fully-populated report (<see cref="ErpnextImportRunResult.ReportPath"/> always <see langword="null"/> — the host writes the markdown).</returns>
    public async Task<ErpnextImportRunResult> RunAsync(
        ErpnextImportRequest request,
        IImportProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        progress ??= NullImportProgress.Instance;
        var writeAuthority = new AuthorizationWriteContext(
            new ActorId(request.Actor.Value),
            request.Tenant,
            _timeProvider.GetUtcNow());

        var options = request.Options;
        var fromPass = options.FromPass;

        // Cross-pass accumulators, declared before the per-pass guards so a --from-pass resume that
        // skips early passes still produces a complete (if sparser) report.
        var rejectBin = new List<ImportFailure>();
        var costCenterResolutions = new List<CostCenterResolution>();
        var warnings = new List<ReportWarning>();
        var passDurations = new List<PassDuration>();
        var nullCurrencyCount = 0;

        // Pass-2 builds these; passes 4.1/4.2/4.3 resolve parties through them. When Pass 2 is skipped
        // (--from-pass >= 3) the maps stay empty and transactional records resolve to a null party —
        // the documented in-memory resume limitation (cross-process resume arrives with SQLite).
        var customerMap = new Dictionary<string, PartyId>();
        var supplierMap = new Dictionary<string, PartyId>();
        var partyMap = new Dictionary<string, PartyId>();

        ReconciliationPassResult reconciliation = new(Array.Empty<PaymentReconciliationOutcome>());
        VerificationResult verification;

        async Task<TResult> RunTimedAsync<TResult>(string passLabel, Func<Task<TResult>> run)
        {
            var sw = Stopwatch.StartNew();
            var result = await run().ConfigureAwait(false);
            sw.Stop();
            passDurations.Add(new PassDuration(passLabel, sw.Elapsed));
            return result;
        }

        await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Source inventory (always — it is the report's DocType census + the _unmapped/ headline,
            // and it is read-only regardless of --from-pass).
            progress.StepStarting("Source inventory");
            var inventory = await _extractor.ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
            if (inventory.UnmappedUnknownCount > 0)
            {
                warnings.Add(ReportWarning.UnmappedDocType(inventory.UnmappedUnknownCount));
            }

            progress.StepCompleted(
                "Source inventory",
                $"OK ({inventory.Mapped.Count()} mapped; {inventory.UnmappedUnknownCount} unmapped)");

            // Pass 1 — Chart of accounts.
            if (fromPass <= 1)
            {
                progress.StepStarting("Pass 1 (Chart of accounts)");
                var accounts = await MaterializeAsync(_extractor.ReadAccountsAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
                var costCenters = await MaterializeAsync(_extractor.ReadCostCentersAsync(cancellationToken), cancellationToken).ConfigureAwait(false);

                var chartResult = await RunTimedAsync(
                    "Pass 1 — Chart of accounts",
                    () => _chartPass.RunAsync(accounts, costCenters, request.TargetChart, cancellationToken)).ConfigureAwait(false);

                rejectBin.AddRange(chartResult.AccountRejects);
                rejectBin.AddRange(chartResult.CostCenterRejects);
                foreach (var outcome in chartResult.CostCenterOutcomes)
                {
                    if (outcome.TryGetRecord(out var resolution))
                    {
                        costCenterResolutions.Add(resolution);
                    }
                }

                progress.StepCompleted(
                    "Pass 1 (Chart of accounts)",
                    $"OK ({chartResult.AccountOutcomes.Count} accounts; {chartResult.CostCenterOutcomes.Count} cost centers)");
            }

            // Pass 2 — Reference data (parties → periods → tax). Sub-passes are mutually independent.
            if (fromPass <= 2)
            {
                progress.StepStarting("Pass 2 (Reference data)");
                var sw = Stopwatch.StartNew();

                // 2.1 Parties: customers → suppliers → contacts → addresses.
                var customers = await MaterializeAsync(_extractor.ReadCustomersAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
                var suppliers = await MaterializeAsync(_extractor.ReadSuppliersAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
                var contacts = await MaterializeAsync(_extractor.ReadContactsAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
                var addresses = await MaterializeAsync(_extractor.ReadAddressesAsync(cancellationToken), cancellationToken).ConfigureAwait(false);

                foreach (var source in customers)
                {
                    var outcome = await _partyUpserter.UpsertCustomerAsync(source, request.Tenant, request.Actor, cancellationToken).ConfigureAwait(false);
                    if (outcome.TryGetRecord(out var party))
                    {
                        customerMap[source.Name] = party.Id;
                        partyMap[source.Name] = party.Id;
                    }
                    else if (outcome.TryGetFailure(out var failure))
                    {
                        rejectBin.Add(failure);
                    }
                }

                foreach (var source in suppliers)
                {
                    var outcome = await _partyUpserter.UpsertSupplierAsync(source, request.Tenant, request.Actor, cancellationToken).ConfigureAwait(false);
                    if (outcome.TryGetRecord(out var party))
                    {
                        supplierMap[source.Name] = party.Id;
                        partyMap[source.Name] = party.Id;
                    }
                    else if (outcome.TryGetFailure(out var failure))
                    {
                        rejectBin.Add(failure);
                    }
                }

                foreach (var source in contacts)
                {
                    var outcome = await _partyUpserter.AttachContactAsync(source, request.Tenant, request.Actor, cancellationToken).ConfigureAwait(false);
                    if (outcome.TryGetFailure(out var failure))
                    {
                        rejectBin.Add(failure);
                    }
                }

                foreach (var source in addresses)
                {
                    var outcome = await _partyUpserter.AttachAddressAsync(source, request.Tenant, request.Actor, cancellationToken).ConfigureAwait(false);
                    if (outcome.TryGetFailure(out var failure))
                    {
                        rejectBin.Add(failure);
                    }
                }

                progress.SubStepCompleted(
                    "2.1 Parties",
                    $"OK ({customerMap.Count} customers; {supplierMap.Count} suppliers)");

                // 2.2 Periods: fiscal years, then synthesized periods per imported FY.
                var fiscalYears = await MaterializeAsync(_extractor.ReadFiscalYearsAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
                var fyCount = 0;
                var periodCount = 0;
                foreach (var source in fiscalYears)
                {
                    var fyOutcome = await _periodUpserter.UpsertFiscalYearAsync(source, request.TargetChart, cancellationToken).ConfigureAwait(false);
                    if (fyOutcome.TryGetRecord(out var fiscalYear))
                    {
                        fyCount++;
                        var periodOutcomes = await _periodUpserter.SynthesizePeriodsAsync(fiscalYear.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
                        foreach (var periodOutcome in periodOutcomes)
                        {
                            if (periodOutcome.TryGetFailure(out var periodFailure))
                            {
                                rejectBin.Add(periodFailure);
                            }
                            else
                            {
                                periodCount++;
                            }
                        }
                    }
                    else if (fyOutcome.TryGetFailure(out var failure))
                    {
                        rejectBin.Add(failure);
                    }
                }

                progress.SubStepCompleted("2.2 Periods", $"OK ({fyCount} FYs; {periodCount} periods)");

                // 2.3 Tax templates.
                var taxTemplates = await MaterializeAsync(_extractor.ReadTaxTemplatesAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
                var taxCount = 0;
                foreach (var source in taxTemplates)
                {
                    var outcome = await _taxUpserter.UpsertTaxTemplateAsync(source, request.TargetChart, cancellationToken).ConfigureAwait(false);
                    if (outcome.TryGetRecord(out _))
                    {
                        taxCount++;
                    }
                    else if (outcome.TryGetFailure(out var failure))
                    {
                        rejectBin.Add(failure);
                    }
                }

                progress.SubStepCompleted("2.3 Tax", $"OK ({taxCount} tax codes)");

                sw.Stop();
                passDurations.Add(new PassDuration("Pass 2 — Reference data", sw.Elapsed));
                progress.StepCompleted(
                    "Pass 2 (Reference data)",
                    $"OK ({customerMap.Count + supplierMap.Count} parties; {fyCount} FYs; {taxCount} tax codes)");
            }

            // The journal-entry stream feeds BOTH Pass 3 (opening) and Pass 4.4 (standalone); each pass
            // filters internally. Materialize once if either will run (i.e. fromPass <= 4).
            IReadOnlyList<ErpnextJournalEntrySource> journalEntries = Array.Empty<ErpnextJournalEntrySource>();
            if (fromPass <= 4)
            {
                journalEntries = await MaterializeAsync(_extractor.ReadJournalEntriesAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            }

            // Pass 3 — Opening balances (opening journal entries).
            if (fromPass <= 3)
            {
                progress.StepStarting("Pass 3 (Opening balances)");
                var openingResult = await RunTimedAsync(
                    "Pass 3 — Opening balances",
                    () => _openingBalancePass.RunAsync(
                        request.Tenant, journalEntries, request.TargetChart, writeAuthority, cancellationToken)).ConfigureAwait(false);

                rejectBin.AddRange(openingResult.Rejects);
                progress.StepCompleted("Pass 3 (Opening balances)", $"OK ({openingResult.Outcomes.Count} opening entries)");
            }

            // Pass 4 — Transactional history (4.1 sales, 4.2 purchases, 4.3 payments, 4.4 standalone JEs).
            if (fromPass <= 4)
            {
                progress.StepStarting("Pass 4 (Transactional history)");

                var salesInvoices = await MaterializeAsync(_extractor.ReadSalesInvoicesAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
                var purchaseInvoices = await MaterializeAsync(_extractor.ReadPurchaseInvoicesAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
                var payments = await MaterializeAsync(_extractor.ReadPaymentsAsync(cancellationToken), cancellationToken).ConfigureAwait(false);

                // Finding F2: a null/blank source currency is assumed-USD (not rejected); count it so the
                // assumption is auditable in the report. A non-USD currency is a hard reject inside the pass.
                nullCurrencyCount =
                    salesInvoices.Count(i => string.IsNullOrWhiteSpace(i.Currency))
                    + purchaseInvoices.Count(i => string.IsNullOrWhiteSpace(i.Currency))
                    + payments.Count(p => string.IsNullOrWhiteSpace(p.Currency));
                if (nullCurrencyCount > 0)
                {
                    warnings.Add(ReportWarning.NullCurrencyAssumedUsd(nullCurrencyCount));
                }

                var salesResult = await RunTimedAsync(
                    "Pass 4.1 — Sales invoices",
                    () => _salesInvoicePass.RunAsync(
                        request.Tenant,
                        salesInvoices,
                        request.TargetChart,
                        request.ControlAccounts.ArControlAccount,
                        request.ControlAccounts.DefaultIncomeAccount,
                        src => customerMap.TryGetValue(src.Customer, out var id) ? id : (PartyId?)null,
                        cancellationToken)).ConfigureAwait(false);
                rejectBin.AddRange(salesResult.Rejects);
                progress.SubStepCompleted("4.1 Sales invoices", $"OK ({salesResult.Outcomes.Count} invoices)");

                var purchaseResult = await RunTimedAsync(
                    "Pass 4.2 — Purchase invoices",
                    () => _purchaseInvoicePass.RunAsync(
                        request.Tenant,
                        purchaseInvoices,
                        request.TargetChart,
                        request.ControlAccounts.ApControlAccount,
                        request.ControlAccounts.DefaultExpenseAccount,
                        src => supplierMap.TryGetValue(src.Supplier, out var id) ? id : (PartyId?)null,
                        cancellationToken)).ConfigureAwait(false);
                rejectBin.AddRange(purchaseResult.Rejects);
                progress.SubStepCompleted("4.2 Purchase invoices", $"OK ({purchaseResult.Outcomes.Count} bills)");

                var paymentResult = await RunTimedAsync(
                    "Pass 4.3 — Payments",
                    () => _paymentPass.RunAsync(
                        request.Tenant,
                        payments,
                        request.TargetChart,
                        src => partyMap.TryGetValue(src.Party, out var id) ? id : (PartyId?)null,
                        writeAuthority,
                        cancellationToken)).ConfigureAwait(false);
                rejectBin.AddRange(paymentResult.Rejects);
                progress.SubStepCompleted("4.3 Payments", $"OK ({paymentResult.Outcomes.Count} payments)");

                var standaloneResult = await RunTimedAsync(
                    "Pass 4.4 — Standalone journal entries",
                    () => _standaloneJournalEntryPass.RunAsync(
                        request.Tenant, journalEntries, request.TargetChart, writeAuthority, cancellationToken)).ConfigureAwait(false);
                rejectBin.AddRange(standaloneResult.Rejects);
                progress.SubStepCompleted("4.4 Standalone journal entries", $"OK ({standaloneResult.Posted} posted)");

                progress.StepCompleted("Pass 4 (Transactional history)", "OK");
            }

            // Pass 5 — Reconciliation (apply unapplied payments to invoices/bills).
            if (fromPass <= 5)
            {
                progress.StepStarting("Pass 5 (Reconciliation)");
                reconciliation = await RunTimedAsync(
                    "Pass 5 — Reconciliation",
                    () => _reconciliationPass.RunAsync(request.Tenant, request.TargetChart, cancellationToken: cancellationToken)).ConfigureAwait(false);

                progress.StepCompleted(
                    "Pass 5 (Reconciliation)",
                    $"OK ({reconciliation.AppliedCount} applied; {reconciliation.AmbiguousCount} ambiguous; {reconciliation.UnmatchedCount} unmatched)");
            }

            // Pass 6 — Verification ALWAYS runs (it is the read-only gate; --from-pass never skips it).
            progress.StepStarting("Pass 6 (Verification)");
            verification = await RunTimedAsync(
                "Pass 6 — Verification",
                () => _verificationPass.RunAsync(
                    request.Tenant,
                    request.TargetChart,
                    request.AsOf,
                    request.Snapshots.ArAging,
                    request.Snapshots.ApAging,
                    request.Snapshots.GlBalances,
                    new VerificationOptions { AllowAgingDrift = options.AllowAgingDrift },
                    cancellationToken)).ConfigureAwait(false);

            progress.StepCompleted("Pass 6 (Verification)", verification.IsPassed ? "OK (verified)" : $"FAILED ({verification.Outcome})");

            // Assemble the report aggregate (always returned, even on rollback — it is the inspection artifact).
            var report = new MigrationReportInput(
                new RunSummary(Guid.NewGuid().ToString("N"), _timeProvider.GetUtcNow(), inventory, passDurations),
                verification,
                reconciliation,
                rejectBin,
                costCenterResolutions,
                warnings);

            // Commit gate. DryRun-first ordering: a --dry-run is reported as DryRun even when verification
            // also failed (the full VerificationResult is in the report regardless, so no signal is hidden).
            ImportRunStatus status;
            ImportHaltReason haltReason;
            if (options.DryRun)
            {
                status = ImportRunStatus.RolledBack;
                haltReason = ImportHaltReason.DryRun;
            }
            else if (!verification.IsPassed)
            {
                status = ImportRunStatus.RolledBack;
                haltReason = verification.Outcome switch
                {
                    VerificationOutcome.AgingReconciliationFailed => ImportHaltReason.AgingReconciliationFailed,
                    _ => ImportHaltReason.TrialBalanceMismatch,
                };
            }
            else if (options.RejectThreshold is int threshold && rejectBin.Count > threshold)
            {
                status = ImportRunStatus.RolledBack;
                haltReason = ImportHaltReason.RejectThresholdExceeded;
            }
            else
            {
                status = ImportRunStatus.Committed;
                haltReason = ImportHaltReason.None;
            }

            if (status == ImportRunStatus.Committed)
            {
                await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            return new ErpnextImportRunResult(status, haltReason, report, ReportPath: null);
        }
        catch
        {
            // An exception here is genuinely exceptional (the passes convert their own per-record faults
            // to Rejected outcomes). Roll back the unit of work (idempotent) and rethrow — never swallow.
            await _unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<IReadOnlyList<T>> MaterializeAsync<T>(IAsyncEnumerable<T> source, CancellationToken cancellationToken)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            list.Add(item);
        }

        return list;
    }
}
