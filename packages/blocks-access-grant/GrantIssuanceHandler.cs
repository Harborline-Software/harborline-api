using System.Text.Json;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Blocks.AccessGrant;

public static class GrantIssuanceSteps
{
    public const string DefinitionKey = "access.privileged-grant-review";
    public const string Decide = "decide";
    public const string Approve = "approve";
    public const string Granted = "granted";
    public const string Rejected = "rejected";
}

public interface IGrantIssuanceContext
{
    WorkflowEffect BuildGrantWriteEffect(AccessGrant grant, WorkflowStepKey stepKey);
}

/// <summary>Coverage-checks role issuance against current scoped atoms and resolves the role before writing.</summary>
public sealed class GrantIssuanceHandler(
    IGrantIssuanceContext context,
    IRoleVocabularyReader roles,
    IAuthorizationClosureReader closure) : IWorkflowStepHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string DefinitionKey => GrantIssuanceSteps.DefinitionKey;

    public async ValueTask<WorkflowStepOutcome> DecideAsync(
        WorkflowInstanceRecord instance, WorkflowTrigger trigger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var request = Deserialize(instance.StateJson);
        var at = trigger.At;
        return trigger.Step switch
        {
            GrantIssuanceSteps.Decide => await DecideRequestAsync(request, at, ct).ConfigureAwait(false),
            GrantIssuanceSteps.Approve => await ResolveHumanActionAsync(instance, request, trigger, at, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown grant-issuance step '{trigger.Step}'."),
        };
    }

    private async ValueTask<WorkflowStepOutcome> DecideRequestAsync(
        GrantIssuanceRequest request, DateTimeOffset at, CancellationToken ct)
    {
        if (!await IsCoveredAsync(request, at, ct).ConfigureAwait(false)) return Refused();
        return WorkflowStepOutcome.Park(GrantIssuanceSteps.Approve, BuildBasisPayload(request));
    }

    private async ValueTask<bool> IsCoveredAsync(
        GrantIssuanceRequest request, DateTimeOffset at, CancellationToken ct)
    {
        if (await roles.ResolveAsync(request.Role, ct).ConfigureAwait(false) is null) return false;
        var tenant = TenantId.FromString(request.TenantId);
        var requested = await closure.RolePermissionsAsync(tenant, request.Role, ct).ConfigureAwait(false);
        var narrowedAtoms = requested.Atoms
            .Select(atom => (atom.Operation, Scope: atom.Scope.Intersect(request.ScopeExpression)))
            .Where(item => item.Scope is not null)
            .Select(item => new PermissionAtom(item.Operation, item.Scope!))
            .ToArray();
        var narrowed = PermissionAtomSet.From(narrowedAtoms);
        if (narrowed.Atoms.Count != requested.Atoms.Count) return false;
        var held = await closure.UserPermissionsAsync(tenant, new ActorId(request.GranterId), at, ct)
            .ConfigureAwait(false);
        return held.Covers(narrowed);
    }

    private async ValueTask<WorkflowStepOutcome> ResolveHumanActionAsync(
        WorkflowInstanceRecord instance,
        GrantIssuanceRequest request,
        WorkflowTrigger trigger,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var action = HumanApprovalHandlerBase.ReadHumanAction(trigger.PayloadJson, GrantIssuanceSteps.Approve);
        if (action == "reject") return Refused();
        if (action != "approve") throw new InvalidOperationException($"Unknown grant-issuance action '{action}'.");
        if (!await IsCoveredAsync(request, at, ct).ConfigureAwait(false)) return Refused();
        var grant = new AccessGrant(
            new GrantId(request.GrantId), TenantId.FromString(request.TenantId), new ActorId(request.PrincipalId),
            request.Role, request.ScopeExpression, request.Residency,
            new GrantValidity(request.ValidFrom, request.ValidUntil), request.GranterKind,
            new ActorId(request.GranterId), at,
            request.Grant, request.LastReviewedAt);
        var key = new WorkflowStepKey(instance.Id, instance.Iteration, GrantIssuanceSteps.Granted);
        var effect = context.BuildGrantWriteEffect(grant, key);
        var json = JsonSerializer.Serialize(new { decision = "approved", granted = true, grantId = request.GrantId }, JsonOptions);
        return WorkflowStepOutcome.Complete(GrantIssuanceSteps.Granted, effect, json, json);
    }

    private static WorkflowStepOutcome Refused() =>
        WorkflowStepOutcome.Complete(GrantIssuanceSteps.Rejected, null,
            "{\"decision\":\"rejected\",\"granted\":false}",
            "{\"decision\":\"rejected\",\"granted\":false}");

    private static string BuildBasisPayload(GrantIssuanceRequest request) => JsonSerializer.Serialize(new
    {
        kind = "grant-issuance-basis", step = GrantIssuanceSteps.Approve, request.GrantId,
        request.TenantId, request.PrincipalId, request.Role, request.GranterId,
        scope = request.ScopeExpression.Value, request.ValidFrom, request.ValidUntil,
        request.Residency, typedOutcomes = new[] { "approve", "reject" },
    }, JsonOptions);

    private static GrantIssuanceRequest Deserialize(string stateJson) =>
        JsonSerializer.Deserialize<GrantIssuanceRequest>(stateJson, JsonOptions)
        ?? throw new InvalidOperationException("A grant-issuance request is required.");

    public static string SerializeRequest(GrantIssuanceRequest request) =>
        JsonSerializer.Serialize(request, JsonOptions);
}
