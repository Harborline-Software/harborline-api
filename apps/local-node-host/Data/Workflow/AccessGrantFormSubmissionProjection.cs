using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Forms.Submission;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>Projects the Access pack's submitted grant form through its typed review workflow.</summary>
internal sealed class AccessGrantFormSubmissionProjection(
    IWorkflowStore workflows,
    IWorkflowTriggerDispatcher dispatcher) : IFormSubmitProjection, IFormSubmissionGate
{
    private const string FormId = "access.grant-a-role";
    private const string SubmitterRole = "access-form-submitter";

    public string? RequiredPermission(FormDefinitionId form) =>
        form.Value == FormId ? TeamRolePermissions.MembersManage : null;

    public IReadOnlyList<string> CapabilityRoles(FormDefinitionId form) =>
        form.Value == FormId ? [SubmitterRole] : [];

    public async Task<IReadOnlyList<FormSubmitProjectionSkip>> ProjectAsync(
        FormSubmitContext context, CancellationToken cancellationToken = default)
    {
        if (context.Form.Value != FormId) return [];
        var request = ReadRequest(context);
        var instanceId = "access-grant-form:" + context.InstanceId;
        if (await workflows.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false) is null)
        {
            await workflows.CreateInstanceAsync(new WorkflowInstanceRecord
            {
                Id = instanceId,
                TenantId = context.Tenant.Value,
                DefinitionKey = GrantIssuanceSteps.DefinitionKey,
                DefinitionVersion = "1.0.1",
                CurrentStep = GrantIssuanceSteps.Approve,
                Status = WorkflowStatus.Running,
                StateJson = GrantIssuanceHandler.SerializeRequest(request),
            }, context.SubmittedAt, cancellationToken).ConfigureAwait(false);
        }

        await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, instanceId, GrantIssuanceSteps.Approve,
                context.SubmittedAt, "{\"decision\":\"approve\"}"),
            cancellationToken).ConfigureAwait(false);
        return [];
    }

    private static GrantIssuanceRequest ReadRequest(FormSubmitContext context)
    {
        var values = context.SubmittedValues.RootElement;
        var reason = Required(values, "reason");
        var residency = Required(values, "residency") switch
        {
            "cache" => GrantResidency.Cache,
            "online-only" => GrantResidency.OnlineOnly,
            var value => Enum.Parse<GrantResidency>(value, ignoreCase: true),
        };
        var validFrom = DateTimeOffset.Parse(Required(values, "effectiveFrom"));
        DateTimeOffset? validUntil = values.TryGetProperty("effectiveTo", out var until) && until.GetString() is { Length: > 0 } text
            ? DateTimeOffset.Parse(text) : null;
        var roleName = Required(values, "role");
        var role = roleName == RoleReference.Administrator.Name
            ? RoleReference.Administrator : new RoleReference(RoleVocabularies.Domain, roleName);
        var grantId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(context.InstanceId.ToString()))[..16]);
        return new GrantIssuanceRequest(
            grantId, context.Tenant.Value, Required(values, "person"), role, context.Actor.Value,
            GranterKind.Person, ScopeExpression.Parse(Required(values, "scope")), residency, validFrom, validUntil,
            new GrantProvenance(Enum.Parse<GrantSourceKind>(reason, true), new GrantReason(reason, context.InstanceId.ToString()), context.Actor),
            context.SubmittedAt);
    }

    private static string Required(JsonElement values, string name) =>
        values.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidOperationException($"Access grant form requires '{name}'.");
}

/// <summary>Builds an idempotent durable grant write for the typed handler's completed step.</summary>
internal sealed class NodeGrantIssuanceContext(IGrantStore grants) : IGrantIssuanceContext
{
    public WorkflowEffect BuildGrantWriteEffect(AccessGrant grant, WorkflowStepKey stepKey) => new(
        (_, ct) => grants.AppendAsync(grant.TenantId, grant, "workflow:" + stepKey.Value, ct), commitsIndependently: true);
}
