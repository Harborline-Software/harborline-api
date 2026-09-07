using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Blocks.FinancialLedger.Services;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Single source of truth for the node-side financial POSTING composition (Cohort D Step 2a).
/// Registers exactly the services <see cref="JournalPostingService"/> needs — the node-resident
/// resolvers + the posting service + its accessor — over an ALREADY-registered
/// <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c> and <c>IJournalStore</c>.
/// </summary>
/// <remarks>
/// <para>
/// Extracted so the composition root (<c>Program.cs</c>) and the SC4-T9(b) Layer-2 runtime
/// DI-graph assertion (<c>Sc4RecoverabilityGuardTests</c>) register the EXACT same posting slice —
/// no test/prod drift in WHAT the gate verifies. The security SPOT-CHECK can read this one method to
/// audit every registration the posting path adds.
/// </para>
/// <para>
/// <b>SC4-C2 conditions enforced by the shape of these registrations</b> (security-engineering verdict
/// 2026-06-15, conditions (a)-(d)):
/// <list type="bullet">
///   <item>(a) the posting service's only persistence call is <c>IJournalStore.SaveAtomicAsync</c>;
///     this method does NOT register <c>IJournalStore</c> (the caller must already have wired it to
///     the recoverable <see cref="NodeEfJournalStore"/>).</item>
///   <item>(b) NO <c>IDomainEventPublisher</c>/<c>IDomainEventStore</c> is registered here —
///     <see cref="JournalPostingService"/> takes no event publisher, and the host never calls
///     <c>AddFoundationEvents()</c>, so the cluster default Noop posture holds.</item>
///   <item>(c) NO kernel CRDT writer (<c>PostingEngine</c>/<c>ILedgerEventStream</c>) and NO per-team
///     <c>FileBackedEventLog</c>/<c>IEventLog</c> is registered here.</item>
///   <item>(d) the account + period resolvers resolve to the node EF reads over
///     <c>local-node.db</c> (<see cref="NodeEfAccountResolver"/> / <see cref="NodeEfPeriodResolver"/>),
///     never a seed-keyed per-team KV store.</item>
/// </list>
/// </para>
/// </remarks>
public static class NodeFinancialPostingComposition
{
    /// <summary>
    /// Registers the node-resident posting-service composition. Idempotent-friendly: the caller is
    /// responsible for having already registered <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c>
    /// (the resolvers' backing) and <c>IJournalStore</c> (== <see cref="NodeEfJournalStore"/>).
    /// </summary>
    public static IServiceCollection AddNodeFinancialPosting(this IServiceCollection services)
    {
        System.ArgumentNullException.ThrowIfNull(services);

        // Node-resident resolvers (SC4-C2 (d)) — pure EF reads over the recoverable local-node.db.
        services.AddSingleton<IAccountResolver, NodeEfAccountResolver>();
        services.AddSingleton<IPeriodResolver, NodeEfPeriodResolver>();

        // Ticket 194: no IUserContext is registered. StaticNodeUserContext answered one permission
        // string — the soft-close override — with a constant true for the whole process lifetime, and
        // it was this node's only administrative grant that no authority log could revoke. Ticket 205
        // slice 5 moved both soft-close override sites (JournalPostingService.AuthorizeSoftCloseAsync
        // and DefaultPaymentApplicationService.MayOverrideSoftCloseAsync) onto one AuthorizationGate
        // decision naming the fiscal period the override addresses, which left the registration with
        // no consumer at all. The type is deleted; its install-constant actor id lives on
        // ActiveTeamAuthorizationContext.LocalUserId, which names a caller and grants nothing.

        // ADR 0032 identity layer — per-org role resolution: the OS-user's role on the active org's
        // membership edge drives ICurrentUser.Roles. TryAdd so a host that wires a richer identity
        // context wins; the node default is the active-team-derived context. (survey #1275 §3)
        services.TryAddSingleton<ActiveTeamAuthorizationContext>();
        services.TryAddSingleton<Harborline.Api.Foundation.Authorization.ICurrentUser>(
            sp => sp.GetRequiredService<ActiveTeamAuthorizationContext>());

        // Ticket 205 slice 5 — the outer container no longer registers IAuthorizationContext at all, and
        // WebPlaneFencedAuthorizationContext (card #3356) is deleted with it. The fence existed because
        // the ~20 gated route call sites took IAuthorizationContext as a Map(...) parameter resolved from
        // here, so a hosted-web member's request would otherwise have inherited the OS operator's grants.
        // Slices 3 and 4 converted every one of those routes to resolve ONE AuthorizationGate decision at
        // its point of use, keyed by the request's canonical principal (NodeGatePrincipal), and slice 5
        // removed the last consumer of the interface in this container (NodeAuthorizationTenantContext).
        // A blanket "no" over a seam nothing asks at is not a fence; the web plane is now closed by the
        // member's OWN grant closure at each act — strictly more honest, and equally closed
        // (WebPlaneGrantSubjectRouteTests, WebPlaneAuthorizationFenceTests, FailClosedWebPlaneRouteFenceTests).
        // The desktop operator's permission set is still read by the hosted app's request-scoped
        // SelectedSessionTenantContext, which takes ActiveTeamAuthorizationContext directly and applies the
        // same plane rule itself.

        // The six-phase posting service.
        services.AddSingleton<IJournalPostingService, JournalPostingService>();

        return services;
    }
}
