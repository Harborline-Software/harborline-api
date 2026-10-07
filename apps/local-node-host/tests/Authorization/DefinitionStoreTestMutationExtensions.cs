using System.Reflection;
using System.Text.Json;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.Foundation.Forms
{
internal static class FormDefinitionStoreTestMutationExtensions
{
    // ck-10 S3b: the writer runs each act through the executor under a carried admission. These seeding
    // helpers carry an allowed forms.author decision for the definition they write, and no façade checks.
    internal static ValueTask<FormDefinition> RegisterAsync(
        this IFormDefinitionStore store, FormDefinition definition, CancellationToken ct = default) =>
        Writer(store).RegisterAsync(
            Admission(definition.Tenant, definition.Id.Value), Target(definition.Tenant, definition.Id.Value),
            definition, NoFacadeChecks, ct);

    internal static ValueTask<FormDefinition> PublishAsync(
        this IFormDefinitionStore store, DefinitionCoordinates coordinates, CancellationToken ct = default) =>
        Transition(store, coordinates, DefinitionLifecycleTransition.Publish, ct);

    internal static ValueTask<FormDefinition> WithdrawAsync(
        this IFormDefinitionStore store, DefinitionCoordinates coordinates, CancellationToken ct = default) =>
        Transition(store, coordinates, DefinitionLifecycleTransition.Withdraw, ct);

    internal static ValueTask<FormDefinition> RestorePackProjectionAsync(
        this IFormDefinitionStore store, DefinitionCoordinates coordinates, CancellationToken ct = default) =>
        Transition(store, coordinates, DefinitionLifecycleTransition.Restore, ct);

    private static ValueTask<FormDefinition> Transition(
        IFormDefinitionStore store, DefinitionCoordinates coordinates, DefinitionLifecycleTransition transition,
        CancellationToken ct) =>
        Writer(store).TransitionAsync(
            Admission(coordinates.Address.Tenant, coordinates.Address.Identity.Value),
            Target(coordinates.Address.Tenant, coordinates.Address.Identity.Value),
            coordinates, transition, null, NoFacadeChecks, ct);

    private static ValueTask NoFacadeChecks(FormDefinition definition, CancellationToken ct) => ValueTask.CompletedTask;

    private static DefinitionWriteAdmission Admission(Harborline.Foundation.Assets.Common.TenantId tenant, string id) =>
        DefinitionWriteAdmission.Decide(TestAuthorization.AllowedDecision(
            tenant, Uri.EscapeDataString(id), "forms", Harborline.Api.Foundation.IdentityAtlas.Permissions.Permission.FormsAuthor));

    private static DefinitionWriteTarget Target(Harborline.Foundation.Assets.Common.TenantId tenant, string id) =>
        new(tenant, "forms", Harborline.Api.Foundation.IdentityAtlas.Permissions.Permission.FormsAuthor, id);

    private static AuthorizedFormDefinitionLifecycle.DefinitionWriter Writer(IFormDefinitionStore store)
    {
        var lifecycle = TestAuthorization.FormLifecycle(
            store, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        return Assert.IsType<AuthorizedFormDefinitionLifecycle.DefinitionWriter>(
            typeof(AuthorizedFormDefinitionLifecycle)
                .GetField("writer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(lifecycle));
    }
}
}

namespace Harborline.Api.Blocks.Workflow.Durable
{
internal static class WorkflowDefinitionStoreTestMutationExtensions
{
    // ck-10 S3b: as the form helpers, under an allowed scheduling.author decision for the workflow key.
    internal static ValueTask<WorkflowDefinitionRecord> RegisterAsync(
        this IWorkflowDefinitionStore store,
        WorkflowDefinition model,
        JsonElement authored,
        WorkflowDefinitionRegistrationOptions? options = null,
        CancellationToken ct = default) => Writer(store).RegisterAsync(
            Admission(new Harborline.Foundation.Assets.Common.TenantId(model.Tenant), model.Key),
            Target(new Harborline.Foundation.Assets.Common.TenantId(model.Tenant), model.Key),
            model, authored, options, static (_, _) => ValueTask.CompletedTask, ct);

    internal static ValueTask<WorkflowDefinitionRecord> PublishAsync(
        this IWorkflowDefinitionStore store, DefinitionCoordinates coordinates, CancellationToken ct = default) =>
        Transition(store, coordinates, DefinitionLifecycleTransition.Publish, ct);

    internal static ValueTask<WorkflowDefinitionRecord> WithdrawAsync(
        this IWorkflowDefinitionStore store, DefinitionCoordinates coordinates, CancellationToken ct = default) =>
        Transition(store, coordinates, DefinitionLifecycleTransition.Withdraw, ct);

    private static ValueTask<WorkflowDefinitionRecord> Transition(
        IWorkflowDefinitionStore store, DefinitionCoordinates coordinates, DefinitionLifecycleTransition transition,
        CancellationToken ct) =>
        Writer(store).TransitionAsync(
            Admission(coordinates.Address.Tenant, coordinates.Address.Identity.Value),
            Target(coordinates.Address.Tenant, coordinates.Address.Identity.Value),
            coordinates, transition, null, static (_, _) => ValueTask.CompletedTask, ct);

    private static DefinitionWriteAdmission Admission(Harborline.Foundation.Assets.Common.TenantId tenant, string key) =>
        DefinitionWriteAdmission.Decide(TestAuthorization.AllowedDecision(
            tenant, Uri.EscapeDataString(key), "scheduling", Harborline.Api.Foundation.IdentityAtlas.Permissions.Permission.SchedulingAuthor));

    private static DefinitionWriteTarget Target(Harborline.Foundation.Assets.Common.TenantId tenant, string key) =>
        new(tenant, "scheduling", Harborline.Api.Foundation.IdentityAtlas.Permissions.Permission.SchedulingAuthor, key);

    private static AuthorizedWorkflowDefinitionLifecycle.DefinitionWriter Writer(IWorkflowDefinitionStore store)
    {
        var lifecycle = TestAuthorization.WorkflowLifecycle(
            store, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        return Assert.IsType<AuthorizedWorkflowDefinitionLifecycle.DefinitionWriter>(
            typeof(AuthorizedWorkflowDefinitionLifecycle)
                .GetField("writer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(lifecycle));
    }
}
}
