using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Foundation.Events;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Single source of truth for the node-side AP bill WRITE + posting composition (Cohort D Step 2c).
/// Registers exactly the services <see cref="BillPostingService"/> needs — the recoverable EF bill
/// repository + a node-resident tenant context + the no-op tax calculator + the Noop event
/// publisher + the posting service + its accessors — over an ALREADY-registered
/// <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c> and <c>IJournalPostingService</c> (the latter
/// wired by <see cref="NodeFinancialPostingComposition.AddNodeFinancialPosting"/> in Step 2a).
/// </summary>
/// <remarks>
/// <para>
/// Extracted so the composition root (<c>Program.cs</c>) and the SC4-T9(b) Layer-2 runtime DI-graph
/// assertion (<c>Sc4RecoverabilityGuardTests</c>) register the EXACT same bill-write slice — no
/// test/prod drift in WHAT the gate verifies. The security SPOT-CHECK can read this one method to
/// audit every registration the AP write path adds. It mirrors
/// <see cref="NodeFinancialPostingComposition.AddNodeFinancialPosting"/>.
/// </para>
/// <para>
/// <b>The bill's auto-posted JE goes through the node posting service.</b>
/// <see cref="BillPostingService.RecordAsync"/> builds a balanced
/// <c>JournalEntry</c> (Debit each line's expense/asset account, Credit AP for the total) and posts
/// it via the injected <c>IJournalPostingService</c> — which is the node-resident, six-phase
/// <c>JournalPostingService</c> from Step 2a that writes ONLY the recoverable
/// <see cref="NodeEfJournalStore"/>. So a recorded bill produces a node-resident journal entry that
/// appears via the node JE read surface (incl. <c>accountIds</c>).
/// </para>
/// <para>
/// <b>SC4-C2 conditions enforced by the shape of these registrations</b> (security-engineering
/// verdict 2026-06-15, conditions (a)-(d), extended to the AP write path):
/// <list type="bullet">
///   <item>(a) the ONLY persistence sinks are the recoverable <c>local-node.db</c>:
///     <see cref="NodeEfBillRepository"/> for bills + (transitively, through the posting service)
///     <see cref="NodeEfJournalStore"/> for the auto-posted JE. This method does NOT register
///     <c>IJournalStore</c> (the Step-2a composition already wired it to the recoverable store).</item>
///   <item>(b) <c>IDomainEventPublisher</c> is registered as the cluster-default
///     <see cref="NoopDomainEventPublisher"/> — explicit so the DI graph shows the no-op posture.
///     The host never calls <c>AddFoundationEvents()</c>, so NO <c>IDomainEventStore</c> exists;
///     <see cref="BillPostingService"/>'s event emission is therefore a no-op (no cross-cluster bus).</item>
///   <item>(c) NO kernel CRDT writer (<c>PostingEngine</c>/<c>ILedgerEventStream</c>) and NO per-team
///     <c>FileBackedEventLog</c>/<c>IEventLog</c> is registered here.</item>
///   <item>(d) the bill repository + tenant context resolve to node-resident reads/writes over
///     <c>local-node.db</c> (<see cref="NodeEfBillRepository"/> / <see cref="StaticNodeTenantContext"/>),
///     never a seed-keyed per-team KV store.</item>
/// </list>
/// </para>
/// </remarks>
public static class NodeBillWriteComposition
{
    /// <summary>
    /// Registers the node-resident AP bill write + posting composition. The caller is responsible for
    /// having already registered <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c> (the repository's
    /// backing) and <c>IJournalPostingService</c> (== the Step-2a node posting service over the
    /// recoverable <see cref="NodeEfJournalStore"/>) — call <c>AddNodeFinancialPosting</c> first.
    /// </summary>
    public static IServiceCollection AddNodeBillWrites(this IServiceCollection services)
    {
        System.ArgumentNullException.ThrowIfNull(services);

        // (d) Recoverable EF bill repository over local-node.db.
        services.AddSingleton<NodeEfBillRepository>();
        services.AddSingleton<IBillRepository>(sp => sp.GetRequiredService<NodeEfBillRepository>());

        // (d) Node-resident tenant context — derives the ambient data TenantId from the ACTIVE TEAM
        // (ADR 0032 identity layer; survey #1275 §5). Retires the install-constant TenantId("local"):
        // the AP write services read Tenant.Id off the ambient ITenantContext, which now follows the
        // active org so a financial write lands in the active org's books, never a shared "local"
        // tenant. Arch-test-fenced (ActiveTeamTenantBindingArchTests).
        services.AddSingleton<ITenantContext, ActiveTeamTenantContext>();

        // Per-line tax: the cluster default no-op (a real tax-bridge adapter is a follow-on; the AP
        // package default is the same NoOpTaxCalculator).
        services.AddSingleton<ITaxCalculator, NoOpTaxCalculator>();

        // (b) Cluster-default Noop publisher, registered explicitly so the DI graph shows the no-op
        // posture (and so BillPostingService binds to it rather than constructing its own). The host
        // never calls AddFoundationEvents(), so there is no IDomainEventStore behind it.
        services.AddSingleton<IDomainEventPublisher, NoopDomainEventPublisher>();

        // The AP bill posting service.
        services.AddSingleton<IBillPostingService, BillPostingService>();

        return services;
    }
}
