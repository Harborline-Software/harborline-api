using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Forms.Submission;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.Workflow;

/// <summary>Projects the Access pack's submitted grant form through its typed review workflow.</summary>
internal sealed class AccessGrantFormSubmissionProjection(
    NodeWorkflowInstantiationService instances,
    IWorkflowTriggerDispatcher dispatcher,
    IWorkflowStore workflowStore,
    IEntityStore entities,
    IWorkflowDefinitionStore workflowCatalog) : IFormSubmitProjection, IFormSubmissionGate, IFormSubmissionResultReader
{
    private const string FormId = "access.grant-a-role";
    // A section's role tokens are the QUALIFIED reference (DeclarativeGateReference.ParseRole refuses a
    // bare name), and the minted capability token is matched against them verbatim — so both spell it the
    // same way as the pack's RoleDefinition: RoleVocabularies.Domain + "/" + the declared name.
    private const string SubmitterRole = RoleVocabularies.Domain + "/access-form-submitter";

    public string? RequiredPermission(FormDefinitionId form) =>
        form.Value == FormId ? TeamRolePermissions.MembersManage : null;

    /// <summary>The grant's requested start, <c>effectiveFrom</c>, is the field K3 refuses in the past without a backdate capability.</summary>
    public string? EffectiveFromField(FormDefinitionId form) => form.Value == FormId ? "effectiveFrom" : null;
    public string? EffectiveToField(FormDefinitionId form) => form.Value == FormId ? "effectiveTo" : null;

    public IReadOnlyList<string> CapabilityRoles(FormDefinitionId form) =>
        form.Value == FormId ? [SubmitterRole] : [];

    public async ValueTask<JsonElement?> ReadResultAsync(FormDefinitionId form, TenantId tenant, EntityId instance,
        CancellationToken cancellationToken = default)
    {
        if (form.Value != FormId) return null;
        var workflow = await workflowStore.LoadAsync(
            NodeWorkflowInstantiationService.AccessGrantInstanceId(instance.ToString()), cancellationToken).ConfigureAwait(false);
        if (workflow is null || workflow.TenantId != tenant.Value) return null;
        var committed = await workflowStore.FindStepResultAsync(
            new WorkflowStepKey(workflow.Id, 0, GrantIssuanceSteps.Approve), cancellationToken).ConfigureAwait(false);
        if (committed is null) return null;
        using var result = JsonDocument.Parse(committed.ResultJson);
        return JsonSerializer.SerializeToElement(new
        {
            workflowInstanceId = workflow.Id, definitionKey = workflow.DefinitionKey,
            definitionVersion = workflow.DefinitionVersion, outcome = result.RootElement,
        });
    }

    public async Task<IReadOnlyList<FormSubmitProjectionSkip>> ProjectAsync(
        FormSubmitContext context, CancellationToken cancellationToken = default)
    {
        if (context.Form.Value != FormId) return [];
        var request = ReadRequest(context);
        var existing = await workflowStore.LoadAsync(
            NodeWorkflowInstantiationService.AccessGrantInstanceId(context.InstanceId.ToString()), cancellationToken).ConfigureAwait(false);
        var workflowVersion = existing?.DefinitionVersion
            ?? await ResolveSubmittedWorkflowVersionAsync(context, cancellationToken).ConfigureAwait(false);
        var instanceId = await instances.StartAccessGrantIssuanceAsync(
            context.Tenant, context.InstanceId.ToString(), request, workflowVersion, context.SubmittedAt, cancellationToken)
            .ConfigureAwait(false);

        await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, instanceId, GrantIssuanceSteps.Approve,
                context.SubmittedAt, "{\"decision\":\"approve\"}"),
            cancellationToken).ConfigureAwait(false);
        return [];
    }

    private async Task<string> ResolveSubmittedWorkflowVersionAsync(FormSubmitContext context, CancellationToken ct)
    {
        var submission = await entities.GetAsync(context.InstanceId, default, ct).ConfigureAwait(false);
        if (submission is null || submission.Tenant != context.Tenant || submission.DeletedAt is not null
            || submission.Binding is not { DefinitionId: "access.grant-a-role" } binding
            || binding.SubmittedAt != context.SubmittedAt)
            throw new InvalidOperationException("Access grant issuance requires its persisted tenant-scoped submission binding.");

        // The immutable submitted form revision, not recovery's current pointer, chooses the pairing.
        // The catalog identifies metadata only; execution still traverses the exact re-admitting read.
        WorkflowDefinitionRecord? paired = null;
        await foreach (var candidate in workflowCatalog.ListByTenantAsync(context.Tenant, ct).ConfigureAwait(false))
        {
            if (candidate.Key != GrantIssuanceSteps.DefinitionKey
                || !candidate.Authored.TryGetProperty("subjectFormRef", out var subject)
                || subject.ValueKind != JsonValueKind.Object
                || !subject.TryGetProperty("formId", out var formId) || formId.GetString() != binding.DefinitionId
                || !subject.TryGetProperty("version", out var formVersion) || formVersion.GetString() != binding.DefinitionVersion)
                continue;
            if (paired is not null)
                throw new InvalidOperationException("The submitted Access form revision has ambiguous workflow pairing metadata.");
            paired = candidate;
        }
        if (paired is null)
            throw new InvalidOperationException("No workflow is paired with the submitted Access form revision.");
        return paired.Version;
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
        // L940 (T-1017): an omitted effectiveFrom starts the grant at the admitted instant on the server clock.
        var validFrom = Optional(values, "effectiveFrom") is { } from
            ? DateTimeOffset.Parse(from, System.Globalization.CultureInfo.InvariantCulture) : context.SubmittedAt;
        DateTimeOffset? validUntil = values.TryGetProperty("effectiveTo", out var until) && until.GetString() is { Length: > 0 } text
            ? DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture) : null;
        // Validate before creating a durable process, including replay outside the HTTP guard.
        _ = new GrantValidity(validFrom, validUntil);
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
        Optional(values, name) ?? throw new InvalidOperationException($"Access grant form requires '{name}'.");

    /// <summary>The field's string value, or null when it is absent, JSON null or blank.</summary>
    private static string? Optional(JsonElement values, string name) =>
        values.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString() : null;
}

/// <summary>Builds an idempotent durable grant write for the typed handler's completed step.</summary>
internal sealed class NodeGrantIssuanceContext(IGrantStore grants) : IGrantIssuanceContext
{
    public WorkflowEffect BuildGrantWriteEffect(AccessGrant grant, WorkflowStepKey stepKey) => new(
        (_, ct) => grants.AppendAsync(grant.TenantId, grant, "workflow:" + stepKey.Value, ct), commitsIndependently: true);
}
