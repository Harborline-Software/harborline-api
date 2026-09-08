using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Blocks.Workflow.Durable;

/// <summary>
/// DI registration for the durable process engine CORE (ADR 0135 slice 1). Registers the
/// trigger dispatcher over an ALREADY-registered <see cref="IWorkflowStore"/> + the set of
/// <see cref="IWorkflowStepHandler"/> implementations.
/// </summary>
/// <remarks>
/// The store impl (the recoverable EF/SQLite <c>NodeEfWorkflowStore</c>) is registered by the host
/// composition (<c>apps/local-node-host</c>), not here — the core package carries no EF dependency,
/// exactly the <c>IJournalStore</c> seam / <c>NodeEfJournalStore</c> impl split this engine mirrors.
/// </remarks>
public static class DurableWorkflowServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IWorkflowTriggerDispatcher"/> + the engine <see cref="WorkflowEngineOptions"/>
    /// (the max-iterations cap, ADR 0135 A0). The caller must already have registered an
    /// <see cref="IWorkflowStore"/> and any <see cref="IWorkflowStepHandler"/> implementations (the host
    /// composition does this with the recoverable store + the v1 handlers).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">
    /// Engine tuning (the loop-back cap). When <see langword="null"/>, <see cref="WorkflowEngineOptions.Default"/>
    /// (cap = 50) is registered. The dispatcher resolves it via DI (its optional ctor param).
    /// </param>
    public static IServiceCollection AddDurableWorkflowEngine(
        this IServiceCollection services,
        WorkflowEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton((options ?? WorkflowEngineOptions.Default).Validated());
        services.AddSingleton<IWorkflowTriggerDispatcher, WorkflowTriggerDispatcher>();
        // ADR 0143 (red-team Chain 1+5 / F2): the canonical capability→authority registry the admission
        // validator DERIVES an action's CP/AP class from (single cross-runtime source; unknown⇒CP).
        services.AddSingleton<ICapabilityAuthorityRegistry>(CapabilityAuthorityRegistry.Canonical);
        // WF-KEY (ADR 0140): the fail-closed authoring/publish-time CP-reachability gate. Stateless;
        // structural BFS over a WorkflowDefinition + registry-derived classification. The authoring face
        // of ADR 0135 A1 R-1. Resolves the registry above via its ctor.
        services.AddSingleton<IWorkflowAdmissionValidator, WorkflowAdmissionValidator>();

        // ── ADR 0143 broker-PEP enforcement seam (the effect-execution boundary) ──────────────────
        // ADR 0135 A1 R-1 (D7-re-pin) / ADR 0143 R1-E item 4 — LOAD-time re-validation of a persisted
        // definition. Shares the canonical wire mapper + the admission validator with the persist path.
        services.AddSingleton<IWorkflowDefinitionLoadValidator>(sp =>
            new WorkflowDefinitionLoadValidator(sp.GetRequiredService<IWorkflowAdmissionValidator>()));

        // ADR 0143 D-INV-7 — the override-rate health metric. The counting sink OWNS the metric out of the
        // box (a host may swap in a durable/audited sink). Registered as its concrete type too so a health
        // surface can read ConfirmedCount / OverriddenCount / OverrideRate.
        services.AddSingleton<CountingWorkflowApprovalDecisionSink>();
        services.AddSingleton<IWorkflowApprovalDecisionSink>(
            sp => sp.GetRequiredService<CountingWorkflowApprovalDecisionSink>());

        // The effect-factory registry (ADR 0143 R1-B/F1 effect-execution boundary). Composes whatever
        // IWorkflowEffectFactory implementations the host registered (the financial JE-posting factory lands
        // with the A1 interpreter — from an [InternalsVisibleTo]-trusted effects adapter, since the factory
        // interface is internal per SC2 F-1). Empty ⇒ the broker refuses every capability (fail-closed).
        // Registered as the concrete once, then exposed as its TWO faces (SC2 F-1 type-level containment):
        //   • public  IWorkflowEffectCatalog        → metadata only (IsEffectingCapability / EffectKeyFor);
        //     the safe seam an apps/ interpreter or the admission validator injects — no path to a factory.
        //   • internal IWorkflowEffectFactoryResolver → the resolve+build surface; only same-assembly code
        //     (the broker) can name/inject it, so Resolve().Build() cannot be reached from an untrusted assembly.
        services.AddSingleton(sp => new WorkflowEffectFactoryRegistry(sp.GetServices<IWorkflowEffectFactory>()));
        services.AddSingleton<IWorkflowEffectCatalog>(sp => sp.GetRequiredService<WorkflowEffectFactoryRegistry>());
        services.AddSingleton<IWorkflowEffectFactoryResolver>(sp => sp.GetRequiredService<WorkflowEffectFactoryRegistry>());

        // The broker-PEP itself — the fixed enforcement point every side-effecting workflow step passes
        // through. Resolves the internal effect-factory resolver + the authority registry + the decision sink
        // + a clock. Exposed only as the public IWorkflowEffectBroker seam (the concrete is internal).
        services.AddSingleton<IWorkflowEffectBroker>(sp => new WorkflowEffectBroker(
            sp.GetRequiredService<IWorkflowEffectFactoryResolver>(),
            sp.GetRequiredService<ICapabilityAuthorityRegistry>(),
            sp.GetRequiredService<IWorkflowApprovalDecisionSink>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Harborline.Api.Foundation.Authorization.AuthorizationGate>()));

        return services;
    }

    /// <summary>
    /// Registers the durable <see cref="EntityStoreWorkflowDefinitionStore"/> as the
    /// <see cref="IWorkflowDefinitionStore"/> singleton (WF-KEY W-7 / G5), composing whatever
    /// <see cref="IEntityStore"/> the host has registered. Call after an entity-store registration
    /// (an in-memory entity store for tests, or the durable production entity store). The
    /// <see cref="IWorkflowAdmissionValidator"/> is resolved from DI when present (register it via
    /// <see cref="AddDurableWorkflowEngine"/>), else the canonical validator is used; the
    /// The composition root's <see cref="TimeProvider"/> is required.
    /// </summary>
    /// <remarks>
    /// The store is backend-agnostic (it composes the <see cref="IEntityStore"/> abstraction), so
    /// durability is determined entirely by which entity store the host registers — the SAME layering
    /// as <c>AddEntityStoreFormDefinitionStore</c>.
    /// </remarks>
    public static IServiceCollection AddEntityStoreWorkflowDefinitionStore(
        this IServiceCollection services,
        Func<IServiceProvider, IEntityMutationStore> mutationStore)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mutationStore);

        // Register the concrete store ONCE, then expose its TWO faces off the SAME singleton (SC2 F-2):
        //   • IWorkflowDefinitionStore          → the AUTHORING face (lenient builder-reload reads).
        //   • IWorkflowDefinitionExecutionStore → the EXECUTION face (fail-closed re-validating reads ONLY).
        // An interpreter / instantiation / D7-re-pin path injects the execution face, so load-for-execution
        // can only traverse the re-admitting reads (GetAdmitted*Async) — the safe path is the only path a
        // DI-resolved executor can reach. One instance backs both so authoring + execution never desync.
        services.TryAddSingleton<IWorkflowAdmissionValidator, WorkflowAdmissionValidator>();
        services.AddSingleton(sp =>
            new EntityStoreWorkflowDefinitionStore(
                sp.GetRequiredService<IEntityStore>(),
                mutationStore(sp),
                sp.GetRequiredService<IWorkflowAdmissionValidator>(),
                sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IWorkflowDefinitionStore>(
            sp => sp.GetRequiredService<EntityStoreWorkflowDefinitionStore>());
        services.AddSingleton<IWorkflowDefinitionExecutionStore>(
            sp => sp.GetRequiredService<EntityStoreWorkflowDefinitionStore>());
        services.AddSingleton(sp => new AuthorizedWorkflowDefinitionLifecycle(
            sp.GetRequiredService<EntityStoreWorkflowDefinitionStore>(),
            mutationStore(sp),
            sp.GetRequiredService<IWorkflowAdmissionValidator>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<AuthorizationGate>(),
            sp.GetRequiredService<IRoleGateAdmission>()));
        return services;
    }
}
