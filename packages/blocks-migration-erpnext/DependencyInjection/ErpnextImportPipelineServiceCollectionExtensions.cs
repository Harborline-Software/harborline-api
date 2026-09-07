using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Blocks.FinancialAp.DependencyInjection;
using Harborline.Api.Blocks.FinancialAr.DependencyInjection;
using Harborline.Api.Blocks.FinancialLedger.DependencyInjection;
using Harborline.Api.Blocks.FinancialLedger.Migration;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPayments.DependencyInjection;
using Harborline.Api.Blocks.FinancialPayments.Services;
using Harborline.Api.Blocks.FinancialPeriods.DependencyInjection;
using Harborline.Api.Blocks.FinancialPeriods.Migration;
using Harborline.Api.Blocks.FinancialTax.DependencyInjection;
using Harborline.Api.Blocks.FinancialTax.Migration;
using Harborline.Api.Blocks.Migration.Erpnext.Orchestration;
using Harborline.Api.Blocks.Migration.Erpnext.Reconciliation;
using Harborline.Api.Blocks.Migration.Erpnext.Verification;
using Harborline.Api.Blocks.People.Foundation.DependencyInjection;
using Harborline.Api.Blocks.People.Foundation.Migration;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Import.Extraction;
using MultiTenancy = Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Migration.Erpnext.DependencyInjection;

/// <summary>
/// Composition root for the whole ERPNext-&gt;Harborline import pipeline (ADR 0100, spec §8):
/// registers every dependency the <c>ErpnextImportOrchestrator</c> needs to run Pass 1-6
/// against the <b>in-memory</b> ledger/AR/AP/payments/periods/tax/people substrate. Consumed by
/// the A7 CLI host and by the A7-6 orchestrator tests; the latter is what verifies this wiring
/// resolves end-to-end on synthetic v15 fixtures.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the in-memory / dry-run substrate, not a durable target.</b> Every store is the
/// <c>InMemory*</c> fake (per-process, lost at exit). A durable SQLite-backed commit target is a
/// deferred RUN-enablement increment; the v1 surface is <c>--dry-run</c> (post against the
/// in-memory ledger, render the report, never persist). The <c>InMemoryImportUnitOfWork</c> is a
/// no-op transaction seam — it records Committed/RolledBack for the orchestrator's commit gate but
/// has no real storage to roll back.
/// </para>
/// <para>
/// <b>Registration ORDER is load-bearing in one place.</b> The ledger substrate (Section 1) is
/// registered BEFORE the cluster <c>Add*</c> extensions (Section 2) so that this method's
/// <c>IPeriodResolver</c>-&gt;<c>InMemoryPeriodResolver</c> wins over the periods cluster's
/// <c>TryAdd</c> of the SQLite resolver (which cannot see periods synthesized into the in-memory
/// fiscal repositories). The in-memory resolver is permissive: posting succeeds whether or not
/// Pass 2 synthesized a matching period, matching the existing per-pass test harnesses.
/// </para>
/// <para>
/// <b>Shared state across passes</b> comes from the cluster <c>TryAddSingleton</c> repositories:
/// every pass resolves the same singleton store, so Pass 1's accounts are visible to Pass 4's JE
/// posting, Pass 3-5's invoices/payments are visible to Pass 6's verification, etc. The one
/// concrete-typed seam is <c>InMemoryAccountResolver</c>: the account importer takes the concrete
/// type (it calls <c>Upsert</c>) while every reader takes <c>IAccountResolver</c>, so both are
/// aliased to the SAME singleton instance.
/// </para>
/// <para>
/// <b>Two disconnected period stores (accepted simplification).</b> Pass 2's period upserter writes
/// to the fiscal-year/period repositories; posting consults the permissive in-memory period
/// resolver. They are not wired together — harmless on the in-memory path because the resolver is
/// permissive, but flagged as a substrate simplification to revisit at RUN-enablement.
/// </para>
/// <para>
/// <b>Scope.</b> The payment-application service is registered <c>scoped</c> by the payments
/// cluster, so the reconciliation applier (which wraps it and stamps the run's actor), the
/// reconciliation pass, and therefore the orchestrator are all <c>scoped</c>. The host MUST resolve
/// <c>ErpnextImportOrchestrator</c> from inside a created scope.
/// </para>
/// </remarks>
public static class ErpnextImportPipelineServiceCollectionExtensions
{
    /// <summary>
    /// Register every service the ERPNext import orchestrator needs to run Pass 1-6 against the
    /// in-memory substrate. After calling this, resolve <c>ErpnextImportOrchestrator</c> from a DI
    /// scope and call <c>RunAsync</c>.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="reader">
    /// An already-loaded source reader (typically a <c>MariaDbDumpSourceReader</c> loaded from the
    /// CIC-supplied dump outside the repo). Forwarded to the extraction registration; never echoed.
    /// </param>
    /// <param name="actor">
    /// The run's acting party (audit attribution), from <c>ErpnextImportRequest.Actor</c>. Stamped on
    /// every payment application performed by the reconciliation pass.
    /// </param>
    /// <param name="tenant">
    /// The run's tenant, from <c>ErpnextImportRequest.Tenant</c>. Seeds the constant single-tenant
    /// <c>MultiTenancy.ITenantContext</c> the AR/AP aging services require to construct (see remarks
    /// on the single-tenant-per-run substrate). Must match the request's tenant.
    /// </param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddErpnextImportPipeline(
        this IServiceCollection services,
        ISourceReader reader,
        PartyId actor,
        TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(reader);

        // ---- Section 1: in-memory ledger substrate (registered FIRST) ----------------------
        // Must beat the periods cluster's TryAdd of SqlitePeriodResolver, and supplies the
        // resolver/store/read-model/posting/user/clock that the migration importers + posting
        // depend on (AddInMemoryAccounting does NOT register any of these).
        services.AddSingleton(_ => new InMemoryAccountResolver());
        services.AddSingleton<IAccountResolver>(sp => sp.GetRequiredService<InMemoryAccountResolver>());
        services.AddSingleton<IJournalStore, InMemoryJournalStore>();
        services.AddSingleton<IGeneralLedgerReadModel, InMemoryGeneralLedgerReadModel>();
        services.AddSingleton<IPeriodResolver, InMemoryPeriodResolver>();
        services.AddSingleton<IUserContext>(
            _ => new StaticUserContext(
                "erpnext-importer",
                new[] { Harborline.Api.Foundation.Authorization.AuthorizationOperationNames.FinancialPeriodOverrideSoftClose }));
        // The AR/AP aging services (resolved by the verification pass) require a MultiTenancy
        // ITenantContext to construct; no cluster registers one. Seed a constant single-tenant
        // context from the run's tenant. The aging read only fires when a verification snapshot is
        // supplied, in which case this tenant must equal the request's (single-tenant-per-run).
        services.AddSingleton<MultiTenancy.ITenantContext>(_ => new StaticTenantContext(tenant));
        services.AddSingleton<IJournalPostingService, JournalPostingService>();
        services.AddSingleton<InMemoryClassificationStore>();

        // ---- Section 2: cluster substrates (in-memory fakes + per-record importers) ---------
        services.AddBlocksPeopleFoundation();
        services.AddInMemoryAccounting();
        services.AddInMemoryBlocksFinancialPeriods();
        services.AddBlocksFinancialTax();
        services.AddBlocksFinancialAr();
        services.AddBlocksFinancialAp();
        services.AddHarborlineFinancialPayments();

        // ---- Section 3: migration importers + Pass 1/2/4.4 orchestration not covered above --
        // The blocks-financial-ledger migration importers + chart/opening/standalone passes are
        // NOT registered by AddInMemoryAccounting, and the three Pass-2 upserters live in their
        // respective clusters' Migration namespaces without DI registration. Wire them here.
        services.AddSingleton<IErpnextAccountImporter, ErpnextAccountImporter>();
        services.AddSingleton<IErpnextCostCenterImporter>(
            sp => new ErpnextCostCenterImporter(
                PropertyAliasMap.Empty,
                sp.GetRequiredService<InMemoryClassificationStore>(),
                sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IErpnextJournalEntryImporter, ErpnextJournalEntryImporter>();
        services.AddSingleton<ErpnextChartImportPass>();
        services.AddSingleton<ErpnextOpeningBalancePass>();
        services.AddSingleton<ErpnextStandaloneJournalEntryPass>();
        services.AddSingleton<IPass2PartyUpserter, Pass2PartyUpserter>();
        services.AddSingleton<IPass2PeriodUpserter, Pass2PeriodUpserter>();
        services.AddSingleton<IPass2TaxUpserter, Pass2TaxUpserter>();

        // ---- Section 4: Pass 5 reconciliation + Pass 6 verification -------------------------
        // The applier wraps the SCOPED payment-application service and stamps the run actor, so it
        // (and the reconciliation pass, and the orchestrator) are scoped.
        services.AddScoped<IReconciliationApplier>(
            sp => new PaymentApplicationReconciliationApplier(
                sp.GetRequiredService<IPaymentApplicationService>(),
                actor));
        services.AddScoped<ErpnextReconciliationPass>();
        services.AddSingleton<ErpnextVerificationPass>();

        // ---- Section 5: transaction seam + the orchestrator itself --------------------------
        services.AddSingleton<IImportUnitOfWork, InMemoryImportUnitOfWork>();
        services.AddScoped<ErpnextImportOrchestrator>();

        // ---- Section 6: read-side extraction (A0) -------------------------------------------
        services.AddErpnextExtraction(reader);

        return services;
    }
}

/// <summary>
/// Minimal constant <see cref="MultiTenancy.ITenantContext"/> seeded from the import run's tenant.
/// The pipeline is composed once per run (see <c>AddErpnextImportPipeline</c>'s <c>tenant</c>
/// parameter), so the ambient tenant never changes; this only exists to let the AR/AP aging
/// services construct. Distinct from the authorization-tier tenant context (ADR 0008).
/// </summary>
internal sealed class StaticTenantContext(TenantId tenant) : MultiTenancy.ITenantContext
{
    public MultiTenancy.TenantMetadata? Tenant { get; } =
        new() { Id = tenant, Name = tenant.Value };
}
