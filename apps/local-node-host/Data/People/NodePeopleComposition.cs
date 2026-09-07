using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Blocks.People.Foundation.Services;
using Harborline.Api.Foundation.Events;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Crdt.DependencyInjection;
using Harborline.Api.Kernel.Sync.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Data.People;

/// <summary>
/// Single source of truth for the node-side contacts (People) composition (T2b contacts node-flip).
/// Registers the recoverable EF party repository over <see cref="Data.LocalNodeDbContext"/> as both
/// <see cref="IPartyReadModel"/> + <see cref="IPartyWriteService"/> + its route-handler accessor, over an
/// ALREADY-registered <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Extracted so the composition root (<c>Program.cs</c>) and any future runtime DI-graph assertion
/// register the EXACT same People slice — no test/prod drift. It mirrors the financial node-write
/// compositions.
/// </para>
/// <para>
/// <b>No new schema.</b> The Party + contact-history tables are mapped by the shared
/// <c>PeopleEntityModule</c> (already registered in the composition root) and already exist in the node
/// <c>InitialSchema</c> migration, so this flip adds NO migration.
/// </para>
/// <para>
/// <b>SC4-C2.</b> The repository's ONLY persistence sink is the recoverable <c>local-node.db</c>; the
/// event publisher is the explicit <see cref="NoopDomainEventPublisher"/> (the host never wires a
/// cross-cluster event bus, so no <c>IDomainEventStore</c> exists), and no kernel CRDT-writer / per-team
/// event log is reachable. SC4-T9(b) is unaffected. Contacts are TENANT-WIDE; the node serves the single
/// install tenant.
/// </para>
/// </remarks>
public static class NodePeopleComposition
{
    /// <summary>
    /// Registers the node-resident contacts (Party) repository + accessor. The caller is responsible for
    /// having already registered <c>IDbContextFactory&lt;LocalNodeDbContext&gt;</c> (the repository's
    /// backing store).
    /// </summary>
    public static IServiceCollection AddNodeContacts(this IServiceCollection services)
    {
        System.ArgumentNullException.ThrowIfNull(services);

        // Cluster-default Noop publisher, registered explicitly so the DI graph shows the no-op posture.
        // The host never calls AddFoundationEvents(), so there is no IDomainEventStore. TryAdd so this
        // coexists with the financial-write compositions (which register the same singleton).
        services.TryAddSingleton<IDomainEventPublisher, NoopDomainEventPublisher>();

        // Recoverable EF party repository over local-node.db — both read + write surfaces.
        services.AddSingleton<NodeEfPartyRepository>();
        services.AddSingleton<IPartyReadModel>(sp => sp.GetRequiredService<NodeEfPartyRepository>());
        services.AddSingleton<IPartyWriteService>(sp => sp.GetRequiredService<NodeEfPartyRepository>());
        services.AddSingleton<ICanonicalPrincipalPartyReader, NodeEfCanonicalPrincipalPartyReader>();

        // ── Contacts CRDT data↔delta bridge (multi-device INC-4 → container-bridge a2) ───────────────────
        //
        // Makes contacts the FIRST synced doctype: the contact write path now flows THROUGH a YDotNet CRDT
        // document (id "contacts"), which emits a delta on every edit and merges peer deltas back into the
        // readable EF store. Before this, node-host writes went to plain EF/SQLite with zero CRDT, so
        // "edit → peer merges" was false (ONR depth survey GAP-3). See ContactCrdtProjection.
        //
        // The CRDT engine is the default YDotNet (Yjs/yrs, MIT) backend. The ContactCrdtProjection
        // implements BOTH IDeltaProducer + IDeltaSink over its single contacts document.
        //
        // CONTAINER-BRIDGE a2 (ONR survey 2026-06-19). The projection is no longer registered DIRECTLY as the
        // install-level IDeltaProducer/IDeltaSink. Instead AddHarborlineDeltaRouter installs the id-routed
        // DeltaRoutingRegistry as the install-level delta plane; the contact-sync bootstrap hosted service
        // Registers the contacts projection on the router as the first doctype (and cold-start-hydrates it).
        // The per-team child-container daemon then bridges to THIS router (resolved from the outer provider
        // via the IDeltaProducer/IDeltaSink interfaces — kernel-runtime never sees the concrete projection),
        // so two machines actually converge contacts LIVE through the production resolution path. Doctype #2
        // is an additive router registration, not a re-architecture. The router is registered BEFORE any
        // AddHarborlineKernelSync in this container (its own TryAdd-Noop defaults then defer).
        services.AddHarborlineCrdtEngine();
        services.AddSingleton<ContactCrdtProjection>();
        services.AddHarborlineDeltaRouter();

        return services;
    }
}
