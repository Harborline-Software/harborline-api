using System.Reflection;
using System.Text.Json;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.Foundation.Forms
{
internal static class FormDefinitionStoreTestMutationExtensions
{
    internal static ValueTask<FormDefinition> RegisterAsync(
        this IFormDefinitionStore store, FormDefinition definition, CancellationToken ct = default) =>
        Writer(store).RegisterAsync(definition, ct);

    internal static ValueTask<FormDefinition> PublishAsync(
        this IFormDefinitionStore store, DefinitionCoordinates coordinates, CancellationToken ct = default) =>
        Writer(store).PublishAsync(coordinates, ct);

    internal static ValueTask<FormDefinition> WithdrawAsync(
        this IFormDefinitionStore store, DefinitionCoordinates coordinates, CancellationToken ct = default) =>
        Writer(store).WithdrawAsync(coordinates, ct);

    internal static ValueTask<FormDefinition> RestorePackProjectionAsync(
        this IFormDefinitionStore store, DefinitionCoordinates coordinates, CancellationToken ct = default) =>
        Writer(store).RestorePackProjectionAsync(coordinates, ct);

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
    internal static ValueTask<WorkflowDefinitionRecord> RegisterAsync(
        this IWorkflowDefinitionStore store,
        WorkflowDefinition model,
        JsonElement authored,
        WorkflowDefinitionRegistrationOptions? options = null,
        CancellationToken ct = default) => Writer(store).RegisterAsync(model, authored, options, ct);

    internal static ValueTask<WorkflowDefinitionRecord> PublishAsync(
        this IWorkflowDefinitionStore store, DefinitionCoordinates coordinates, CancellationToken ct = default) =>
        Writer(store).PublishAsync(coordinates, ct);

    internal static ValueTask<WorkflowDefinitionRecord> WithdrawAsync(
        this IWorkflowDefinitionStore store, DefinitionCoordinates coordinates, CancellationToken ct = default) =>
        Writer(store).WithdrawAsync(coordinates, ct);

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
