using System.Collections.Frozen;

using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  WF-KEY / ADR 0143 — the EFFECT-FACTORY registry (the .NET effect-execution
//  boundary named by ADR 0143 R1-B/F1). This is the fence the broker-PEP guards:
//  the ONLY way an authored/interpreted workflow step can produce a real domain
//  side-effect (a posted JE, an outbound notification) is a registered
//  IWorkflowEffectFactory, and a factory is invoked ONLY from inside the
//  WorkflowEffectBroker (arch-tested — WorkflowEngineEnforcementSeamArchitectureTests +
//  WorkflowEffectFactoryContainmentTests).
//
//  TYPE-LEVEL CONTAINMENT (SC2 F-1, 2026-07-02 seam-review). The fence is NOT a
//  name-coupled source-scan (a rename/registry-injection evaded that — proven). It is
//  COMPILER-enforced by C# accessibility, split across two surfaces:
//    • PUBLIC  IWorkflowEffectCatalog       → metadata ONLY (IsEffectingCapability /
//      EffectKeyFor). What an interpreter / the admission validator legitimately needs
//      to tell an effecting step from a decision step. It CANNOT hand out a factory.
//    • INTERNAL IWorkflowEffectFactory (+ its Build), IWorkflowEffectFactoryResolver
//      (Resolve), WorkflowEffectFactoryRegistry, WorkflowEffectBroker → the resolve+build
//      surface. `internal` to blocks-workflow, so ONLY same-assembly code (the broker) can
//      obtain a live factory and invoke Build. A concrete host factory implements the
//      internal IWorkflowEffectFactory ONLY from an assembly this package explicitly grants
//      via [InternalsVisibleTo] (a deliberate, reviewed trust grant — PR (b)'s effects
//      adapter) — NOT "anyone who can resolve the DI service". An apps/ interpreter that
//      injects IWorkflowEffectCatalog gets metadata and no way to reach Build; it cannot
//      even NAME IWorkflowEffectFactoryResolver / IWorkflowEffectFactory to attempt the
//      Resolve().Build() bypass (compile error). The regression fence is the reflection
//      red-team test in WorkflowEffectFactoryContainmentTests (goes red if the surface is
//      re-publicized — the way the reviewer proved the old hole open).
//
//  FINANCIAL-FREE ENGINE CORE. The factory INTERFACE lives here (blocks-workflow),
//  but every CONCRETE factory that touches a domain effect (the JE-posting factory
//  that calls IJournalPostingService) lives in the host / financial cluster. The
//  engine core hands the broker a WorkflowEffect closure (IWorkflowStore.WorkflowEffect)
//  and never references the financial cluster — the same layering IWorkflowStore uses.
//  Enforced by WorkflowEngineIsFinancialFreeArchitectureTests.
//
//  The `EffectKey` is the marker ADR 0135 A1 R-1 names: "a step carrying a non-null
//  EffectKey". In the declarative model, a step's effect is its ActionBinding's
//  CapabilityRef; the EffectKey binds that CapabilityRef to a concrete factory + its
//  reach (Internal vs OutboundExternal).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The reach of a workflow effect. <see cref="OutboundExternal"/> effects (email/webhook/API calls
/// that send data to an external party) are treated as CP by the broker REGARDLESS of the capability's
/// registry authority — the ADR 0143 R1-E red-team residual ("classify outbound-external effects as CP",
/// the data-exfil chain). An autonomous workflow that could email data out is a consequential act.
/// </summary>
public enum WorkflowEffectReach
{
    /// <summary>The effect mutates only node-local state (e.g. a posted JE on local-node.db).</summary>
    Internal = 0,

    /// <summary>The effect sends data to an external party (email/webhook/external API) — treated as CP.</summary>
    OutboundExternal = 1,
}

/// <summary>
/// The stable identity of a registered workflow effect: the capability it is invoked as, plus its reach.
/// This is the "non-null EffectKey" marker ADR 0135 A1 R-1 keys its load-time CP-reachability check on —
/// an action whose <see cref="WorkflowActionBindingDef.CapabilityRef"/> resolves to a registered EffectKey
/// is an EFFECTING step (it can produce a real side-effect), so the reachability fence applies to it.
/// </summary>
/// <param name="CapabilityRef">The capability the effect is invoked as (e.g. <c>ledger.post-journal-entry</c>).</param>
/// <param name="Reach">Internal vs OutboundExternal (the latter is forced-CP by the broker).</param>
public readonly record struct EffectKey(string CapabilityRef, WorkflowEffectReach Reach)
{
    /// <summary>Validates the capability ref is non-empty.</summary>
    public EffectKey EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(CapabilityRef))
        {
            throw new ArgumentException("An EffectKey must carry a non-empty CapabilityRef.", nameof(CapabilityRef));
        }

        return this;
    }
}

/// <summary>
/// The financial-free request handed to an <see cref="IWorkflowEffectFactory"/>: which capability, on which
/// instance, at which idempotency-stable step key, with the authored action params (opaque JSON — the engine
/// core does not parse them; the concrete factory does). The factory returns a
/// <see cref="WorkflowEffect"/> that stages its rows onto the advance's in-flight unit-of-work (ADR-0126),
/// so the effect co-commits atomically with the step advance (build invariant #1).
/// </summary>
/// <param name="CapabilityRef">The capability the effect is invoked as.</param>
/// <param name="Instance">The instance the effect fires for (its working state, tenant, pinned version).</param>
/// <param name="StepKey">The stable <c>(instance, iteration, step)</c> key — the factory derives a deterministic effect id from it (build invariant #2).</param>
/// <param name="ParamsJson">The authored action params (opaque JSON; empty object if none).</param>
/// <param name="OriginatingDecision">The immutable decision carried by the broker into a synchronous reaction.</param>
/// <param name="EffectDecision">The carried decision for the exact record created by the effect, when required.</param>
public readonly record struct WorkflowEffectRequest(
    string CapabilityRef,
    WorkflowInstanceRecord Instance,
    WorkflowStepKey StepKey,
    string ParamsJson,
    AuthorizationDecision? OriginatingDecision = null,
    AuthorizationDecision? EffectDecision = null)
{
    /// <summary>Convenience factory defaulting the params to an empty JSON object.</summary>
    public static WorkflowEffectRequest For(
        string capabilityRef, WorkflowInstanceRecord instance, WorkflowStepKey stepKey, string paramsJson = "{}")
        => new(capabilityRef, instance, stepKey, paramsJson);
}

/// <summary>
/// Builds the domain <see cref="WorkflowEffect"/> for one capability. The concrete impl (in the host /
/// financial cluster) is the ONLY place a workflow-driven CP side-effect is constructed; it is invoked
/// ONLY from inside <see cref="WorkflowEffectBroker"/> (never from the interpreter or a step handler
/// directly) — that invocation fence is what makes the broker the effect-execution boundary (ADR 0143 SC2).
/// <para>
/// <b>INTERNAL (SC2 F-1 type-level containment).</b> This interface — including <see cref="Build"/> — is
/// <see langword="internal"/> to <c>blocks-workflow</c>. A host concrete factory implements it ONLY from an
/// assembly this package explicitly trusts via <c>[InternalsVisibleTo]</c> (PR (b)'s effects adapter). No
/// other assembly can name it, resolve it from DI, or invoke <see cref="Build"/> — so the broker is the sole
/// invoker by construction (the compiler enforces it, not a source-scan an identifier rename could evade).
/// </para>
/// </summary>
internal interface IWorkflowEffectFactory
{
    /// <summary>The capability this factory produces an effect for (matched against an action's CapabilityRef).</summary>
    string CapabilityRef { get; }

    /// <summary>The reach of the effect (Internal vs OutboundExternal — the latter is forced-CP by the broker).</summary>
    WorkflowEffectReach Reach { get; }

    /// <summary>
    /// Builds the effect closure for <paramref name="request"/>. Pure construction — it stages nothing; the
    /// returned <see cref="WorkflowEffect"/> is enlisted by <see cref="IWorkflowStore.AdvanceAsync"/> so its
    /// rows co-commit with the advance. Must be deterministic w.r.t. the step key (build invariant #2).
    /// </summary>
    WorkflowEffect Build(WorkflowEffectRequest request);
}

/// <summary>
/// The PUBLIC metadata surface of the effect-factory registry (SC2 F-1). Exposes ONLY whether a capability is
/// an EFFECTING step and its <see cref="EffectKey"/> (capability + reach) — what the admission validator and
/// the A1 interpreter legitimately need to tell an effecting step from a pure-decision step. It deliberately
/// does NOT expose <c>Resolve</c>: there is NO public path from this seam to a live
/// <see cref="IWorkflowEffectFactory"/> or its <c>Build</c>. Obtaining an effect is the broker's job alone
/// (via the internal <see cref="IWorkflowEffectFactoryResolver"/>).
/// </summary>
public interface IWorkflowEffectCatalog
{
    /// <summary>True iff a factory is registered for <paramref name="capabilityRef"/> (i.e. it is an effecting capability).</summary>
    bool IsEffectingCapability(string capabilityRef);

    /// <summary>The <see cref="EffectKey"/> (capability + reach) for <paramref name="capabilityRef"/>, or <see langword="null"/>.</summary>
    EffectKey? EffectKeyFor(string capabilityRef);
}

/// <summary>
/// The INTERNAL resolve surface (SC2 F-1). Hands out a live <see cref="IWorkflowEffectFactory"/> and is
/// <see langword="internal"/> to <c>blocks-workflow</c>, so ONLY same-assembly code (the
/// <see cref="WorkflowEffectBroker"/>) can obtain a factory to invoke <c>Build</c>. An apps/ interpreter
/// cannot name — let alone inject — this type, so the proven <c>Resolve().Build()</c> bypass no longer
/// compiles from an untrusted assembly. Also carries <see cref="EffectKeyFor"/> so the broker's
/// <see cref="WorkflowEffectBroker.Classify"/> needs only this one internal seam.
/// </summary>
internal interface IWorkflowEffectFactoryResolver
{
    /// <summary>The factory for <paramref name="capabilityRef"/>, or <see langword="null"/> if none is registered.</summary>
    IWorkflowEffectFactory? Resolve(string capabilityRef);

    /// <summary>The <see cref="EffectKey"/> (capability + reach) for <paramref name="capabilityRef"/>, or <see langword="null"/>.</summary>
    EffectKey? EffectKeyFor(string capabilityRef);
}

/// <summary>
/// The default registry — a frozen capability→factory map built from the registered factories. Immutable +
/// thread-safe after construction. A duplicate capability across two factories throws at construction
/// (fail-closed-loudly — an ambiguous effect binding is a wiring error, not a silent last-wins).
/// <para>
/// <b>INTERNAL (SC2 F-1).</b> The registry is <see langword="internal"/>: it composes the internal
/// <see cref="IWorkflowEffectFactory"/> and exposes the internal <see cref="IWorkflowEffectFactoryResolver"/>.
/// External code sees only the public <see cref="IWorkflowEffectCatalog"/> face (metadata), never the resolve
/// surface.
/// </para>
/// </summary>
internal sealed class WorkflowEffectFactoryRegistry : IWorkflowEffectCatalog, IWorkflowEffectFactoryResolver
{
    private readonly FrozenDictionary<string, IWorkflowEffectFactory> _byCapability;

    /// <summary>Builds the registry from the registered factories (DI passes <c>IEnumerable&lt;IWorkflowEffectFactory&gt;</c>).</summary>
    public WorkflowEffectFactoryRegistry(IEnumerable<IWorkflowEffectFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        var map = new Dictionary<string, IWorkflowEffectFactory>(StringComparer.Ordinal);
        foreach (var factory in factories)
        {
            ArgumentNullException.ThrowIfNull(factory);
            if (string.IsNullOrWhiteSpace(factory.CapabilityRef))
            {
                throw new InvalidOperationException(
                    $"An {nameof(IWorkflowEffectFactory)} ({factory.GetType().Name}) declared a null/blank CapabilityRef.");
            }

            if (!map.TryAdd(factory.CapabilityRef, factory))
            {
                throw new InvalidOperationException(
                    $"Two {nameof(IWorkflowEffectFactory)} implementations both claim capability " +
                    $"'{factory.CapabilityRef}' ({map[factory.CapabilityRef].GetType().Name} and " +
                    $"{factory.GetType().Name}) — an effect capability must have exactly one factory (fail-closed-loudly).");
            }
        }

        _byCapability = map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IWorkflowEffectFactory? Resolve(string capabilityRef)
    {
        ArgumentNullException.ThrowIfNull(capabilityRef);
        return _byCapability.TryGetValue(capabilityRef, out var f) ? f : null;
    }

    /// <inheritdoc />
    public bool IsEffectingCapability(string capabilityRef)
    {
        ArgumentNullException.ThrowIfNull(capabilityRef);
        return _byCapability.ContainsKey(capabilityRef);
    }

    /// <inheritdoc />
    public EffectKey? EffectKeyFor(string capabilityRef)
    {
        ArgumentNullException.ThrowIfNull(capabilityRef);
        return _byCapability.TryGetValue(capabilityRef, out var f)
            ? new EffectKey(f.CapabilityRef, f.Reach)
            : null;
    }
}
