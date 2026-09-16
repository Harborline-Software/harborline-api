using System.Text.Json;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.Blocks.AccessGrant;
using Xunit;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed partial class AccessAdministrationPreloadTests
{
    [Theory]
    [InlineData("refusal")]
    [InlineData("throw")]
    [InlineData("cancel")]
    public async Task Mixed_kind_late_failure_preserves_every_published_snapshot(string failure)
    {
        await PreloadPlatformThenAccessAsync();
        var replacement = MixedReplacement("1.1.2", failure == "refusal");
        var context = ReplacementContext();
        Assert.True(_installer.Install(await ExportAsync(replacement), context).Installed);
        var before = await PublishedSnapshotAsync();
        _beforeViewAdmission = (definition, _) =>
        {
            if (definition.Key != "m6.late") return ValueTask.CompletedTask;
            return failure switch
            {
                "throw" => throw new IOException("late projection failure"),
                "cancel" => throw new OperationCanceledException("late projection cancellation"),
                _ => ValueTask.CompletedTask,
            };
        };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (failure == "cancel")
                Assert.Throws<OperationCanceledException>(() => _installer.Activate(context, replacement.Key, replacement.Version));
            else
            {
                var refused = _installer.Activate(context, replacement.Key, replacement.Version);
                Assert.False(refused.Activated);
                Assert.False(refused.Projected);
            }
            Assert.Equal(before, await PublishedSnapshotAsync());
            Assert.Empty(((IPackProjectionAdmissionStore)_store).ListIncompleteProjectionAdmissions());
            Assert.Equal(PackLifecycleState.Draft, _store.GetVersion(Tenant, replacement.Key, replacement.Version)!.Lifecycle);
            Assert.Null(_renderPlans.Get(Tenant, PackContentKind.FormDefinition, "m6.form", "1.0.0"));
            Assert.Null(_renderPlans.Get(Tenant, PackContentKind.ViewDefinition, "m6.early-view", "1.0.0"));
        }
        if (failure == "refusal") return; // Signed invalid bytes cannot be edited on retry.
        _beforeViewAdmission = null;
        var retry = _installer.Activate(context, replacement.Key, replacement.Version);
        Assert.True(retry.Activated, JsonSerializer.Serialize(retry.ProjectionResult) + retry.Detail);
        var after = await PublishedSnapshotAsync();
        Assert.NotEqual(before, after);
        Assert.NotNull(await _forms.GetCurrentPublishedAsync(new(Tenant, "m6.form")));
        Assert.NotNull(await _workflows.GetCurrentPublishedAsync(new(Tenant, "m6.workflow")));
        Assert.NotNull(await _views.GetDefinitionAsync(Tenant.Value, "m6.early-view", "1.0.0"));
        Assert.NotNull(_renderPlans.Get(Tenant, PackContentKind.FormDefinition, "m6.form", "1.0.0"));
        Assert.True(_installer.Activate(context, replacement.Key, replacement.Version).Activated);
        Assert.Equal(after, await PublishedSnapshotAsync()); // Exact replay creates no tuple or lifecycle revision.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_catalogue_reader_sees_only_old_or_complete_new_replacement(bool refuse)
    {
        await PreloadPlatformThenAccessAsync();
        var replacement = MixedReplacement("1.1.2", refuse);
        var context = ReplacementContext();
        Assert.True(_installer.Install(await ExportAsync(replacement), context).Installed);
        var catalogue = new ProjectedCatalogue(_authorizedForms, _views, _renderPlans);
        var before = JsonSerializer.Serialize(await catalogue.ListAsync(Tenant));
        var reachedLate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeViewAdmission = async (definition, _) =>
        {
            if (definition.Key != "m6.late") return;
            reachedLate.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
        };
        var activation = Task.Run(() => _installer.Activate(context, replacement.Key, replacement.Version));
        await reachedLate.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(async () =>
        {
            reading.SetResult();
            return JsonSerializer.Serialize(await catalogue.ListAsync(Tenant));
        });
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(reader.IsCompleted);
        }
        finally { release.SetResult(); }
        var outcome = await activation.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(outcome.Activated == !refuse, JsonSerializer.Serialize(outcome.ProjectionResult) + outcome.Detail);
        var observed = await reader.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(JsonSerializer.Serialize(await catalogue.ListAsync(Tenant)), observed);
        if (refuse) Assert.Equal(before, observed);
        else
        {
            Assert.NotEqual(before, observed);
            Assert.Contains("m6.form", observed);
            Assert.Contains("m6.early-view", observed);
            Assert.Contains("m6.late", observed);
            Assert.DoesNotContain("\"renderPlan\":null", observed, StringComparison.OrdinalIgnoreCase);
        }
    }

    private PackInstallContext ReplacementContext() => new(Tenant, TrustingTheNodeKey(), PackRevocationList.Empty,
        DateTimeOffset.UtcNow, PackInstallRoutes.RevocationMaxAge,
        Principal: AccessGrantAuthorizationSeed.NodeOperatorPrincipal);

    private PackExportRequest MixedReplacement(string version, bool refuse)
    {
        var source = AccessAdministrationPreloadHostedService.ReadExportRequest(_signer.Signer.IssuerId.ToBase64Url());
        var holder = source.Contents.Single(item => item.Key == "access.holders");
        var early = holder.Content.DeepClone();
        early["key"] = "m6.early-view";
        var late = holder.Content.DeepClone();
        late["key"] = "m6.late";
        if (refuse) late["viewKind"] = "views.not-registered";
        var workflow = source.Contents.Single(item => item.Kind == PackContentKind.WorkflowDefinition).Content.DeepClone();
        workflow["key"] = "m6.workflow";
        workflow["version"] = "1.0.0";
        workflow["subjectFormRef"]!["formId"] = "m6.form";
        workflow["subjectFormRef"]!["version"] = "1.0.0";
        workflow["postSubmitProjection"] = "m6.workflow";
        return source with
        {
            Version = version,
            Contents = source.Contents.Concat([
                new PackContentSource("m6.asset", PackContentKind.AssetTypeDefinition, "1.0.0",
                    JsonSerializer.SerializeToNode(new { id = "m6.asset", displayName = "Atomic asset", traits = new[] { "Maintainable" } })!),
                new PackContentSource("m6.form", PackContentKind.FormDefinition, "1.0.0",
                    JsonSerializer.SerializeToNode(PackProjectionTestFixture.FormContent("Atomic form"))!),
                new PackContentSource("m6.workflow", PackContentKind.WorkflowDefinition, "1.0.0", workflow),
                new PackContentSource("m6.role", PackContentKind.RoleDefinition, "1.0.0",
                    JsonSerializer.SerializeToNode(new { role = "atomic-reader", displayName = "Atomic reader" })!),
                new PackContentSource("m6.binding", PackContentKind.AuthorizationCapabilityBinding, "1.0.0",
                    JsonSerializer.SerializeToNode(new { operation = "records:read", scope = "/records/m6", offeredRoles = new[] { "sys.platform-roles/administrator" } })!),
                new PackContentSource("m6.early-view", holder.Kind, holder.Version, early),
                new PackContentSource("m6.late", holder.Kind, holder.Version, late),
            ]).ToArray(),
        };
    }

    private async Task<string> PublishedSnapshotAsync()
    {
        using var lease = PackProjectionActivationBarrier.Read();
        var forms = new List<FormDefinition>();
        await foreach (var form in _forms.ListByTenantAsync(Tenant)) forms.Add(form);
        var workflows = new List<Harborline.Api.Blocks.Workflow.Durable.WorkflowDefinitionRecord>();
        await foreach (var workflow in _workflows.ListByTenantAsync(Tenant)) workflows.Add(workflow);
        var views = await _views.ListDefinitionsAsync(Tenant.Value);
        return JsonSerializer.Serialize(new
        {
            active = _store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey),
            forms, workflows, views, defaults = _defaults.List(Tenant),
            configuration = await _configuration.ListAsync(Tenant),
            types = _app.Services.GetRequiredService<IEntityTypeRegistry>().ListSeeds(),
            plans = forms.Select(form => _renderPlans.Get(Tenant, PackContentKind.FormDefinition, form.Id.Value, form.Version.ToString()))
                .Concat(views.Select(view => _renderPlans.Get(Tenant, PackContentKind.ViewDefinition, view.Key, view.Version))).ToArray(),
        });
    }

    [Fact]
    public async Task Forbidden_auditor_offer_refuses_activation_without_changing_platform()
    {
        await _platformPreload.PreloadAsync(Tenant, CancellationToken.None);
        var prior = _store.GetActive(Tenant, PlatformPackPreloadHostedService.PackKey)!;
        var viewsBefore = JsonSerializer.Serialize(await _views.ListDefinitionsAsync(Tenant.Value));
        var source = PlatformPackPreloadHostedService.ReadExportRequest(_signer.Signer.IssuerId.ToBase64Url());
        var replacement = source with
        {
            Version = "1.4.1",
            Contents = source.Contents.Append(new PackContentSource(
                "platform.binding.forbidden-auditor", PackContentKind.AuthorizationCapabilityBinding, "1.0.0",
                JsonSerializer.SerializeToNode(new
                {
                    operation = "audit:read", scope = "/", offeredRoles = new[] { "platform/auditor" },
                })!)).ToArray(),
        };
        var context = new PackInstallContext(Tenant, TrustingTheNodeKey(), PackRevocationList.Empty,
            DateTimeOffset.UtcNow, PackInstallRoutes.RevocationMaxAge,
            Principal: AccessGrantAuthorizationSeed.NodeOperatorPrincipal);
        Assert.True(_installer.Install(await ExportAsync(replacement), context).Installed);
        var activation = _installer.Activate(context, replacement.Key, replacement.Version);
        Assert.False(activation.Activated);
        Assert.Equal(PackSeedProjector.AccessProjectionRefusedCode, activation.Refusal!.Code);
        Assert.Equal(prior, _store.GetActive(Tenant, replacement.Key));
        Assert.Equal(viewsBefore, JsonSerializer.Serialize(await _views.ListDefinitionsAsync(Tenant.Value)));
        Assert.Equal(PackLifecycleState.Draft, _store.GetVersion(Tenant, replacement.Key, replacement.Version)!.Lifecycle);
    }

    [Fact]
    public async Task Replacement_late_view_refusal_keeps_old_active_catalogue_and_publishes_no_early_view()
    {
        await PreloadPlatformThenAccessAsync();
        var prior = _store.GetActive(Tenant, AccessAdministrationPreloadHostedService.PackKey)!;
        var viewsBefore = JsonSerializer.Serialize(await _views.ListDefinitionsAsync(Tenant.Value, CancellationToken.None));
        var source = AccessAdministrationPreloadHostedService.ReadExportRequest(_signer.Signer.IssuerId.ToBase64Url());
        var holder = source.Contents.Single(item => item.Key == "access.holders");
        var early = holder.Content.DeepClone();
        early["key"] = "m6.early-view";
        var late = holder.Content.DeepClone();
        late["key"] = "m6.late-refusal";
        late["viewKind"] = "views.not-registered";
        var replacement = source with
        {
            Version = "1.1.2-atomicity-probe.0",
            Contents = source.Contents.Concat([
                new PackContentSource("m6.early-view", holder.Kind, holder.Version, early),
                new PackContentSource("m6.late-refusal", holder.Kind, holder.Version, late),
            ]).ToArray(),
        };
        var bytes = await ExportAsync(replacement);
        var context = new PackInstallContext(Tenant, TrustingTheNodeKey(), PackRevocationList.Empty,
            DateTimeOffset.UtcNow, PackInstallRoutes.RevocationMaxAge,
            Principal: AccessGrantAuthorizationSeed.NodeOperatorPrincipal);
        Assert.True(_installer.Install(bytes, context).Installed);
        var activation = _installer.Activate(context, replacement.Key, replacement.Version);
        Assert.False(activation.Activated);
        Assert.False(activation.Projected);
        Assert.Equal(PackInstallCodes.ActivateProjectionRefused, activation.Error);
        Assert.Equal("pack.view-definition.malformed", activation.Refusal!.Code);
        var result = Assert.IsType<PackSeedProjectionSummary>(activation.ProjectionResult);
        Assert.Contains(result.Refusals, refusal => refusal.ContentKey == "m6.late-refusal"
            && refusal.Code == "pack.view-definition.malformed");
        Assert.Equal(viewsBefore, JsonSerializer.Serialize(await _views.ListDefinitionsAsync(Tenant.Value, CancellationToken.None)));
        Assert.Equal(prior, _store.GetActive(Tenant, replacement.Key));
        Assert.Null(await _views.GetDefinitionAsync(Tenant.Value, "m6.early-view", "1.0.0", CancellationToken.None));
        Assert.NotEqual(PackLifecycleState.Active, _store.GetVersion(Tenant, replacement.Key, replacement.Version)!.Lifecycle);
    }
}
