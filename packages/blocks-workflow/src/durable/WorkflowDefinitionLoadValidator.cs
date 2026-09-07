using System.Text.Json;

namespace Harborline.Api.Blocks.Workflow.Durable;

// ─────────────────────────────────────────────────────────────────────────────
//  ADR 0135 A1 R-1 (the D7-re-pin clause) + ADR 0143 R1-E (SC2 DoD item 4) —
//  LOAD-TIME re-validation of a persisted WorkflowDefinition.
//
//  RegisterAsync admits a definition at PERSIST (WorkflowAdmissionValidator). But R-1
//  requires the CP-reachability gate to be "re-run on EVERY D7 re-pin ... an edit that
//  flips a step HumanTask→Automated or routes a CP edge around the human-task must be
//  caught AFTER instantiation, not only at first load", and R1-E names load-time
//  re-validation as a missing SC2 item. The persisted `authored` JSON is the source of
//  truth on load; a definition admitted under an OLDER capability registry can become
//  inadmissible if a capability is later reclassified to CP. This gate re-parses the
//  stored authored JSON with the SAME canonical mapper + re-runs the SAME admission
//  validator at the moment a definition is loaded FOR EXECUTION — fail-closed: a
//  now-inadmissible definition throws WorkflowAdmissionException and never executes.
//
//  It is deliberately the load path only — a BUILDER reload (edit-to-FIX) uses the
//  lenient store Get; this gate is what an instantiation / D7-re-pin / interpreter
//  path calls before it is allowed to run a definition. Deterministic + idempotent
//  (the admission validator is structural over an immutable registry).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Re-parses + re-validates a persisted <see cref="WorkflowDefinition"/> at load-for-execution (ADR 0135
/// A1 R-1 / ADR 0143 R1-E). The fail-closed gate an instantiation / D7-re-pin / interpreter path calls
/// before executing an authored definition.
/// </summary>
public interface IWorkflowDefinitionLoadValidator
{
    /// <summary>
    /// Re-parses <paramref name="authored"/> into the lean model (stamping the server-owned identity) and
    /// re-runs admission. Returns the admitted model, or THROWS <see cref="WorkflowAdmissionException"/> if
    /// the persisted definition is no longer admissible (fail-closed — it must not execute).
    /// </summary>
    WorkflowDefinition ReadAdmissibleOrThrow(JsonElement authored, string tenant, string key, string version);

    /// <summary>Non-throwing form: the admission result for a persisted definition (empty violations ⇒ admissible).</summary>
    WorkflowAdmissionResult Revalidate(JsonElement authored, string tenant, string key, string version);
}

/// <summary>
/// The default load-time re-validator — the canonical wire mapper (<see cref="WorkflowDefinitionWireMapper"/>)
/// + the shipped <see cref="IWorkflowAdmissionValidator"/>. Shares BOTH with the register-time path, so
/// "admitted at persist" and "re-validated at load" can never diverge.
/// </summary>
public sealed class WorkflowDefinitionLoadValidator : IWorkflowDefinitionLoadValidator
{
    private readonly IWorkflowAdmissionValidator _admission;

    /// <summary>Constructs the re-validator over an explicit admission validator (DI/test seam).</summary>
    public WorkflowDefinitionLoadValidator(IWorkflowAdmissionValidator admission)
        => _admission = admission ?? throw new ArgumentNullException(nameof(admission));

    /// <summary>Constructs the re-validator over the canonical admission validator (registry-derived).</summary>
    public WorkflowDefinitionLoadValidator()
        : this(new WorkflowAdmissionValidator())
    {
    }

    /// <inheritdoc />
    public WorkflowDefinition ReadAdmissibleOrThrow(JsonElement authored, string tenant, string key, string version)
    {
        var model = WorkflowDefinitionWireMapper.ToModel(authored, tenant, key, version);
        _admission.EnsureAdmissible(model);
        return model;
    }

    /// <inheritdoc />
    public WorkflowAdmissionResult Revalidate(JsonElement authored, string tenant, string key, string version)
        => _admission.Validate(WorkflowDefinitionWireMapper.ToModel(authored, tenant, key, version));
}
