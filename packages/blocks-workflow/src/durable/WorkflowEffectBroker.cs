using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  ADR 0143 D1 — the .NET broker-PEP (the fixed enforcement point). The .NET
//  runtime analog of the harborline-sdk `ProposalBroker` (propose→confirm) named by
//  ADR 0143 R1-B/F1 as owed-with-the-build. It is the effect-execution boundary:
//  the SINGLE code path through which a workflow step obtains a stageable
//  WorkflowEffect for a registered capability. It does NOT decide policy — it
//  ENFORCES it: it derives the CP/AP class from the canonical authority registry
//  (ADR 0128; unknown⇒CP), forces OutboundExternal effects to CP (the R1-E
//  data-exfil residual), and gates a CP effect behind confirm-time SoD
//  (D-INV-5, reusing the shipped HumanApprovalHandlerBase.EnsureApproverSeparationOfDuties)
//  before it will build the effect via the (host-supplied) IWorkflowEffectFactory.
//
//  NO SIDE DOOR (SC2). The broker is the ONLY caller of IWorkflowEffectFactory.Build
//  (arch-tested: WorkflowEffectFactoryArchitectureTests). The engine core is
//  financial-free (arch-tested: WorkflowEngineIsFinancialFreeArchitectureTests), so
//  an interpreter/handler CANNOT reach a real CP side-effect except by asking this
//  broker for it — which for a CP capability means a human confirm that passes SoD.
//
//  Two paths, mutually exclusive by class:
//    • AP capability  → BuildAutonomousEffect  (no human; refuses if the capability is CP).
//    • CP capability  → ConfirmAndBuildEffect   (SoD-gated human confirm; refuses if the capability is AP).
//  Both refuse an UNREGISTERED capability (no factory ⇒ nothing to build, fail-closed).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The proposing principal for a CP effect — the party that originated the parked proposal.</summary>
/// <param name="PartyId">The proposer's server-derived party id (ADR 0122 IPartyContext).</param>
/// <param name="IsHuman">Whether the proposer is a human principal (vs an agent/service host).</param>
public readonly record struct WorkflowProposerIdentity(Guid PartyId, bool IsHuman);

/// <summary>The confirming principal for a CP effect — resolved server-side at the confirm boundary.</summary>
/// <param name="PartyId">The confirmer's server-derived party id (ADR 0122 IPartyContext, never body-supplied).</param>
/// <param name="IsHuman">Whether the confirmer is a human principal (an agent/service can never confirm a CP op).</param>
public readonly record struct WorkflowConfirmerIdentity(Guid PartyId, bool IsHuman);

/// <summary>Stable, locale-independent broker-refusal codes (a client localizes off these).</summary>
public static class WorkflowEffectBrokerCodes
{
    /// <summary>The capability has no registered effect factory (nothing to build — fail-closed).</summary>
    public const string UnregisteredCapability = "workflow.effect.unregistered_capability";

    /// <summary>A CP capability was routed to the autonomous (no-human) build path — refused.</summary>
    public const string CpRequiresConfirmation = "workflow.effect.cp_requires_confirmation";

    /// <summary>An AP capability was routed to the CP confirm path — refused (wiring error).</summary>
    public const string ApNotConfirmable = "workflow.effect.ap_not_confirmable";
}

/// <summary>
/// Thrown when the broker refuses to build an effect for a reason OTHER than an SoD violation (which throws
/// <see cref="WorkflowSodViolationException"/>): an unregistered capability, or a class/path mismatch. Carries
/// a stable <see cref="WorkflowEffectBrokerCodes"/> code.
/// </summary>
public sealed class WorkflowEffectAuthorizationException(string code, string message)
    : InvalidOperationException(message)
{
    /// <summary>The stable refusal code (a localizing client keys off this).</summary>
    public string Code { get; } = code;
}

/// <summary>
/// The broker-PEP contract (ADR 0143 D1). Classifies an effecting capability and produces a stageable
/// <see cref="WorkflowEffect"/> only through the class-appropriate, gated path.
/// </summary>
public interface IWorkflowEffectBroker
{
    /// <summary>
    /// The EFFECTIVE CP/AP class of <paramref name="capabilityRef"/>: the ADR-0128 registry-derived class
    /// (unknown⇒CP), FORCED to <see cref="ActionClassification.CP"/> when the registered effect's reach is
    /// <see cref="WorkflowEffectReach.OutboundExternal"/> (the data-exfil residual). Never returns Unspecified.
    /// </summary>
    ActionClassification Classify(string capabilityRef);

    /// <summary>True iff <see cref="Classify"/> resolves <paramref name="capabilityRef"/> to CP.</summary>
    bool RequiresConfirmation(string capabilityRef);

    /// <summary>
    /// AP path — builds the effect autonomously (no human). REFUSES (throws
    /// <see cref="WorkflowEffectAuthorizationException"/>) if the capability classifies CP (a CP effect can
    /// never be produced without a human confirm) or is unregistered.
    /// </summary>
    WorkflowEffect BuildAutonomousEffect(
        WorkflowEffectRequest request,
        AuthorizationDecision originatingDecision);

    /// <summary>Independently initiated AP path; the gate decides before factory or decision-sink access.</summary>
    ValueTask<WorkflowEffect> BuildAutonomousEffectAsync(
        WorkflowEffectRequest request,
        AuthorizationWriteContext authority,
        CancellationToken ct = default);

    /// <summary>
    /// CP path — enforces confirm-time separation of duties (D-INV-5), records the D-INV-7 metric, then builds
    /// the effect via the registered factory. THROWS <see cref="WorkflowSodViolationException"/> on an SoD
    /// violation (non-human confirmer, or agent self-confirm) — and NOTHING is built or recorded-confirmed.
    /// REFUSES (throws <see cref="WorkflowEffectAuthorizationException"/>) if the capability classifies AP
    /// (use <see cref="BuildAutonomousEffect"/>) or is unregistered.
    /// </summary>
    /// <param name="request">The effect request (capability, instance, step key, params).</param>
    /// <param name="proposer">The party that proposed the parked CP op.</param>
    /// <param name="confirmer">The party confirming (server-derived; an agent/service can never confirm).</param>
    /// <param name="originatingDecision">The immutable decision that admitted the synchronous reaction.</param>
    WorkflowEffect ConfirmAndBuildEffect(
        WorkflowEffectRequest request,
        WorkflowProposerIdentity proposer,
        WorkflowConfirmerIdentity confirmer,
        AuthorizationDecision originatingDecision);

    /// <summary>Independently initiated CP path; the gate decides before factory or decision-sink access.</summary>
    ValueTask<WorkflowEffect> ConfirmAndBuildEffectAsync(
        WorkflowEffectRequest request,
        WorkflowProposerIdentity proposer,
        WorkflowConfirmerIdentity confirmer,
        AuthorizationWriteContext authority,
        CancellationToken ct = default);

    /// <summary>
    /// Records a human OVERRIDE (reject / send-back) of a parked CP proposal for the D-INV-7 override-rate
    /// metric. No effect is built. Best-effort; never throws.
    /// </summary>
    void RecordOverride(
        WorkflowEffectRequest request,
        WorkflowConfirmerIdentity confirmer,
        AuthorizationDecision originatingDecision);
}

/// <summary>
/// The default broker-PEP. Stateless w.r.t. proposals (the durable park record IS the proposal store — the
/// broker does not keep an in-memory map that could desync with the durable park; the <em>confirm route</em>
/// re-derives the request from the pinned definition + the parked basis — that re-derivation is PR (b)'s
/// responsibility, see F-3). Deterministic + thread-safe.
/// <para>
/// <b>INTERNAL (SC2 F-1).</b> The concrete broker is <see langword="internal"/> — external code composes it
/// only as the public <see cref="IWorkflowEffectBroker"/> seam (via DI). Being internal lets it hold the
/// internal <see cref="IWorkflowEffectFactoryResolver"/> (the sole path to a live factory) without exposing
/// that resolve surface publicly.
/// </para>
/// </summary>
internal sealed class WorkflowEffectBroker : IWorkflowEffectBroker
{
    private readonly IWorkflowEffectFactoryResolver _factories;
    private readonly ICapabilityAuthorityRegistry _authority;
    private readonly IWorkflowApprovalDecisionSink _decisions;
    private readonly TimeProvider _time;
    private readonly AuthorizationGate _gate;

    /// <summary>Constructs the broker over the internal effect-factory resolver, the authority registry, the decision sink, and a clock.</summary>
    public WorkflowEffectBroker(
        IWorkflowEffectFactoryResolver factories,
        ICapabilityAuthorityRegistry authority,
        IWorkflowApprovalDecisionSink decisions,
        TimeProvider time,
        AuthorizationGate gate)
    {
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <inheritdoc />
    public ActionClassification Classify(string capabilityRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityRef);

        // Registry-derived class (ADR 0128; unknown⇒CP — the fail-closed default lives in the registry).
        var derived = _authority.AuthorityOf(capabilityRef);

        // OutboundExternal effects are FORCED to CP regardless of the authority label (ADR 0143 R1-E
        // red-team residual — the data-exfil chain: an autonomous workflow that can email data out is a
        // consequential act). An unregistered capability has no reach; the registry default (CP) already
        // covers it.
        var effectKey = _factories.EffectKeyFor(capabilityRef);
        if (effectKey is { Reach: WorkflowEffectReach.OutboundExternal })
        {
            return ActionClassification.CP;
        }

        // Never surface Unspecified — the registry returns CP/AP only, and CP is the fail-closed default.
        return derived == ActionClassification.AP ? ActionClassification.AP : ActionClassification.CP;
    }

    /// <inheritdoc />
    public bool RequiresConfirmation(string capabilityRef)
        => Classify(capabilityRef) == ActionClassification.CP;

    /// <inheritdoc />
    public WorkflowEffect BuildAutonomousEffect(
        WorkflowEffectRequest request,
        AuthorizationDecision originatingDecision)
    {
        VerifyOriginatingDecision(request, originatingDecision);
        return BuildAutonomousEffectCore(request with { OriginatingDecision = originatingDecision });
    }

    public async ValueTask<WorkflowEffect> BuildAutonomousEffectAsync(
        WorkflowEffectRequest request,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        var decision = await DecideIndependentAsync(request, authority, ct).ConfigureAwait(false);
        return BuildAutonomousEffectCore(request with { OriginatingDecision = decision });
    }

    private WorkflowEffect BuildAutonomousEffectCore(WorkflowEffectRequest request)
    {
        var factory = ResolveOrThrow(request.CapabilityRef);

        // A CP capability can NEVER be produced without a human confirm — fail-closed on the autonomous path.
        if (Classify(request.CapabilityRef) == ActionClassification.CP)
        {
            throw new WorkflowEffectAuthorizationException(
                WorkflowEffectBrokerCodes.CpRequiresConfirmation,
                $"capability '{request.CapabilityRef}' classifies CP — it cannot be produced on the autonomous " +
                "path; a CP effect requires propose-then-confirm (ConfirmAndBuildEffect).");
        }

        return factory.Build(request);
    }

    /// <inheritdoc />
    public WorkflowEffect ConfirmAndBuildEffect(
        WorkflowEffectRequest request,
        WorkflowProposerIdentity proposer,
        WorkflowConfirmerIdentity confirmer,
        AuthorizationDecision originatingDecision)
    {
        VerifyOriginatingDecision(request, originatingDecision);
        return ConfirmAndBuildEffectCore(
            request with { OriginatingDecision = originatingDecision }, proposer, confirmer);
    }

    public async ValueTask<WorkflowEffect> ConfirmAndBuildEffectAsync(
        WorkflowEffectRequest request,
        WorkflowProposerIdentity proposer,
        WorkflowConfirmerIdentity confirmer,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        var decision = await DecideIndependentAsync(request, authority, ct).ConfigureAwait(false);
        return ConfirmAndBuildEffectCore(
            request with { OriginatingDecision = decision }, proposer, confirmer);
    }

    private WorkflowEffect ConfirmAndBuildEffectCore(
        WorkflowEffectRequest request,
        WorkflowProposerIdentity proposer,
        WorkflowConfirmerIdentity confirmer)
    {
        var factory = ResolveOrThrow(request.CapabilityRef);

        // The CP confirm path is for CP capabilities. Routing an AP capability here is a wiring error
        // (the SoD ceremony is meaningless for an AP effect) — refuse loudly rather than silently over-gate.
        if (Classify(request.CapabilityRef) == ActionClassification.AP)
        {
            throw new WorkflowEffectAuthorizationException(
                WorkflowEffectBrokerCodes.ApNotConfirmable,
                $"capability '{request.CapabilityRef}' classifies AP — build it on the autonomous path " +
                "(BuildAutonomousEffect); the CP confirm path is for CP effects only.");
        }

        // D-INV-5 — confirm-time separation of duties, the ONE gate every CP confirm funnels through. Reuses
        // the shipped rule (HumanApprovalHandlerBase.EnsureApproverSeparationOfDuties): a non-human confirmer
        // is refused; an agent-proposed CP op needs a DISTINCT confirmer (a human proposer may self-confirm on
        // the single-user desktop). Throws WorkflowSodViolationException — BEFORE the effect is built or a
        // confirm is recorded, so a refused confirm neither posts nor pollutes the override-rate metric.
        HumanApprovalHandlerBase.EnsureApproverSeparationOfDuties(
            proposer.PartyId, proposer.IsHuman, confirmer.PartyId, confirmer.IsHuman, request.CapabilityRef);

        // SoD passed — this is a genuine human confirmation. Build the effect via the registered factory
        // (the ONLY place IWorkflowEffectFactory.Build is called) and record the D-INV-7 metric.
        var effect = factory.Build(request);
        _decisions.Record(new WorkflowApprovalDecision(
            request.CapabilityRef, request.Instance.Id, confirmer.PartyId,
            WorkflowApprovalOutcome.Confirmed,
            request.OriginatingDecision?.Request.At
                ?? throw new InvalidOperationException("A confirmed reaction must carry its deciding instant.")));
        return effect;
    }

    /// <inheritdoc />
    public void RecordOverride(
        WorkflowEffectRequest request,
        WorkflowConfirmerIdentity confirmer,
        AuthorizationDecision originatingDecision)
    {
        VerifyOriginatingDecision(request, originatingDecision);
        _decisions.Record(new WorkflowApprovalDecision(
            request.CapabilityRef, request.Instance.Id, confirmer.PartyId,
            WorkflowApprovalOutcome.Overridden, originatingDecision.Request.At));
    }

    private async ValueTask<AuthorizationDecision> DecideIndependentAsync(
        WorkflowEffectRequest request,
        AuthorizationWriteContext authority,
        CancellationToken ct)
    {
        if (!string.Equals(request.Instance.TenantId, authority.Tenant.Value, StringComparison.Ordinal))
            throw new ArgumentException("The workflow instance tenant does not match the write authority.", nameof(request));
        var decision = await _gate.DecideAsync(
            authority.Request(
                AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite),
                "record",
                request.Instance.Id),
            ct).ConfigureAwait(false);
        return decision.RequireAllowed();
    }

    private static void VerifyOriginatingDecision(
        WorkflowEffectRequest request,
        AuthorizationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        decision.RequireAllowed();
        var decided = decision.Request;
        if (string.IsNullOrWhiteSpace(decided.Principal.Value)
            || decided.Tenant != new TenantId(request.Instance.TenantId)
            || decided.Act.Operation.Value != TeamRolePermissions.RecordsWrite
            || decided.Target.RecordKind != "record"
            || decided.Target.RecordId != request.Instance.Id
            || decision.DecidedAt != decided.At)
            throw new ArgumentException("The originating decision does not match the workflow record.", nameof(decision));
    }

    private IWorkflowEffectFactory ResolveOrThrow(string capabilityRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityRef);
        return _factories.Resolve(capabilityRef)
            ?? throw new WorkflowEffectAuthorizationException(
                WorkflowEffectBrokerCodes.UnregisteredCapability,
                $"no effect factory is registered for capability '{capabilityRef}' — the broker cannot build " +
                "an effect it has no factory for (fail-closed). Register an IWorkflowEffectFactory for it.");
    }
}
