using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Forms.Submission;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Xunit;
using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed partial class AccessAdministrationPreloadTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Access_projection_refuses_an_invalid_defaulted_interval_before_creating_a_workflow(int endOffsetMinutes)
    {
        var at = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var store = Substitute.For<IWorkflowStore>();
        var dispatcher = Substitute.For<IWorkflowTriggerDispatcher>();
        var projection = new AccessGrantFormSubmissionProjection(
            new NodeWorkflowInstantiationService(store, Substitute.For<IDbContextFactory<LocalNodeDbContext>>()), dispatcher, store,
            Substitute.For<IEntityStore>(), Substitute.For<IWorkflowDefinitionStore>());
        using var values = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            person = "recipient", role = "member", scope = "/records", residency = "cache", reason = "manual",
            effectiveTo = at.AddMinutes(endOffsetMinutes).ToString("O"),
        }));
        var context = new FormSubmitContext(new FormDefinitionId("access.grant-a-role"),
            EntityId.Parse("forminst:forms/d4e5853b33c3fa3c1f5fef18c00f0c62"), Tenant, new ActorId("operator"), at, values);
        var refusal = await Assert.ThrowsAsync<ArgumentException>(() => projection.ProjectAsync(context));
        Assert.Equal("validTo", refusal.ParamName);
        await store.DidNotReceiveWithAnyArgs().CreateInstanceAsync(default!, default, default);
        await dispatcher.DidNotReceiveWithAnyArgs().DispatchAsync(default!, default(CancellationToken));
    }

    [Fact]
    public Task T433_predeclared_key_drives_real_engine_instance_and_workflow_grant_with_idempotent_replay() =>
        AssertPredeclaredSubmissionAsync(false, "1.0.3");

    [Fact]
    public Task T433_predeclared_key_on_the_released_predecessor_pins_its_admitted_workflow_revision() =>
        AssertPredeclaredSubmissionAsync(true, "1.0.1");

    [Fact]
    public Task T433_recovery_after_pack_upgrade_does_not_rebind_the_submitted_predecessor_form() =>
        AssertPredeclaredSubmissionAsync(true, "1.0.1", deferUntilUpgrade: true);

    private async Task AssertPredeclaredSubmissionAsync(
        bool releasedPredecessor, string expectedWorkflowVersion, bool deferUntilUpgrade = false)
    {
        var tenant = new TenantId("43300000-0000-4000-8000-000000000000");
        var actor = new ActorId("m6-t433-admin");
        var at = DateTimeOffset.UtcNow;
        var expectedInstance = "forminst:forms/d4e5853b33c3fa3c1f5fef18c00f0c62";
        var expectedGrant = new GrantId(Guid.Parse("2f6717a4-88a6-0d55-0688-96f359e06dea"));
        await _platformPreload.PreloadAsync(tenant, CancellationToken.None);
        if (releasedPredecessor) await InstallReleased113Async(tenant);
        else await _preload.PreloadAsync(tenant, CancellationToken.None);

        var (grants, configuration) = TestInMemoryAuthorizationStores.Pair();
        var definitions = new AuthorizationDefinitionWriter(configuration, configuration,
            new AuthorizationDefinitionAdmission(_roles), new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(), grants);
        await new AccessGrantAuthorizationSeed(definitions, configuration, grants)
            .InstallAsync(tenant, at, AuthorizationSeedProfile.Production, TestDesktopOperator.Actor);
        await grants.AppendAsync(tenant, new AccessGrant(new GrantId(Guid.NewGuid()), tenant, actor,
            RoleReference.Administrator, ScopeExpression.Parse("/"), GrantResidency.Cache,
            new GrantValidity(at.AddDays(-1), null), GranterKind.Person, actor, at,
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason("manual"), actor), at));

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, WorkflowEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, FormSubmitOutboxEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(options => options.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var database = await factory.CreateDbContextAsync()) await database.Database.EnsureCreatedAsync();
        var workflowStore = new NodeEfWorkflowStore(factory);
        var dispatcher = new WorkflowTriggerDispatcher(workflowStore,
            [new GrantIssuanceHandler(new NodeGrantIssuanceContext(grants), _roles,
                new DefinitionJoinedAuthorizationReader(grants, configuration))]);
        var projection = new AccessGrantFormSubmissionProjection(
            new NodeWorkflowInstantiationService(workflowStore, factory,
                _app.Services.GetRequiredService<IWorkflowDefinitionExecutionStore>()), dispatcher, workflowStore,
            _app.Services.GetRequiredService<IEntityStore>(), _workflows);
        var engine = new ProjectingFormEngine(_app.Services.GetRequiredService<IFormEngine>(),
            new FormSubmitProjectionRunner([projection]));
        var form = new FormDefinitionId("access.grant-a-role");
        var bearer = await _app.Services.GetRequiredService<IFormCapabilityIssuer>().IssueAsync(tenant, actor,
            projection.CapabilityRoles(form), [FormCapabilityAction.Write], at.AddMinutes(5));
        var token = await _app.Services.GetRequiredService<IFormCapabilityVerifier>().VerifyAsync(bearer, at);
        using var candidate = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            person = "m6-t433-holder", role = "member", scope = "/records", residency = "cache",
            effectiveFrom = at.AddMinutes(-1).ToString("O"), effectiveTo = "", reason = "manual",
        }));
        var authority = new AuthorizationWriteContext(actor, tenant, at);
        if (deferUntilUpgrade)
        {
            // Commit the real submission and its immutable binding before its workflow projection runs.
            var committed = await _app.Services.GetRequiredService<IFormEngine>().SaveWithReceiptAsync(
                form, candidate, token, authority, idempotencyKey: "m6-t433-grant-submit-v1");
            var entity = await _app.Services.GetRequiredService<IEntityStore>().GetAsync(committed.InstanceId);
            Assert.Equal("1.0.1", entity!.Binding!.DefinitionVersion);
            var outbox = new NodeEfFormSubmitOutbox(factory);
            await outbox.EnqueueAsync(new FormSubmitContext(form, committed.InstanceId, tenant, actor,
                committed.SubmittedAt, candidate));
            await _preload.PreloadAsync(tenant, CancellationToken.None);
            Assert.Equal("1.0.3", (await _workflows.GetCurrentPublishedAsync(
                new Harborline.Api.Foundation.Definitions.DefinitionAddress(tenant, GrantIssuanceSteps.DefinitionKey)))!.Version);
            var stored = Assert.Single(await new NodeEfFormSubmitOutbox(factory).ListUnresolvedAsync());
            var recovered = stored.RebuildContext();
            using (recovered.SubmittedValues)
            {
                // Upgrade withdraws 1.0.1. Existing execution admission must refuse it rather than
                // silently create the current 1.0.3 workflow for a submitted 1.0.1 form.
                var refusal = await Assert.ThrowsAsync<WorkflowDefinitionNotFoundException>(
                    () => projection.ProjectAsync(recovered));
                Assert.Equal("1.0.1", refusal.Version);
                Assert.Equal(tenant.Value, refusal.Tenant);
            }
            Assert.Null(await workflowStore.LoadAsync("access-grant-form:" + expectedInstance));
            Assert.Empty(await grants.FindByPrincipalAsync(tenant, new ActorId("m6-t433-holder")));
            return;
        }
        var first = await engine.SaveWithReceiptAsync(form, candidate, token, authority,
            idempotencyKey: "m6-t433-grant-submit-v1");
        // An already created predecessor instance keeps its pin after the pack upgrades.
        if (releasedPredecessor) await _preload.PreloadAsync(tenant, CancellationToken.None);
        var replay = await engine.SaveWithReceiptAsync(form, candidate, token, authority,
            idempotencyKey: "m6-t433-grant-submit-v1");
        Assert.Equal(expectedInstance, first.InstanceId.ToString());
        Assert.Equal(first, replay);
        var grant = Assert.Single(await grants.FindByPrincipalAsync(tenant, new ActorId("m6-t433-holder")));
        Assert.Equal(expectedGrant, grant.GrantId);
        Assert.Equal(new RoleReference(RoleVocabularies.Domain, "member"), grant.Role);
        Assert.Equal("/records", grant.Scope.Value);
        var workflow = await workflowStore.LoadAsync("access-grant-form:" + expectedInstance);
        Assert.NotNull(workflow);
        // Oracle: released 1.1.3 carries workflow 1.0.1; the current 1.1.6 carries 1.0.3.
        Assert.Equal(expectedWorkflowVersion, workflow.DefinitionVersion);
        Assert.Equal(WorkflowStatus.Completed, workflow.Status);
        var result = await workflowStore.FindStepResultAsync(new WorkflowStepKey(workflow.Id, 0, GrantIssuanceSteps.Approve));
        Assert.NotNull(result);
        using var outcome = JsonDocument.Parse(result.ResultJson);
        Assert.Equal(expectedGrant.Value, outcome.RootElement.GetProperty("grantId").GetGuid());
        var persistedResult = await projection.ReadResultAsync(form, tenant, first.InstanceId);
        Assert.Equal(expectedGrant.Value, persistedResult!.Value.GetProperty("outcome").GetProperty("grantId").GetGuid());
        Assert.Equal(persistedResult.Value.GetRawText(),
            (await projection.ReadResultAsync(form, tenant, replay.InstanceId))!.Value.GetRawText());
        Assert.Null(await projection.ReadResultAsync(form, new TenantId("other-tenant"), first.InstanceId));
    }
}
