using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Foundation.Coordination;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Foundation.Events;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Single source of truth for the node-side AR invoice WRITE + posting composition (Cohort D Step
/// 2b). Registers exactly the services <see cref="InvoicePostingService"/> needs — the recoverable EF
/// invoice repository + a durable node numbering service + a node-resident tenant context + the
/// no-op tax calculator + the Noop event publisher + the posting service + its accessors — over an
/// ALREADY-registered <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c>, <c>IJournalPostingService</c>,
/// and <c>IJournalStore</c> (the latter two wired by
/// <see cref="NodeFinancialPostingComposition.AddNodeFinancialPosting"/> in Step 2a).
/// </summary>
/// <remarks>
/// <para>
/// Extracted so the composition root (<c>Program.cs</c>) and the SC4-T9(b) Layer-2 runtime DI-graph
/// assertion (<c>Sc4RecoverabilityGuardTests</c>) register the EXACT same invoice-write slice — no
/// test/prod drift in WHAT the gate verifies. The security SPOT-CHECK can read this one method to
/// audit every registration the AR write path adds. It mirrors
/// <see cref="NodeBillWriteComposition.AddNodeBillWrites"/>.
/// </para>
/// <para>
/// <b>The invoice's issue JE goes through the node posting service.</b>
/// <see cref="InvoicePostingService.IssueAsync"/> builds a balanced <c>JournalEntry</c> (Debit AR for
/// the total, Credit each line's income account) and posts it via the injected
/// <c>IJournalPostingService</c> — which is the node-resident, six-phase <c>JournalPostingService</c>
/// from Step 2a that writes ONLY the recoverable <see cref="NodeEfJournalStore"/>. So an issued
/// invoice produces a node-resident journal entry that appears via the node JE read surface (incl.
/// <c>accountIds</c>). The <see cref="NodeEfJournalStore"/> is also passed as the optional
/// <c>journalStore</c> dep so the void/write-off paths have the recoverable store available; the
/// AR cluster's original-JE-Reversed marking branch is in-memory-store-specific, so on the node the
/// void posts a reversing entry that nets the GL to zero (identical net behavior to the AP bill void)
/// while the invoice's own Voided status gates against a double-void.
/// </para>
/// <para>
/// <b>SC4-C2 conditions enforced by the shape of these registrations</b> (security-engineering
/// verdict 2026-06-15, conditions (a)-(d), extended to the AR write path):
/// <list type="bullet">
///   <item>(a) the ONLY persistence sinks are the recoverable <c>local-node.db</c>:
///     <see cref="NodeEfInvoiceRepository"/> for invoices + (transitively, through the posting
///     service) <see cref="NodeEfJournalStore"/> for the issue/void/write-off JEs. This method does
///     NOT register <c>IJournalStore</c> (the Step-2a composition already wired it to the recoverable
///     store).</item>
///   <item>(b) <c>IDomainEventPublisher</c> is registered as the cluster-default
///     <see cref="NoopDomainEventPublisher"/> — explicit so the DI graph shows the no-op posture.
///     The host never calls <c>AddFoundationEvents()</c>, so NO <c>IDomainEventStore</c> exists;
///     <see cref="InvoicePostingService"/>'s event emission is therefore a no-op (no cross-cluster
///     bus).</item>
///   <item>(c) NO kernel CRDT writer (<c>PostingEngine</c>/<c>ILedgerEventStream</c>) and NO per-team
///     <c>FileBackedEventLog</c>/<c>IEventLog</c> is registered here.</item>
///   <item>(d) the invoice repository + tenant context resolve to node-resident reads/writes over
///     <c>local-node.db</c> (<see cref="NodeEfInvoiceRepository"/> / <see cref="StaticNodeTenantContext"/>),
///     never a seed-keyed per-team KV store; the numbering service derives its sequence from the same
///     recoverable store.</item>
/// </list>
/// </para>
/// <para>
/// <b>Shared-service de-duplication.</b> The tenant context, tax calculator, and Noop publisher are
/// registered with <c>TryAddSingleton</c> so this composition coexists with
/// <see cref="NodeBillWriteComposition.AddNodeBillWrites"/> (which registers the same three) in any
/// order without a duplicate-registration surprise — both compositions resolve the SAME node-resident
/// <see cref="StaticNodeTenantContext"/> / <see cref="NoOpTaxCalculator"/> /
/// <see cref="NoopDomainEventPublisher"/> singletons. When this composition is built standalone (the
/// Layer-2 invoice DI-graph test), the <c>TryAdd</c>s register them fresh.
/// </para>
/// </remarks>
public static class NodeInvoiceWriteComposition
{
    /// <summary>
    /// Registers the node-resident AR invoice write + posting composition. The caller is responsible
    /// for having already registered <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c> (the
    /// repository's + numbering service's backing), <c>IJournalPostingService</c> (== the Step-2a node
    /// posting service over the recoverable <see cref="NodeEfJournalStore"/>), and the recoverable
    /// <c>IJournalStore</c> — call <c>AddNodeFinancialPosting</c> first.
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <param name="localReplicaId">
    /// The install's per-replica suffix for minted invoice numbers (default <c>"AA"</c>, mirroring
    /// <see cref="BlocksFinancialArOptions.LocalReplicaId"/>).
    /// </param>
    public static IServiceCollection AddNodeInvoiceWrites(
        this IServiceCollection services,
        Harborline.Api.Foundation.Assets.Common.ReplicaId? localReplicaId = null)
    {
        System.ArgumentNullException.ThrowIfNull(services);

        var replica = localReplicaId ?? new Harborline.Api.Foundation.Assets.Common.ReplicaId("AA");

        // (d) Recoverable EF invoice repository over local-node.db.
        services.AddSingleton<NodeEfInvoiceRepository>();
        services.AddSingleton<IInvoiceRepository>(sp => sp.GetRequiredService<NodeEfInvoiceRepository>());

        // (d) Durable, restart-safe invoice numbering — derives the next sequence from the MAX
        // existing canonical number in the SAME recoverable store (never re-mints a colliding -0001
        // after a restart, unlike the in-memory counter).
        services.AddSingleton<IInvoiceNumberingService>(sp =>
            new NodeEfInvoiceNumberingService(
                sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>(),
                replica));

        // (d) Node-resident tenant context — derives the ambient data TenantId from the ACTIVE TEAM
        // (ADR 0032 identity layer; survey #1275 §5), retiring the install-constant TenantId("local").
        // TryAdd so it coexists with AddNodeBillWrites (same ActiveTeamTenantContext registration).
        services.TryAddSingleton<ITenantContext, ActiveTeamTenantContext>();

        // Per-line tax: the cluster default no-op (a real tax-bridge adapter is a follow-on).
        services.TryAddSingleton<ITaxCalculator, NoOpTaxCalculator>();

        // (b) Cluster-default Noop publisher, registered explicitly so the DI graph shows the no-op
        // posture. The host never calls AddFoundationEvents(), so there is no IDomainEventStore.
        services.TryAddSingleton<IDomainEventPublisher, NoopDomainEventPublisher>();

        // The AR invoice posting service. The recoverable
        // NodeEfJournalStore (registered by Program.cs / the Step-2a slice) is passed as the optional
        // journalStore dep so the void path has the recoverable store available.
        services.AddSingleton<IInvoicePostingService>(sp =>
            new InvoicePostingService(
                tenantContext: sp.GetRequiredService<ITenantContext>(),
                invoices:      sp.GetRequiredService<IInvoiceRepository>(),
                numbering:     sp.GetRequiredService<IInvoiceNumberingService>(),
                tax:           sp.GetRequiredService<ITaxCalculator>(),
                journals:      sp.GetRequiredService<IJournalPostingService>(),
                events:        sp.GetRequiredService<IDomainEventPublisher>(),
                journalStore:  sp.GetRequiredService<IJournalStore>(),
                timeProvider:  sp.GetRequiredService<TimeProvider>()));

        // ADR 0135 F3 — the issued-invoice status adapter. NodeEfJournalStore resolves it through the
        // declared Platform registry; an issue JE's
        // Draft → Issued invoice update joins the JE's local-node.db transaction whenever an ambient
        // IssuedInvoiceWriteScope matches (any invoice issue — direct OR recurring). A no-op for every
        // non-issue JE post, so manual JE / bills / payments / payroll / void paths are unaffected. This
        // closes the residual JE↔AR non-atomic window: no posted-issue-JE can coexist with a stranded Draft.
        services.AddSingleton<INodeIssuedInvoiceWriteEnlister, NodeIssuedInvoiceWriteEnlister>();
        services.AddSingleton<IWriteEnlistment>(sp =>
            (IWriteEnlistment)sp.GetRequiredService<INodeIssuedInvoiceWriteEnlister>());

        return services;
    }
}
