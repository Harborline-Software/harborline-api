using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  ADR 0143 SC2 F-1 / ledger Owed #2 — the PUBLIC way a host registers a concrete
//  workflow effect factory WITHOUT naming the internal IWorkflowEffectFactory.
//
//  Owed #2 named an [InternalsVisibleTo]-granted effects-adapter assembly. This is a
//  DELIBERATE deviation to the SAME end, chosen because it is STRICTLY TIGHTER: the
//  factory interface stays fully `internal` — exposed to NO external assembly (not even
//  a trusted one) — so it removes the one residual crack the ledger itself names ("if an
//  apps/ assembly were ever [InternalsVisibleTo]-granted, type containment would compile
//  for it → the fence evaporates for that assembly"). A host registers an effect by
//  handing in a BUILD DELEGATE, which this package captures inside the `internal`
//  DelegateWorkflowEffectFactory; the delegate is invoked ONLY by the broker (the sole
//  Build caller — arch-tested). No external assembly can name the factory, resolve it
//  from DI, or reach another capability's Build. See the PR (b) deviation note.
//
//  WHY host-side, not a shared adapter package: a real WorkflowEffect stages its rows
//  onto the host's in-flight unit-of-work (LocalNodeDbContext) — an app type a package
//  cannot reference. So the concrete build closure is inherently host-coupled (exactly as
//  NodeLiveInvoiceApprovalContext.BuildPostEffect is). The delegate API lets the host
//  supply that closure through a PUBLIC seam while the factory type stays contained.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Public registration for a host-supplied workflow effect factory (ADR 0143 SC2). The host registers a
/// capability's effect by handing in the reach + a build delegate; this package wraps it in the
/// <see langword="internal"/> effect-factory type the broker resolves — so the host never names, resolves, or
/// invokes the factory surface (the broker remains the sole <c>Build</c> caller, compiler-enforced).
/// </summary>
public static class WorkflowEffectFactoryRegistration
{
    /// <summary>
    /// Registers an effect factory for <paramref name="capabilityRef"/> with the given
    /// <paramref name="reach"/>, building each effect via <paramref name="build"/> (resolved lazily against the
    /// container so the closure can capture host services). The factory is added as the
    /// <see langword="internal"/> effect-factory service the effect-factory registry composes; the broker is
    /// still the only code that can resolve + invoke it.
    /// </summary>
    /// <param name="services">The service collection (the composition root — a trusted boundary).</param>
    /// <param name="capabilityRef">The capability the effect is invoked as (e.g. <c>ledger.post-journal-entry</c>).</param>
    /// <param name="reach">
    /// <see cref="WorkflowEffectReach.Internal"/> for a node-local effect, or
    /// <see cref="WorkflowEffectReach.OutboundExternal"/> (the broker forces it CP regardless of the registry label).
    /// </param>
    /// <param name="build">
    /// Builds the <see cref="WorkflowEffect"/> for a request, given the resolved container. Pure construction —
    /// it stages nothing; the returned effect is enlisted by <see cref="IWorkflowStore.AdvanceAsync"/>. Must be
    /// deterministic w.r.t. the step key (build invariant #2).
    /// </param>
    public static IServiceCollection AddWorkflowEffectFactory(
        this IServiceCollection services,
        string capabilityRef,
        WorkflowEffectReach reach,
        Func<IServiceProvider, WorkflowEffectRequest, WorkflowEffect> build)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityRef);
        ArgumentNullException.ThrowIfNull(build);

        services.AddSingleton<IWorkflowEffectFactory>(sp =>
            new DelegateWorkflowEffectFactory(capabilityRef, reach, request => build(sp, request)));
        return services;
    }
}

/// <summary>
/// The <see langword="internal"/> effect factory that wraps a host-supplied build delegate (SC2 F-1). It is the
/// concrete <see cref="IWorkflowEffectFactory"/> the registry composes for a delegate-registered capability;
/// because the interface is <see langword="internal"/>, only same-assembly code (the broker, via the registry)
/// can name/invoke it, so the host's delegate is reachable ONLY through the gated broker path.
/// </summary>
internal sealed class DelegateWorkflowEffectFactory : IWorkflowEffectFactory
{
    private readonly Func<WorkflowEffectRequest, WorkflowEffect> _build;

    /// <summary>Wraps a capability + reach + build delegate.</summary>
    public DelegateWorkflowEffectFactory(
        string capabilityRef, WorkflowEffectReach reach, Func<WorkflowEffectRequest, WorkflowEffect> build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityRef);
        ArgumentNullException.ThrowIfNull(build);
        CapabilityRef = capabilityRef;
        Reach = reach;
        _build = build;
    }

    /// <inheritdoc />
    public string CapabilityRef { get; }

    /// <inheritdoc />
    public WorkflowEffectReach Reach { get; }

    /// <inheritdoc />
    public WorkflowEffect Build(WorkflowEffectRequest request) => _build(request);
}
