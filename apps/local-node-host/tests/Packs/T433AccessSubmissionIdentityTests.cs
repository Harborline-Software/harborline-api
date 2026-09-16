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
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed partial class AccessAdministrationPreloadTests
{
    [Fact]
    public async Task T433_predeclared_key_drives_real_engine_instance_and_workflow_grant_with_idempotent_replay()
    {
        var tenant = new TenantId("43300000-0000-4000-8000-000000000000");
        var actor = new ActorId("m6-t433-admin");
        var at = DateTimeOffset.UtcNow;
        var expectedInstance = "forminst:forms/d4e5853b33c3fa3c1f5fef18c00f0c62";
        var expectedGrant = new GrantId(Guid.Parse("2f6717a4-88a6-0d55-0688-96f359e06dea"));
        await _platformPreload.PreloadAsync(tenant, CancellationToken.None);
        await _preload.PreloadAsync(tenant, CancellationToken.None);

        var (grants, configuration) = TestInMemoryAuthorizationStores.Pair();
        var definitions = new AuthorizationDefinitionWriter(configuration, configuration,
            new AuthorizationDefinitionAdmission(_roles), new AuthorizationCapabilityBindingAdmission(),
            TestAuthorization.AllowGate(), grants);
        await new AccessGrantAuthorizationSeed(definitions, configuration, grants)
            .InstallAsync(tenant, at, AuthorizationSeedProfile.Production);
        await grants.AppendAsync(tenant, new AccessGrant(new GrantId(Guid.NewGuid()), tenant, actor,
            RoleReference.Administrator, ScopeExpression.Parse("/"), GrantResidency.Cache,
            new GrantValidity(at.AddDays(-1), null), GranterKind.Person, actor, at,
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason("manual"), actor), at));

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, WorkflowEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(options => options.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var database = await factory.CreateDbContextAsync()) await database.Database.EnsureCreatedAsync();
        var workflowStore = new NodeEfWorkflowStore(factory);
        var dispatcher = new WorkflowTriggerDispatcher(workflowStore,
            [new GrantIssuanceHandler(new NodeGrantIssuanceContext(grants), _roles,
                new DefinitionJoinedAuthorizationReader(grants, configuration))]);
        var projection = new AccessGrantFormSubmissionProjection(
            new NodeWorkflowInstantiationService(workflowStore, factory), dispatcher, workflowStore);
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
        var first = await engine.SaveWithReceiptAsync(form, candidate, token, authority,
            idempotencyKey: "m6-t433-grant-submit-v1");
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
