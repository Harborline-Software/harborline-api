using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

/// <summary>
/// Step identifiers + definition key for the grant-revocation process (ADR 0117 amendment A4). Public so
/// host wiring + arch-tests share the names.
/// </summary>
public static class GrantRevocationSteps
{
    /// <summary>The definition key the dispatcher matches an instance's <c>DefinitionKey</c> against.</summary>
    public const string DefinitionKey = "grant-revocation";

    /// <summary>Entry step — parks the revocation on the CP confirm human-task.</summary>
    public const string Start = "start";

    /// <summary>The CP confirm step a revocation parks on (human-task interim).</summary>
    public const string Confirm = "confirm";

    /// <summary>Terminal step after the grant is dropped (revokedAt stamped) + key-rotation flagged.</summary>
    public const string Revoked = "revoked";

    /// <summary>Terminal step when the revocation is declined at confirm.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// The host-supplied surface the <see cref="GrantRevocationHandler"/> uses to drop the grant AND execute
/// forward-cutoff key-rotation: it builds the revoke <see cref="WorkflowEffect"/> that stamps <c>RevokedAt</c>
/// onto the durable grant — and, in the SAME atomic advance, schedules key-rotation for the affected roles
/// (F-2) — inside the engine's atomic advance. Mirrors <see cref="IGrantIssuanceContext"/>.
/// </summary>
/// <remarks>
/// <b>Atomicity is the host's contract (F-2).</b> The host owns the concrete unit-of-work (the EF
/// <c>DbContext</c> in production, the test transactional store under test), so it is the host — not the
/// handler — that stages both the <c>RevokedAt</c> stamp AND the
/// <see cref="IGrantKeyRotationCoordinator"/> rotation onto that one unit of work, so a crash before commit
/// rolls BOTH back (never a revoke-without-rotation). The handler stays store-and-side-effect-wiring-free,
/// exactly as the issuance handler does — it only decides the outcome + the rotation intent.
/// </remarks>
public interface IGrantRevocationContext
{
    /// <summary>
    /// Builds the revoke-and-rotate effect for the grant identified by <paramref name="request"/> at
    /// <paramref name="stepKey"/>. The effect stages the <c>RevokedAt</c> stamp onto the advance's in-flight
    /// unit-of-work AND schedules forward-cutoff key-rotation for <paramref name="affectedRoles"/> in the same
    /// commit window (F-2); the step key's value is the store idempotency source-reference.
    /// </summary>
    /// <param name="request">The revocation being applied.</param>
    /// <param name="affectedRoles">The roles the revoked grant conferred — the key purposes to rotate. Empty ⇒ no rotation.</param>
    /// <param name="stepKey">The revoke step key (the store idempotency source-reference).</param>
    WorkflowEffect BuildRevokeEffect(
        GrantRevocationRequest request,
        IReadOnlyList<RoleReference> affectedRoles,
        WorkflowStepKey stepKey);
}

/// <summary>
/// The working-state payload of a revocation process instance (read from <c>StateJson</c>).
/// </summary>
/// <param name="GrantId">The grant to revoke.</param>
/// <param name="TenantId">The granting tenant (the home / isolation boundary).</param>
/// <param name="RevokedBy">The principal performing the revocation.</param>
/// <param name="RevokedAt">The revocation instant to stamp (pinned for deterministic replay).</param>
/// <param name="Reason">The allowlisted audited revocation reason.</param>
/// <param name="AffectedRoles">
/// The roles the revoked grant conferred — the affected key purposes the F-2 forward-cutoff rotation rotates
/// (ADR 0117 amendment A4). Pinned at instantiation so the rotation set is deterministic on replay without a
/// fresh store read. Empty for an inert grant ⇒ nothing to rotate.
/// </param>
public sealed record GrantRevocationRequest(
    Guid GrantId,
    string TenantId,
    string RevokedBy,
    DateTimeOffset RevokedAt,
    GrantReason Reason,
    IReadOnlyList<RoleReference>? AffectedRoles = null);

/// <summary>
/// Handler for the grant-revocation process (ADR 0117 amendment A4) — a CP-class durable process on the
/// ADR 0135 engine. Revocation drops the grant record (stamps <c>RevokedAt</c>) AND EXECUTES forward-cutoff
/// key-rotation (the F-2 follow-on — previously v1 only FLAGGED rotation), via the host-supplied
/// <see cref="IGrantKeyRotationCoordinator"/> over the real ADR 0068/0118/0046 rotation primitive.
/// </summary>
/// <remarks>
/// <para>
/// Flow (steps: <c>confirm → revoked / cancelled</c>): the revocation parks on the <c>confirm</c> CP
/// human-task; <c>approve</c> ⇒ advance to <c>revoked</c> WITH a composite effect that stamps
/// <c>RevokedAt</c> AND schedules key-rotation, both committed in the one atomic advance; <c>reject</c> ⇒
/// terminal <c>cancelled</c>, NO effect.
/// </para>
/// <para>
/// <b>Key-rotation is EXECUTED, not just flagged (F-2 — the named v1 gap, now closed).</b> On approve the
/// handler composes the host's revoke effect with a rotation-scheduling step into ONE
/// <see cref="WorkflowEffect"/> staged on the atomic advance. The rotation fires through
/// <see cref="IGrantKeyRotationCoordinator"/> (the host wires it over the real
/// <c>IKeyRotationScheduler.ScheduleAsync</c>, trigger <c>KeyRotationTrigger.RoleChange</c> per ADR 0068
/// §1.4 + ADR 0118 D4 + ADR 0046-A6 §A6.1), so a revoked principal cannot read data encrypted AFTER the
/// cutoff even if it later compromises the pre-rotation key — the forward-secrecy property the v1 flag
/// previously deferred. The outcome event reports <c>keyRotationStatus: "executed"</c>.
/// </para>
/// <para>
/// <b>The named A4 exception still stands.</b> Rotation is a FORWARD cutoff, not retroactive — a
/// <c>residency: Cache</c> grantee's already-cached plaintext stays readable (A4's explicitly-named bounded
/// residency-cache window; no remote-wipe in v1). Rotation closes forward-secrecy against a FUTURE key
/// compromise, not the residency-cache window.
/// </para>
/// <para>
/// <b>Atomic + idempotent.</b> Because rotation rides the same atomic advance as the <c>RevokedAt</c> stamp,
/// a crash before commit rolls BOTH back and the resume re-runs the step once (the engine's idempotency row
/// gates re-entry) — no double-rotation, no revoke-without-rotation. The coordinator SHOULD be idempotent
/// for the same request.
/// </para>
/// </remarks>
public sealed class GrantRevocationHandler : IWorkflowStepHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IGrantRevocationContext _context;

    /// <summary>
    /// Constructs the handler over the host's revoke-and-rotate effect builder (the host wires both the
    /// <c>RevokedAt</c> store stamp and the F-2 <see cref="IGrantKeyRotationCoordinator"/> rotation behind
    /// <see cref="IGrantRevocationContext.BuildRevokeEffect"/>, staged atomically onto the one unit of work).
    /// </summary>
    public GrantRevocationHandler(IGrantRevocationContext context)
        => _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public string DefinitionKey => GrantRevocationSteps.DefinitionKey;

    /// <inheritdoc />
    public ValueTask<WorkflowStepOutcome> DecideAsync(
        WorkflowInstanceRecord instance,
        WorkflowTrigger trigger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var request = Deserialize(instance.StateJson);

        return ValueTask.FromResult(trigger.Step switch
        {
            // The initial event parks the revocation on the CP confirm human-task.
            GrantRevocationSteps.Start => Park(request),
            GrantRevocationSteps.Confirm => ResolveHumanAction(instance, request, trigger),
            _ => throw new InvalidOperationException(
                $"{nameof(GrantRevocationHandler)} received a trigger for unknown step '{trigger.Step}' "
                + $"(instance '{instance.Id}')."),
        });
    }

    private static WorkflowStepOutcome Park(GrantRevocationRequest request)
    {
        var basis = JsonSerializer.Serialize(new
        {
            kind = "grant-revocation-basis",
            step = GrantRevocationSteps.Confirm,
            grantId = request.GrantId,
            tenantId = request.TenantId,
            revokedBy = request.RevokedBy,
            keyRotationRequired = true,
            typedOutcomes = new[] { "approve", "reject" },
        }, JsonOptions);
        return WorkflowStepOutcome.Park(GrantRevocationSteps.Confirm, basis);
    }

    private WorkflowStepOutcome ResolveHumanAction(
        WorkflowInstanceRecord instance,
        GrantRevocationRequest request,
        WorkflowTrigger trigger)
    {
        var action = HumanApprovalHandlerBase.ReadHumanAction(trigger.PayloadJson, GrantRevocationSteps.Confirm);

        switch (action)
        {
            case "approve":
            {
                var revokeStepKey = new WorkflowStepKey(instance.Id, instance.Iteration, GrantRevocationSteps.Revoked);
                // F-2: the host context stages the RevokedAt stamp AND forward-cutoff key-rotation onto ONE
                // atomic advance. Rotation now EXECUTES (it no longer only flags) — a revoked principal cannot
                // read data encrypted after the cutoff even if it later compromises the pre-rotation key. Both
                // ride the same commit, so a crash rolls both back and the resume re-runs once (idempotency-
                // gated). The residency-cache window (A4) remains the named exception.
                var affectedRoles = request.AffectedRoles ?? Array.Empty<RoleReference>();
                var effect = _context.BuildRevokeEffect(request, affectedRoles, revokeStepKey);
                var result = JsonSerializer.Serialize(new
                {
                    decision = "revoked",
                    grantId = request.GrantId,
                    revokedAt = request.RevokedAt,
                    keyRotationRequired = true,
                    keyRotationStatus = "executed",
                    keyRotationTrigger = "RoleChange",
                    keyRotationBasis = "ADR 0068 §1.4 KeyRotationTrigger.RoleChange + ADR 0118 D4 + ADR 0046-A6 §A6.1",
                    affectedRoles,
                    residencyCacheWindowNote =
                        "Forward cutoff only; a residency:Cache grantee's already-cached plaintext stays "
                        + "readable (A4 named exception).",
                }, JsonOptions);
                return WorkflowStepOutcome.Complete(
                    finalStep: GrantRevocationSteps.Revoked,
                    effect: effect,
                    resultJson: result,
                    eventDataJson: result);
            }

            case "reject":
                return WorkflowStepOutcome.Complete(
                    finalStep: GrantRevocationSteps.Cancelled,
                    effect: null,
                    resultJson: "{\"decision\":\"cancelled\"}",
                    eventDataJson: "{\"decision\":\"cancelled\"}");

            default:
                throw new InvalidOperationException(
                    $"{nameof(GrantRevocationHandler)} received an unknown human action '{action}' for "
                    + $"instance '{instance.Id}' (expected approve / reject).");
        }
    }

    private static GrantRevocationRequest Deserialize(string stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson) || stateJson == "{}")
        {
            throw new InvalidOperationException(
                "A grant-revocation instance must carry a serialized GrantRevocationRequest in StateJson.");
        }

        return JsonSerializer.Deserialize<GrantRevocationRequest>(stateJson, JsonOptions)
            ?? throw new InvalidOperationException("StateJson did not deserialize to a GrantRevocationRequest.");
    }

    /// <summary>Serializes a <see cref="GrantRevocationRequest"/> to the canonical <c>StateJson</c> form.</summary>
    public static string SerializeRequest(GrantRevocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.Serialize(request, JsonOptions);
    }
}
