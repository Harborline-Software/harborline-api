using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  WF-KEY — the DECLARATIVE workflow-definition model (ADR 0140; the process keystone).
//
//  The data-driven authoring shape of a hand-written typed IWorkflowStepHandler — the
//  process analog of the dynamic-forms FormDefinition. States / guarded transitions /
//  triggers / actions as DATA. The .NET mirror of @harborline-software/api-contracts WorkflowDefinition
//  (packages/contracts/src/workflow.ts); a drift between the two is what the structural
//  parity test pins.
//
//  WF-KEY v1 AUTHORS + VALIDATES (WorkflowAdmissionValidator) + STORES + SIMULATES this
//  model; it is NOT executed by a general interpreter. Live execution stays gated behind
//  the deferred A1 interpreter + the broker-PEP (ADR 0135 Amendment 2026-06-24). These
//  POCOs carry NO EF dependency (mirrors DurableWorkflowModel.cs) and NO runtime authority.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Lifecycle status of a <see cref="WorkflowDefinition"/> revision.</summary>
public enum WorkflowDefinitionStatus
{
    Draft = 0,
    Published = 1,
    Deprecated = 2,
    Withdrawn = 3,
}

/// <summary>Editability posture (ADR 0135 §4.4). Locked = rigid orchestration; Editable = re-pinned on edit.</summary>
public enum WorkflowMutability
{
    Locked = 0,
    Editable = 1,
}

/// <summary>A state's kind. A <see cref="Terminal"/> state has no outgoing transitions.</summary>
public enum WorkflowStateKind
{
    Normal = 0,
    Terminal = 1,
}

/// <summary>
/// The authoring-declared authority an action consumes (ADR 0128). The default
/// <see cref="Unspecified"/> (0) is the fail-closed sentinel: an action that did not
/// declare a classification is REFUSED at admission (never default-allow).
/// </summary>
public enum ActionClassification
{
    /// <summary>No classification declared — refused at admission (fail-closed).</summary>
    Unspecified = 0,

    /// <summary>Consequential: post to GL, send money, delete, touch a CP-locked field. MUST be human-gated.</summary>
    CP = 1,

    /// <summary>Autonomous-permitted.</summary>
    AP = 2,
}

/// <summary>What an action does (ADR 0140 — the security-relevant surface).</summary>
public enum WorkflowActionKind
{
    Notify = 0,
    CreateRecord = 1,
    UpdateField = 2,
    InvokeService = 3,
    StartSubProcess = 4,
    EmitEvent = 5,
}

/// <summary>A state an instance parks at / advances through (the engine's CurrentStep).</summary>
public sealed class WorkflowStateDef
{
    /// <summary>Stable state id (matches <see cref="WorkflowInstanceRecord.CurrentStep"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Terminal ⇒ no outgoing transitions; Normal ⇒ must have ≥1 outgoing.</summary>
    public WorkflowStateKind Kind { get; init; } = WorkflowStateKind.Normal;

    /// <summary>Presentation lens hint (flow/gantt/kanban); does not affect admission.</summary>
    public string? ViewHint { get; init; }
}

/// <summary>
/// A guarded edge {from, on, to}. The declarative form of a typed handler's switch decision.
/// The optional <see cref="Guard"/> is a rule id evaluated by IGuardEvaluator.
/// </summary>
public sealed class WorkflowTransitionDef
{
    public required string Id { get; init; }

    /// <summary>Source state id.</summary>
    public required string From { get; init; }

    /// <summary>The trigger that offers this transition (a <see cref="WorkflowTriggerBindingDef.Id"/>).</summary>
    public required string On { get; init; }

    /// <summary>Destination state id.</summary>
    public required string To { get; init; }

    /// <summary>Optional guard rule id.</summary>
    public string? Guard { get; init; }

    /// <summary>Roles required by this declarative transition gate.</summary>
    public IReadOnlyList<string> RequiredRoles { get; init; } = [];

    /// <summary>Record-derived standings required by this transition gate.</summary>
    public IReadOnlyList<RecordStandingReference> RequiredStandings { get; init; } = [];
}

/// <summary>A trigger the definition declares against (reuses the four <see cref="WorkflowTriggerKind"/>).</summary>
public sealed class WorkflowTriggerBindingDef
{
    public required string Id { get; init; }

    public required WorkflowTriggerKind Kind { get; init; }

    /// <summary>Event — the subject-form/domain event type that fires this.</summary>
    public string? EventType { get; init; }

    /// <summary>Schedule — the RRULE the daemon expands.</summary>
    public string? Rrule { get; init; }

    /// <summary>HumanAction — the parked human-task key whose typed result fires this.</summary>
    public string? Task { get; init; }

    /// <summary>DependencyComplete — the prerequisite step / sub-process id.</summary>
    public string? Dependency { get; init; }
}

/// <summary>
/// A guarded capability-call fired on entering a state or on a transition (ADR 0140). Carries an
/// authoring-declared CP/AP <see cref="Classification"/> that drives the CP-park fence. Adds no new
/// authority primitive.
/// </summary>
public sealed class WorkflowActionBindingDef
{
    public required string Id { get; init; }

    /// <summary>The state id this action fires on entering. Exactly one of OnState / OnTransition is set.</summary>
    public string? OnState { get; init; }

    /// <summary>The transition id this action fires on. Exactly one of OnState / OnTransition is set.</summary>
    public string? OnTransition { get; init; }

    public WorkflowActionKind Kind { get; init; }

    /// <summary>The guarded capability invoked (a Tool from the action palette).</summary>
    public required string CapabilityRef { get; init; }

    /// <summary>Authoring-declared authority class. Unspecified ⇒ refused at admission (fail-closed).</summary>
    public ActionClassification Classification { get; init; } = ActionClassification.Unspecified;

    /// <summary>Optional action-condition rule id ("should this run?").</summary>
    public string? Condition { get; init; }

    /// <summary>Roles required by this declarative action gate.</summary>
    public IReadOnlyList<string> RequiredRoles { get; init; } = [];

    /// <summary>Record-derived standings required by this action gate.</summary>
    public IReadOnlyList<RecordStandingReference> RequiredStandings { get; init; } = [];
}

/// <summary>A content-addressed reference to a FormDefinition (the composition binding, ADR 0140 D3).</summary>
public sealed class FormDefinitionRef
{
    public required string FormId { get; init; }
    public required string Version { get; init; }
}

/// <summary>A strongly typed declarative workflow-definition key.</summary>
/// <param name="Value">The stable key value.</param>
public readonly record struct WorkflowDefinitionKey(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A strongly typed semantic version for a declarative workflow definition.</summary>
/// <param name="Major">Major segment.</param>
/// <param name="Minor">Minor segment.</param>
/// <param name="Patch">Patch segment.</param>
public readonly record struct WorkflowDefinitionVersion(int Major, int Minor, int Patch)
{
    /// <summary>Parses the canonical <c>major.minor.patch</c> representation.</summary>
    public static WorkflowDefinitionVersion Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = value.Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || !int.TryParse(parts[2], out var patch)
            || major < 0
            || minor < 0
            || patch < 0)
        {
            throw new FormatException($"Workflow definition version must be major.minor.patch; got '{value}'.");
        }

        return new WorkflowDefinitionVersion(major, minor, patch);
    }

    /// <inheritdoc />
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>Workflow-specific provenance carried by the common definition envelope.</summary>
public sealed record WorkflowDefinitionProvenance
{
    /// <summary>The empty provenance used until workflow authorship and lineage are introduced.</summary>
    public static WorkflowDefinitionProvenance Unspecified { get; } = new();
}

/// <summary>
/// The canonical declarative workflow keystone (the process analog of FormDefinition). The editable
/// template the builder authors; the ADR 0135 engine instantiates it into a Process.
/// </summary>
public sealed class WorkflowDefinition
{
    private DefinitionEnvelope<WorkflowDefinitionKey, WorkflowDefinitionVersion, TenantId, WorkflowDefinitionProvenance> _envelope =
        new(
            default,
            default,
            TenantId.System,
            CascadeLayer.Tenant,
            WorkflowDefinitionProvenance.Unspecified,
            Array.Empty<DefinitionRequirement>());

    /// <summary>The definition's single typed control-metadata authority.</summary>
    public DefinitionEnvelope<WorkflowDefinitionKey, WorkflowDefinitionVersion, TenantId, WorkflowDefinitionProvenance> Envelope
    {
        get => _envelope;
        init => _envelope = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Definition key (matches <see cref="WorkflowInstanceRecord.DefinitionKey"/>).</summary>
    public string Key
    {
        get => Envelope.Identity.Value;
        init => _envelope = Envelope with { Identity = new WorkflowDefinitionKey(value) };
    }

    /// <summary>Canonical "{major}.{minor}.{patch}" — the D7-pinned version.</summary>
    public string Version
    {
        get => Envelope.Version.ToString();
        init => _envelope = Envelope with { Version = WorkflowDefinitionVersion.Parse(value) };
    }

    public WorkflowDefinitionStatus Status { get; init; } = WorkflowDefinitionStatus.Draft;

    /// <summary>Owning tenant id.</summary>
    public string Tenant
    {
        get => Envelope.Tenant.Value;
        init => _envelope = Envelope with { Tenant = new TenantId(value) };
    }

    /// <summary>The FormDefinition this process operates on (ADR 0140 D3 composition binding).</summary>
    public FormDefinitionRef? SubjectFormRef { get; init; }

    public WorkflowMutability Mutability { get; init; } = WorkflowMutability.Locked;

    /// <summary>The state new instances start in (must be a <see cref="States"/> id).</summary>
    public required string InitialState { get; init; }

    public IReadOnlyList<WorkflowStateDef> States { get; init; } = [];

    public IReadOnlyList<WorkflowTransitionDef> Transitions { get; init; } = [];

    public IReadOnlyList<WorkflowTriggerBindingDef> Triggers { get; init; } = [];

    public IReadOnlyList<WorkflowActionBindingDef> Actions { get; init; } = [];

    /// <summary>The guard rule ids referenced by transitions/actions exist in this set (rule ids only here).</summary>
    public IReadOnlyList<string> GuardRuleIds { get; init; } = [];

    /// <summary>The exact pack/version that declared this revision, or null for tenant-authored workflows.</summary>
    public PackProjectionSource? PackSource { get; internal init; }
}
