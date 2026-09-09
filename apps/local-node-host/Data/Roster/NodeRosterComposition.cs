using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.DependencyInjection;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.DependencyInjection;
using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Data.Roster;

/// <summary>
/// Single source of truth for the node-side TRUST-ROSTER sync composition (production-wiring gap #1). Registers
/// the <see cref="RosterCrdtProjection"/> (the append-log CRDT bridge over the recoverable
/// <see cref="NodeLocalRosterDbContext"/>) and ensures the install-level delta router exists so the roster
/// doctype can register on it as an ADDITIVE route (the #1265 seam) alongside contacts + comms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Doctype #3 is additive.</b> The container-bridge delta router (<c>AddHarborlineDeltaRouter</c>) is the
/// id-routed install-level delta plane; contacts is first (default), comms second, roster third — one more
/// <c>IDeltaRouter.Register</c> call (in <see cref="RosterSyncBootstrapHostedService"/>), NOT a
/// re-architecture. <c>AddHarborlineDeltaRouter</c> is idempotent (TryAdd), so calling it here as well as in
/// <c>AddNodeContacts</c> / <c>AddNodeComms</c> is safe.
/// </para>
/// <para>
/// <b>SC4-C2.</b> The roster projection's ONLY durable sink is the recoverable
/// <see cref="NodeLocalRosterDbContext"/> (the SQLCipher-keyed <c>local-node.db</c>), registered by
/// <c>AddLocalNodeSqlCipherStore</c>. The trust is in the per-record signature (re-validated on rebuild), not
/// in the storage.
/// </para>
/// <para>
/// <b>The live roster the rebuild pushes into.</b> The projection is built via a FACTORY that resolves the
/// install-level <see cref="NodeTeamRoster"/> (seeded at bootstrap with the genesis self-admission) and adopts
/// the re-validated synced roster into it on each merge — so the comms <c>rosterBinding</c> + the
/// <c>MemberSetTrustPolicy</c> read the SYNCED membership. If a host did not register a <c>NodeTeamRoster</c>
/// (a minimal DI test), the projection still converges the CRDT list + durable store but pushes no live roster
/// (harmless).
/// </para>
/// </remarks>
public static class NodeRosterComposition
{
    /// <summary>
    /// Registers the node-resident roster-sync projection + the (idempotent) delta router. The caller is
    /// responsible for having already registered <c>IDbContextFactory&lt;NodeLocalRosterDbContext&gt;</c>
    /// (via <c>AddLocalNodeSqlCipherStore</c>), the CRDT engine, and (for the live-roster push) a
    /// <see cref="NodeTeamRoster"/>.
    /// </summary>
    public static IServiceCollection AddNodeRoster(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The CRDT engine (YDotNet/Yjs, MIT) — idempotent with the other doctypes' calls.
        services.AddHarborlineCrdtEngine();
        // The merge-path signature verifier the rebuild enforces on every synced record. Stateless Ed25519
        // verifier, singleton + TryAdd so it composes idempotently with the comms/contacts registration.
        services.TryAddSingleton<IOperationVerifier, Ed25519Verifier>();
        services.TryAddSingleton<IVerifiedTenantRosterReader, VerifiedTenantRosterReader>();

        // 293 s3c: the replicated path's authority is the local grant store, read through the one sanctioned
        // roster-edge-then-closure reading (EffectiveMemberPermissions). A composition with no grant store
        // answers the empty set, which is the same fail-closed floor an unregistered authority gave.
        services.TryAddSingleton<IRosterAuthority>(GrantStoreRosterAuthority.FromServices);

        // The projection is built via a FACTORY that resolves the live NodeTeamRoster (seeded at bootstrap) and
        // adopts the re-validated synced roster into it on each merge. If a host did not register it (minimal DI
        // test), GetService returns null and the projection converges without pushing a live roster.
        services.AddSingleton<RosterCrdtProjection>(sp => new RosterCrdtProjection(
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ICrdtEngine>(),
            sp.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>(),
            sp.GetRequiredService<IOperationVerifier>(),
            sp.GetRequiredService<IOperationSigner>(),
            sp.GetRequiredService<ILogger<RosterCrdtProjection>>(),
            nodeRoster: sp.GetService<NodeTeamRoster>(),
            // Resolved lazily (ticket 290): the fold's removal leg, absent in a minimal DI test.
            administrators: () => sp.GetService<NodeAdministratorAuthority>(),
            refusalAudit: () => sp.GetService<Harborline.Api.LocalNodeHost.Health.AuthorizationRefusalAudit>(),
            // 293 s3b2: no permission set rides the wire, so the replicated chain gates (admitter holds
            // members:admit, revoker holds members:revoke, no-escalation) read a party's authority from the
            // host's IRosterAuthority - the grant store's view. Unregistered → the fail-closed floor, where
            // only the genesis chain root holds authority. Slice 3c registers GrantStoreRosterAuthority above.
            rosterAuthority: () => sp.GetService<IRosterAuthority>()));
        services.AddSingleton<IRosterRevocationProjection, RosterRevocationProjection>();

        // The install-level id-routed delta plane — idempotent (TryAdd) with the other doctypes.
        services.AddHarborlineDeltaRouter();

        services.AddSingleton<ICompromisedDeviceRevocationPublisher,
            NodeRosterCompromisedDeviceRevocationPublisher>();
        services.AddSingleton<INodeRosterMemberRevocationAuthority,
            NodeRosterMemberRevocationAuthority>();
        services.AddSingleton<IDeviceEntitlementSnapshotSource>(sp =>
            new DeltaRouterEntitlementSnapshotSource(sp.GetRequiredService<IDeltaRouter>()));
        services.AddSingleton<ICompromiseKeyRotation, DeferredCompromiseKeyRotation>();
        services.AddSingleton<ICompromisedDeviceResponseStore,
            NodeRosterCompromisedDeviceResponseStore>();
        services.AddSingleton<ICompromisedDeviceResponseService,
            CompromisedDeviceResponseService>();

        return services;
    }
}
