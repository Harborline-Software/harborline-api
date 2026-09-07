using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPayments.Services;
using Harborline.Api.Foundation.Events;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Single source of truth for the node-side payment-WRITE composition (ADR 0122 §D4 T2 — CIC
/// un-deferred payment-write 2026-06-16). Registers exactly the services the node payment-record +
/// apply path needs: the recoverable read/WRITE node payment + payment-application repositories, the
/// <see cref="DefaultPaymentApplicationService"/> that records the <c>PaymentApplication</c> + keeps
/// the invoice/bill balance + payment unapplied-amount consistent, and the route-handler accessor
/// (bug-2849). Composes over the ALREADY-registered node AR/AP repositories
/// (<c>AddNodeInvoiceWrites</c> / <c>AddNodeBillWrites</c>) + node-resident <see cref="ITenantContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now records + applies payments offline.</b> Before T2 the node payment repositories
/// threw <see cref="NotSupportedException"/> on write (read-only residency for the P2 lease-history
/// read). T2 lifts that: recording an invoice/bill payment creates a <c>Payment</c> (Draft) in the
/// recoverable <c>local-node.db</c> and APPLIES it to the target invoice/bill via
/// <see cref="IPaymentApplicationService.ApplyAsync"/> — which writes the <c>PaymentApplication</c>
/// row that (transitively, via the invoice's <c>SubLedgerAccountId</c> FK) surfaces the payment in the
/// ADR 0120 lease sub-ledger payment-history. So a fully-offline install (signal-bridge STOPPED) can
/// record rent / invoice / bill payments and see them in the lease ledger.
/// </para>
/// <para>
/// <b>What this wires:</b>
/// <list type="bullet">
///   <item><see cref="NodeEfPaymentRepository"/> / <see cref="NodeEfPaymentApplicationRepository"/> as
///     <see cref="IPaymentRepository"/> / <see cref="IPaymentApplicationRepository"/> — the SAME
///     recoverable EF repos the lease-history read composes over, now read/WRITE. <c>AddSingleton</c>
///     (not <c>TryAdd</c>) so this composition WINS over the read-only <c>TryAdd</c> in
///     <see cref="NodeLeaseSubLedgerComposition.AddNodeLeaseSubLedgerReads"/> — call THIS first so the
///     read-model composes over the writable repos (functionally identical types, just authoring-enabled).</item>
///   <item><see cref="IPaymentApplicationService"/> → <see cref="DefaultPaymentApplicationService"/>,
///     composed over the node AR/AP/payment repos + the node-resident <see cref="ITenantContext"/>
///     + the explicit <see cref="NoopDomainEventPublisher"/> (no cross-cluster event bus — the host
///     never calls <c>AddFoundationEvents()</c>).</item>
/// </list>
/// </para>
/// <para>
/// <b>SC4-C2 recoverability (security-engineering verdict; asserted by the SC4-T9(b) Layer-2 node
/// payment-write DI-graph test in <c>Sc4RecoverabilityGuardTests</c>, mirroring the AR/AP write tests):</b>
/// <list type="bullet">
///   <item>(a) the ONLY persistence sinks are the recoverable <c>local-node.db</c> —
///     <see cref="NodeEfPaymentRepository"/> (payments) + <see cref="NodeEfPaymentApplicationRepository"/>
///     (applications) + (transitively, via the apply service updating balances)
///     <see cref="NodeEfInvoiceRepository"/> / <see cref="NodeEfBillRepository"/>. No JE is posted on
///     record (the Bridge contract records Draft payments; GL-posting Clear/Bounce is the deferred
///     future lifecycle, NOT wired here), so no kernel CRDT-writer is reachable.</item>
///   <item>(b) <see cref="IDomainEventPublisher"/> is the cluster-default
///     <see cref="NoopDomainEventPublisher"/>; no <see cref="IDomainEventStore"/> is registered.</item>
///   <item>(c) NO kernel CRDT writer (<c>PostingEngine</c>/<c>ILedgerEventStream</c>) and NO per-team
///     <c>FileBackedEventLog</c>/<c>IEventLog</c> is registered here.</item>
///   <item>(d) the payment + application repos + tenant context resolve to node-resident reads/writes
///     over <c>local-node.db</c> (never a seed-keyed per-team KV); the payment write is idempotent on
///     <c>SourceReference</c> over that same recoverable store (the dedupe state IS the recoverable
///     record + the <c>ux_payments_tenant_source_ref</c> unique index, NOT a seed-keyed KV).</item>
/// </list>
/// </para>
/// <para>
/// <b>Ordering.</b> Call AFTER <c>AddNodeInvoiceWrites</c> + <c>AddNodeBillWrites</c> (so the node AR/AP
/// repositories the apply service composes over are registered) and BEFORE
/// <c>AddNodeLeaseSubLedgerReads</c> (so the writable payment repos win its read-only <c>TryAdd</c>).
/// </para>
/// </remarks>
public static class NodePaymentWriteComposition
{
    /// <summary>
    /// Registers the node-resident payment record + apply composition. The caller must have already
    /// registered <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c>, <see cref="IInvoiceRepository"/>
    /// (<c>AddNodeInvoiceWrites</c>), <see cref="IBillRepository"/> (<c>AddNodeBillWrites</c>), and the
    /// node-resident <see cref="ITenantContext"/>.
    /// </summary>
    public static IServiceCollection AddNodePaymentWrites(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // (a/d) Recoverable read/WRITE node payment repos over local-node.db. AddSingleton (NOT TryAdd)
        // so this WINS over the read-only TryAdd in AddNodeLeaseSubLedgerReads — call this first.
        services.AddSingleton<NodeEfPaymentRepository>();
        services.AddSingleton<IPaymentRepository>(sp => sp.GetRequiredService<NodeEfPaymentRepository>());
        services.AddSingleton<NodeEfPaymentApplicationRepository>();
        services.AddSingleton<IPaymentApplicationRepository>(
            sp => sp.GetRequiredService<NodeEfPaymentApplicationRepository>());

        // (b) Cluster-default Noop publisher (no cross-cluster event bus). TryAdd to coexist with the
        // AR/AP write compositions which register the same singleton.
        services.TryAddSingleton<IDomainEventPublisher, NoopDomainEventPublisher>();

        // The payment-application service (records the PaymentApplication + keeps balances consistent).
        // Composed explicitly over the node-resident deps so the DI graph is auditable + the SC4-T9(b)
        // Layer-2 assertion can build the exact same graph.
        services.AddSingleton<IPaymentApplicationService>(sp =>
            new DefaultPaymentApplicationService(
                payments:      sp.GetRequiredService<IPaymentRepository>(),
                applications:  sp.GetRequiredService<IPaymentApplicationRepository>(),
                invoices:      sp.GetRequiredService<IInvoiceRepository>(),
                bills:         sp.GetRequiredService<IBillRepository>(),
                tenantContext: sp.GetRequiredService<ITenantContext>(),
                // GetRequiredService, not GetService: the reversal period gate must fail loudly at
                // composition rather than quietly disappear (see ticket 091 for what a container
                // that cannot build a financial service costs when nobody validates it).
                periods:       sp.GetRequiredService<IPeriodResolver>(),
                // Ticket 205 slice 5: the reversal-date soft-close OVERRIDE resolves one point-of-use
                // AuthorizationGate decision naming the fiscal period it addresses. GetService, not
                // GetRequiredService: a container without a gate cannot DECIDE the override, and an
                // undecidable override does not stand — so a missing registration closes this path
                // rather than opening it or failing composition.
                gate:          sp.GetService<Harborline.Api.Foundation.Authorization.AuthorizationGate>(),
                events:        sp.GetRequiredService<IDomainEventPublisher>(),
                timeProvider:  sp.GetRequiredService<TimeProvider>()));

        return services;
    }
}
