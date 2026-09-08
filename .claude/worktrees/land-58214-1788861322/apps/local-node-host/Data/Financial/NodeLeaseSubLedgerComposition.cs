using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialPayments.Services;
using Harborline.Api.Blocks.FinancialSubLedger.Projection.Services;
using Harborline.Api.Blocks.FinancialSubLedger.Services;
using Harborline.Api.Blocks.Leases.DependencyInjection;
using Harborline.Api.Blocks.Leases.Services;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Single source of truth for the node-side ADR 0120 lease sub-ledger READ composition (ADR 0122
/// §D4 P2 → (b)). Activates the lease → sub-ledger → payment-history read path on the embedded node
/// so a fully-offline Harborline install (signal-bridge STOPPED) renders REAL lease payment-history from
/// the recoverable <c>local-node.db</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this wires (the offline read chain
/// <c>lease → LeaseSubLedgerLink → SubLedgerAccountId → ISubLedgerReadModel.GetHistoryAsync</c>):</b>
/// <list type="bullet">
///   <item><see cref="ISubLedgerAccountRepository"/> → <see cref="InMemorySubLedgerAccountRepository"/>
///     (in-memory v1 per ADR 0120 v1 posture; ADR-0120 PR-B default) — holds the minted Receivable
///     <c>SubLedgerAccount</c> per activated lease.</item>
///   <item><see cref="ISubLedgerReadModel"/> → <see cref="SubLedgerReadModel"/>, composed over the
///     node's EF-backed AR/AP repositories (<see cref="NodeEfInvoiceRepository"/> /
///     <see cref="NodeEfBillRepository"/>, already wired by <c>AddNodeInvoiceWrites</c> /
///     <c>AddNodeBillWrites</c>) + the READ-ONLY node payment repositories below. So a lease's history
///     resolves from REAL node-resident invoices + payments, never the Bridge or an empty in-memory
///     store.</item>
///   <item><see cref="ILeaseSubLedgerLinkRepository"/> + <see cref="LeaseSubLedgerService"/> (via
///     <c>AddLeaseSubLedgerMapping</c>) — the PM-pack link record + the activation/read service.
///     In-memory v1 link store (ADR 0120 PR-D default).</item>
///   <item><see cref="IPaymentRepository"/> / <see cref="IPaymentApplicationRepository"/> →
///     <see cref="NodeEfPaymentRepository"/> / <see cref="NodeEfPaymentApplicationRepository"/>, the
///     READ-ONLY node payment residency (the read half of the deferred 2d feature — payment AUTHORING
///     is NOT pulled forward; the write members throw).</item>
/// </list>
/// </para>
/// <para>
/// <b>Singleton, not scoped (node posture).</b> The projection package's
/// <c>AddSubLedgerProjection()</c> registers <see cref="SubLedgerReadModel"/> as <em>scoped</em> (it is
/// authored for the request-scoped Bridge). On the embedded node the route handlers close over a
/// dependency resolved from the OUTER (root) container (bug-2849), so a scoped read-model would be a
/// captive dependency. Every dependency of <see cref="SubLedgerReadModel"/> here is itself
/// singleton-safe — the node AR/AP/payment repos resolve a fresh short-lived
/// <see cref="LocalNodeDbContext"/> per call from the <c>IDbContextFactory</c>, and the in-memory
/// account/link repos are thread-safe — so this composition registers the read-model + account repo as
/// <em>singletons</em>. It does NOT call <c>AddSubLedgerProjection()</c> (which would register the
/// scoped variant); it registers the same two services directly with the node-correct lifetime.
/// </para>
/// <para>
/// <b>SC-4 recoverability (security-engineering SC4-C2; <c>Sc4RecoverabilityGuardTests</c>).</b> Every
/// service here is READ-ONLY over the recoverable <c>local-node.db</c> (or an in-memory v1 mapping
/// store that holds NO irreplaceable financial value — the financial value lives in the recoverable
/// invoices/payments/JEs). Nothing here references a kernel CRDT-writer type
/// (<c>PostingEngine</c>/<c>ILedgerEventStream</c>/<c>FileBackedEventLog</c>/<c>IEventLog</c>) — so the
/// Layer-1 IL scan stays green — and nothing here is part of a posting/bill/invoice DI-graph the
/// Layer-2 assertions build, so it does not perturb those gates.
/// </para>
/// <para>
/// <b>Ordering.</b> Call AFTER <c>AddNodeInvoiceWrites</c> + <c>AddNodeBillWrites</c> (so the node AR/AP
/// repositories <see cref="SubLedgerReadModel"/> composes over are already registered). The payment
/// repos here are the LAST piece the read-model's ctor needs.
/// </para>
/// </remarks>
public static class NodeLeaseSubLedgerComposition
{
    /// <summary>
    /// Registers the node-resident lease sub-ledger READ composition. The caller must have already
    /// registered <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c>, <see cref="IInvoiceRepository"/>
    /// (<c>AddNodeInvoiceWrites</c>), and <see cref="IBillRepository"/> (<c>AddNodeBillWrites</c>).
    /// </summary>
    public static IServiceCollection AddNodeLeaseSubLedgerReads(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // READ-ONLY node payment residency (the read half of the deferred 2d feature). The
        // SubLedgerReadModel ctor requires IPaymentRepository + IPaymentApplicationRepository; these
        // resolve REAL node-resident payment data over local-node.db. TryAdd so a future node
        // payment-WRITE composition (2d) can register a read/write variant FIRST and win.
        services.TryAddSingleton<NodeEfPaymentRepository>();
        services.TryAddSingleton<IPaymentRepository>(sp => sp.GetRequiredService<NodeEfPaymentRepository>());
        services.TryAddSingleton<NodeEfPaymentApplicationRepository>();
        services.TryAddSingleton<IPaymentApplicationRepository>(
            sp => sp.GetRequiredService<NodeEfPaymentApplicationRepository>());

        // ADR 0120 sub-ledger identity + projection — registered with the node-correct SINGLETON
        // lifetime (NOT the projection package's scoped AddSubLedgerProjection — see class remarks).
        // In-memory v1 account repo (ADR 0120 v1 posture); SubLedgerReadModel composed over the node
        // AR/AP/payment repos already registered.
        services.TryAddSingleton<ISubLedgerAccountRepository, InMemorySubLedgerAccountRepository>();
        services.TryAddSingleton<ISubLedgerReadModel, SubLedgerReadModel>();

        // PM-pack lease → sub-ledger mapping: in-memory link repo + LeaseSubLedgerService (activation +
        // offline read path) + ErpNextLeaseCaptureService. AddLeaseSubLedgerMapping registers these as
        // singletons and depends on the ISubLedgerAccountRepository + ISubLedgerReadModel registered
        // above. Idempotent via TryAdd inside the extension's AddSingleton-on-fresh-services.
        services.AddLeaseSubLedgerMapping();

        return services;
    }
}
