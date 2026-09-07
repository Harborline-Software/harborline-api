using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Blocks.Payroll.Services;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Data.Payroll;

/// <summary>
/// Single source of truth for the node-side payroll composition (T4 local-first sweep — the payroll
/// node-flip; ADR 0113 ABSOLUTE local-first). Registers the three recoverable Node EF payroll repos +
/// the block <see cref="PayRunPostingService"/> (which posts pay-run JEs through the already-node-wired
/// <c>IJournalPostingService</c>) over an ALREADY-registered
/// <c>IDbContextFactory&lt;NodeLocalPayrollDbContext&gt;</c> and the node posting composition.
/// </summary>
/// <remarks>
/// <para>
/// Extracted (mirroring <c>NodeBankingWriteComposition.AddNodeBankingWrites</c> /
/// <c>NodeFinancialPostingComposition.AddNodeFinancialPosting</c>) so the composition root
/// (<c>Program.cs</c>) and the SC4-T9(b) Layer-2 runtime DI-graph assertion
/// (<c>Sc4RecoverabilityGuardTests</c>) register the EXACT same payroll slice — no test/prod drift in
/// WHAT the gate verifies. The security SPOT-CHECK can read this one method to audit every registration
/// the payroll path adds.
/// </para>
/// <para>
/// <b>Why register the slice directly instead of <c>AddInMemoryPayroll()</c>-then-override?</b> The
/// block's <c>AddInMemoryPayroll()</c> registers the in-memory repos via <c>TryAdd*</c>; registering the
/// exact node slice directly keeps the composition SC4-auditable and self-documenting and avoids the
/// InMemory-then-override double-registration (the node has no in-memory payroll persistence to shadow).
/// </para>
/// <para>
/// <b>SC4-C2 conditions enforced by the shape of these registrations</b> (extending the
/// security-engineering verdict 2026-06-15 (a)-(d) to the payroll path):
/// <list type="bullet">
///   <item>(a)/(d) the ONLY payroll persistence sinks are the recoverable <c>local-node.db</c> payroll
///     tables (via the three Node EF repos over <see cref="NodeLocalPayrollDbContext"/>). The pay-run
///     post writes its balanced journal entry through the SAME recoverable <c>NodeEfJournalStore</c>
///     (wired by <c>AddNodeFinancialPosting</c>, called first), never a seed-keyed per-team KV.</item>
///   <item>(b) the pay-run post path posts a JournalEntry through the GL-of-record posting service;
///     it does NOT touch <c>IDomainEventPublisher</c>/<c>IDomainEventStore</c> directly (the node
///     posting composition already enforces the no-op posture).</item>
///   <item>(c) NO kernel CRDT writer (<c>PostingEngine</c>/<c>ILedgerEventStream</c>) and NO per-team
///     <c>FileBackedEventLog</c>/<c>IEventLog</c> is registered here — the payroll services reference
///     none (the SC4-T9(b) Layer-1 IL scan stays green automatically).</item>
/// </list>
/// </para>
/// <para>
/// <b>Tenant context (ADR 0032 identity layer).</b> <see cref="PayRunPostingService"/> reads the
/// ambient <c>ITenantContext.Tenant?.Id</c>. This registers the active-team-derived
/// <see cref="ActiveTeamTenantContext"/> (projects the data <c>TenantId</c> from the active team's id),
/// the SAME impl type the bill/invoice write compositions register — so when payroll runs after them the
/// <c>TryAdd</c> is a benign no-op, and when payroll is the only composition (a payroll-only graph) it is
/// the registration, identically active-team-bound. It does NOT register the retired
/// <see cref="StaticNodeTenantContext"/> (the install-constant <c>TenantId("local")</c>): registering a
/// DIFFERENT impl type via <c>TryAdd</c> would make the ambient tenant order-dependent on whichever
/// composition registered first — pinning a payroll-only graph to the literal <c>"local"</c> tenant,
/// the cross-org split-brain the data-isolation fence forbids. <see cref="ActiveTeamTenantContext"/>
/// resolves its <see cref="IActiveTeamAccessor"/> from DI (registered at the host root before any write
/// composition). Arch-test-fenced (<c>ActiveTeamTenantBindingArchTests</c> — both the literal-construction
/// and the <c>StaticNodeTenantContext</c>-as-<c>ITenantContext</c> registration forms).
/// </para>
/// </remarks>
public static class NodePayrollWriteComposition
{
    /// <summary>
    /// Registers the node-resident payroll composition. The caller must already have registered
    /// <c>IDbContextFactory&lt;NodeLocalPayrollDbContext&gt;</c> (the repos' backing) and the node
    /// posting composition (call <c>AddNodeFinancialPosting</c> first) so
    /// <c>IJournalPostingService</c> resolves to the recoverable node posting service.
    /// </summary>
    public static IServiceCollection AddNodePayrollWrites(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // (a)/(d) The three recoverable Node EF payroll repos over local-node.db.
        services.AddSingleton<IEmployeeRepository, NodeEfEmployeeRepository>();
        services.AddSingleton<IPayRunRepository, NodeEfPayRunRepository>();
        services.AddSingleton<IFilingObligationRepository, NodeEfFilingObligationRepository>();

        // The block pay-run posting service — posts balanced JEs through the node IJournalPostingService.
        // Singleton on the node (mirrors the node posting service's singleton lifetime; the block default
        // is scoped for the Bridge's per-request tenant context, but the node's tenant is install-constant).
        services.AddSingleton<IPayRunPostingService, PayRunPostingService>();

        // The pay-run posting service reads the ambient tenant from ITenantContext. Register the
        // active-team-derived ActiveTeamTenantContext (ADR 0032 identity layer) — the SAME impl type the
        // bill/invoice write compositions register, so TryAdd makes payroll idempotent with them; a
        // payroll-only graph gets the identical active-team binding (never the retired "local" literal).
        // Registering the retired StaticNodeTenantContext here instead would split-brain a payroll-only
        // graph onto TenantId("local") — the cross-org data-isolation hole the fence forbids.
        services.TryAddSingleton<ITenantContext, ActiveTeamTenantContext>();

        return services;
    }
}
