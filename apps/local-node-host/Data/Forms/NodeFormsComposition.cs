using System;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Foundation.Assets;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Foundation.Forms.DependencyInjection;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Engine.DependencyInjection;
using Harborline.Api.Foundation.Governance.DependencyInjection;
using Harborline.Api.Foundation.Macaroons;
using Harborline.Api.Foundation.MissionSpace.Regulatory.DependencyInjection;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.SecurityPolicy.Models;
using Harborline.Api.Foundation.SecurityPolicy.Retention;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.Kernel.Schema.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Data.Forms;

/// <summary>
/// Single source of truth for the node-side DYNAMIC-FORMS composition (ADR 0055
/// keystone, the "wire the built-but-unwired engine" amendment 2026-06-25).
/// Registers the four substrates <see cref="Harborline.Api.Foundation.Forms.Engine.FormEngine"/>
/// composes — the kernel schema registry, the asset entity store + audit log,
/// the durable Entity-store-backed <see cref="Harborline.Api.Foundation.Forms.IFormDefinitionStore"/>,
/// and the macaroon form-capability issuer/verifier pair — plus the engine
/// itself, so a <c>FormDefinition</c> can be rendered → validated → saved on
/// the single-operator embedded node.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope (CP-safety).</b> This wires the engine for <b>first-party</b> forms
/// — <c>FormDefinition</c>s authored by our own packets and registered against
/// the node's own schema registry. <b>Packet-carried / third-party</b> form
/// definitions (a form that could write a CP-locked field) require the
/// fail-closed load-time CP-reachability validator shared with the ADR-0135 A1
/// packet-carried-interpreter discipline (the engine-family CP-safety
/// substrate). That gate is a SEPARATE, LATER item — see the ADR 0055 amendment
/// CP-safety follow-up. Until it exists, only schemas/overlays the node itself
/// registers reach the engine; nothing on this path loads an externally-supplied
/// definition.
/// </para>
/// <para>
/// <b>Substrate availability.</b> <see cref="Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor"/>
/// (INV-S3 PII-at-rest) is already registered by the host's
/// <c>AddHarborlineRecoveryCoordinator</c> call; the kernel schema registry
/// project is already referenced. This composition adds the asset entity store
/// + audit log, the form-definition store, the engine, and the macaroon
/// primitives the form-capability pair resolves
/// (<see cref="IRootKeyStore"/> + <see cref="IMacaroonIssuer"/>).
/// </para>
/// <para>
/// <b>Persistence posture (v1).</b> Form instances persist through the
/// in-memory <see cref="Harborline.Api.Foundation.Assets.Entities.IEntityStore"/>
/// reference store and the form-definition store composes that same in-memory
/// entity store — durable on a single process run, NOT across restart. The
/// durable node-EF-backed forms entity store (the analogue of the calendar /
/// banking <c>NodeEf</c> overrides over the SQLCipher local-node.db) is the
/// follow-up to this wiring slice; a durable <c>IEntityStore</c> registration
/// is the app/Bridge path. The engine
/// surface is identical regardless of which <c>IEntityStore</c> wins, so the
/// durable swap is a later, drop-in registration override (the calendar
/// <c>AddSingleton</c>-after-<c>TryAdd</c> last-wins pattern).
/// </para>
/// <para>
/// <b>Idempotency.</b> Every underlying registration is <c>TryAdd</c>-based
/// (schema registry, assets, engine, macaroon pair) except the form-definition
/// store, which is a plain <c>AddSingleton</c> (last-wins) so a durable override
/// registered after this call replaces it.
/// </para>
/// </remarks>
public static class NodeFormsComposition
{
    /// <summary>
    /// Registers the dynamic-forms engine and its substrate dependencies for the
    /// embedded node (ADR 0055). Call once at the composition root, after
    /// <c>AddHarborlineRecoveryCoordinator</c> (so <c>IFieldEncryptor</c> resolves)
    /// and before <c>SharedHostedWebApp</c> (so the hosted endpoint maps routes
    /// before Kestrel starts).
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="hostJurisdiction">
    /// The node's deployment residency region — the <c>TargetJurisdiction</c> the SPINE-2
    /// Store PEP checks a <c>Reside</c>-effect classified field's residency eligibility
    /// against (F-11 / F2). Sourced from node configuration (<c>LocalNode:HostJurisdiction</c>);
    /// the LIVE composition MUST relay the node's real region rather than let the engine's
    /// permissive <c>"US"</c> default silently govern residency on every node. A null / blank
    /// value falls back to the <see cref="FormEngineOptions.HostJurisdiction"/> default so a
    /// pre-existing caller and every non-residency-bearing form stay unchanged.
    /// </param>
    public static IServiceCollection AddNodeForms(
        this IServiceCollection services,
        string? hostJurisdiction = null,
        Action<IServiceCollection, Func<IServiceProvider, IEntityMutationStore>,
            Func<IServiceProvider, IHierarchyCompositeUnitOfWork>>? configureWriters = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Form writes are authorization decisions with durable audit evidence. Keep the dependency
        // explicit at the composition boundary: the shipping host supplies it through
        // AddEnrollmentCompensatingControlAudit, while other hosts may compose AddHarborlineKernelAudit. Deferring this
        // check until IFormEngine is first resolved turns a missing module into a remote runtime 500.
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IAuthorizedAuditTrail)))
        {
            throw new InvalidOperationException(
                "AddNodeForms requires the kernel audit module. Register IAuthorizedAuditTrail by " +
                "calling AddHarborlineKernelAudit or AddEnrollmentCompensatingControlAudit before AddNodeForms.");
        }

        // (1a) Content-addressed blob store fallback for non-schema consumers
        //      (e.g. org-branding assets). The schema registry no longer touches
        //      IBlobStore — schema bytes live inline on the Schema record (card
        //      3776). In-memory single-process v1; TryAdd so a durable override
        //      wins.
        services.TryAddSingleton<Harborline.Api.Foundation.Blobs.IBlobStore, NodeInMemoryBlobStore>();

        // (1b) Kernel schema registry — JSON-Schema 2020-12 validation core (the
        //      engine's ValidateAsync resolves ISchemaRegistry).
        services.AddHarborlineKernelSchemaRegistry();

        // (1c) Ticket 151 (L1418) — compile-at-activation validation. The catalog compiles the
        //      baseline record schemas ONCE, when it is composed (a pack's record types are compiled
        //      the same way at install-activate); CompiledSchemaEntityValidator then looks the
        //      compiled artefact up per write and never compiles. It is registered as its own type,
        //      NOT as the shared IEntityValidator store hook: that hook fires for every asset entity
        //      — definition ENVELOPES and form INSTANCES included, whose bodies are envelopes a
        //      record-type schema must not judge. The seat for record validation is the records write
        //      coordinator (NodeEntityWriter), which holds this validator and runs it after the gate
        //      on every record create and update.
        services.TryAddSingleton(sp =>
        {
            var catalog = new CompiledSchemaCatalog(sp.GetRequiredService<ISchemaRegistry>());
            Harborline.Api.LocalNodeHost.Data.Entities.NodeRecordSchemas.ActivateBaseline(catalog);
            return catalog;
        });
        services.TryAddSingleton(sp => new CompiledSchemaEntityValidator(
            sp.GetRequiredService<ISchemaRegistry>(),
            sp.GetRequiredService<CompiledSchemaCatalog>()));

        // (2) Asset entity store + version chain + audit log + hierarchy. The
        //     engine's SaveAsync writes the form instance through IEntityStore
        //     and emits one IAuditLog.AppendAsync per save (INV-S4).
        Func<IServiceProvider, IEntityMutationStore> entityMutations = null!;
        Func<IServiceProvider, IHierarchyCompositeUnitOfWork> hierarchyMutations = null!;
        services.AddHarborlineAssetsInMemory((entities, hierarchy) =>
        {
            entityMutations = entities;
            hierarchyMutations = hierarchy;
        });

        // (3) Macaroon primitives the form-capability issuer/verifier resolve.
        //     The host does not call AddHarborlineDecentralization (it has no need
        //     of the wider ReBAC / capability-graph surface), so register the
        //     two macaroon primitives the forms capability pair needs directly,
        //     idempotently. The root-key store is pre-seeded with a per-process
        //     key for the forms location: the issuer signs with it and the
        //     verifier (resolving the SAME store) checks against it — the macaroon
        //     never leaves the node (issuer→verifier is an in-process round-trip
        //     per request), so a per-process key is sufficient. A KMS / keyring /
        //     root-seed-derived IRootKeyStore is the production swap (same TryAdd
        //     seam); deriving this key from the install root seed is the
        //     follow-up that makes a node-issued macaroon stable across restart.
        services.TryAddSingleton<IRootKeyStore>(_ =>
        {
            var store = new InMemoryRootKeyStore();
            store.Set(
                MacaroonFormCapabilityIssuer.DefaultLocation,
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            return store;
        });
        services.TryAddSingleton<IMacaroonIssuer, DefaultMacaroonIssuer>();

        // (4) The form-capability issuer + verifier (mint a bearer macaroon,
        //     verify it into a CapabilityToken). The route mints a token for the
        //     active-team tenant + the single operator, then calls the engine.
        services.AddMacaroonFormCapabilities();

        // (5) The durable Entity-store-backed form-definition store, composing the
        //     IEntityStore registered above. AddSingleton (last-wins) so a durable
        //     node-EF override registered after this call replaces it.
        services.AddEntityStoreFormDefinitionStore(
            entityMutations);

        // (5b) The D4 reuse-unit store (ADR 0135 amendment 2026-07-01) — in-memory v1, TryAdd so a
        //      durable override wins. Not previously wired: nothing in the node host referenced
        //      IReusableUnitStore until the prototype-showcase L3 (reference-reuse) demo form needed a
        //      real ReusableUnit to point at. The render engine NOW consumes IReuseResolver — a Reference
        //      node's subtree expands (CP-locked) in a live GET view (FormEngine.RenderAsync resolves the
        //      cascade before walking Section.Items). AddReuseResolver registers the resolver the engine's
        //      factory resolves via GetService.
        services.TryAddSingleton<Harborline.Api.Foundation.Forms.IReusableUnitStore,
            Harborline.Api.Foundation.Forms.InMemoryReusableUnitStore>();
        services.AddReuseResolver();

        // (6) SPINE-2 governance (F-11 / ADR 0140 D2) — makes classification ENFORCE on
        //     field VALUES (encrypt / redact / residency / retention), replacing the legacy
        //     PiiSensitivity-only overlay. Self-contained so ANY node-forms composition
        //     resolves the enforcer:
        //       • the regulatory data-residency enforcer (empty-constraint in-memory default),
        //       • the retention resolver over the tenant's default security policy,
        //       • fail-closed null-object defaults for the two per-subject crypto services the
        //         PEP constructor requires but the form VALUE path never invokes (a subject-scoped
        //         class is refused earlier — the subjectRef seam is not wired). A host that calls
        //         AddHarborlineRecoveryCoordinator FIRST supplies the real crypto (TryAdd keeps it),
        //       • the governance layer itself (resolver + PEP + bridges + fail-closed consent).
        services.AddInMemoryRegulatoryPolicy();
        services.TryAddSingleton<IRetentionPolicyResolver>(provider =>
        {
            var timeProvider = provider.GetRequiredService<TimeProvider>();
            return new DefaultRetentionPolicyResolver((tenant, ct) =>
                new ValueTask<TenantSecurityPolicy>(
                    TenantSecurityPolicy.DefaultFor(tenant, timeProvider.GetUtcNow())));
        });
        services.TryAddSingleton<ISubjectFieldEncryptor, FailClosedSubjectFieldEncryptor>();
        services.TryAddSingleton<ISubjectErasureService, FailClosedSubjectErasureService>();
        services.AddHarborlineGovernance();

        // (7) The engine itself (singleton IFormEngine). The governance seam wired above is
        //     resolved by AddHarborlineFormEngine's factory; RequireGovernanceEnforcement makes a
        //     missing seam a hard construction failure (the composition-root fail-closed gate —
        //     the engine can never silently fall back to the legacy PII-only path).
        //     HostJurisdiction is relayed from node config (F2): the LIVE node governs a
        //     Reside-effect field's residency against its REAL deployment region, not a silent
        //     hardcoded "US". A null/blank arg keeps the FormEngineOptions default.
        var options = new FormEngineOptions { RequireGovernanceEnforcement = true };
        if (!string.IsNullOrWhiteSpace(hostJurisdiction))
        {
            options = new FormEngineOptions
            {
                RequireGovernanceEnforcement = true,
                HostJurisdiction = hostJurisdiction,
            };
        }
        services.AddHarborlineFormEngine(
            entityMutations,
            options);

        configureWriters?.Invoke(services, entityMutations, hierarchyMutations);

        return services;
    }
}
