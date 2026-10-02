using System.Reflection;
using System.Text.Json;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Definitions;

/// <summary>
/// ck-10 S3b (DES-0029, ADR 0038): form and workflow definition registration and lifecycle transitions run their
/// six stages through <see cref="WritePipeline.RunAsync"/> under one <see cref="DefinitionWriteAdmission"/>, which
/// the authorize stage compares with the definition the write touches.
/// </summary>
public sealed class DefinitionWritePipelineTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("definition-pipeline");
    private static readonly TenantId OtherTenant = new("definition-pipeline-other");
    private static readonly ActorId Operator = new("operator");

    // ADR 0038's order, written out here rather than read from WritePipeline.Order.
    private static readonly WritePipelineStage[] SixStages =
    [
        WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate,
        WritePipelineStage.Validate, WritePipelineStage.Commit, WritePipelineStage.React,
    ];

    private static readonly WritePipelineStage[] ThroughValidate =
    [
        WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate, WritePipelineStage.Validate,
    ];

    [Fact(DisplayName = "ck-10 S3b: a tenant form register, then its publish, each run the six stages")]
    public async Task Form_TenantRegisterThenPublish_RunTheSixStages()
    {
        var h = new FormHarness();

        await h.Lifecycle.RegisterAsync(Form("intake"), Context(Tenant));
        Assert.Equal(SixStages, h.TakeStages());
        Assert.Equal(FormDefinitionStatus.Draft, (await h.Store.GetAsync(Coordinates("intake"))).Status);

        await h.Lifecycle.PublishAsync(Coordinates("intake"), Context(Tenant));
        Assert.Equal(SixStages, h.TakeStages());
        Assert.Equal(FormDefinitionStatus.Published, (await h.Store.GetAsync(Coordinates("intake"))).Status);
    }

    [Fact(DisplayName = "ck-10 S3b: a tenant workflow register, then its publish, each run the six stages")]
    public async Task Workflow_TenantRegisterThenPublish_RunTheSixStages()
    {
        var h = new WorkflowHarness();
        using var authored = WorkflowAuthored("approval", Tenant);

        await h.Lifecycle.RegisterAsync(authored.RootElement, Context(Tenant));
        Assert.Equal(SixStages, h.TakeStages());
        Assert.Equal(WorkflowDefinitionStatus.Draft, (await h.Store.GetAsync(Coordinates("approval"))).Status);

        await h.Lifecycle.PublishAsync(Coordinates("approval"), Context(Tenant));
        Assert.Equal(SixStages, h.TakeStages());
        Assert.Equal(WorkflowDefinitionStatus.Published, (await h.Store.GetAsync(Coordinates("approval"))).Status);
    }

    [Fact(DisplayName = "ck-10 S3b: a decided form admission for another definition stops at authorize and stores nothing")]
    public async Task Form_DecidedForAnotherDefinition_StopsAtAuthorize()
    {
        var h = new FormHarness();
        var other = await h.Lifecycle.DecideAsync("another-form", Context(Tenant));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            h.Lifecycle.RegisterAsync(Form("intake"), other).AsTask());

        Assert.Equal([WritePipelineStage.Authorize], h.TakeStages());
        Assert.Empty(await h.Definitions());
    }

    [Fact(DisplayName = "ck-10 S3b: a decided form admission for another tenant stops at authorize and stores nothing")]
    public async Task Form_DecidedForAnotherTenant_StopsAtAuthorize()
    {
        var h = new FormHarness();
        var foreign = await h.Lifecycle.DecideAsync("intake", Context(OtherTenant));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            h.Lifecycle.RegisterAsync(Form("intake"), foreign).AsTask());

        Assert.Equal([WritePipelineStage.Authorize], h.TakeStages());
        Assert.Empty(await h.Definitions());
    }

    [Fact(DisplayName = "ck-10 S3b: a decided form admission for another definition cannot publish a stored draft")]
    public async Task Form_DecidedForAnotherDefinition_CannotPublish()
    {
        var h = new FormHarness();
        await h.Lifecycle.RegisterAsync(Form("intake"), Context(Tenant));
        h.TakeStages();
        var other = await h.Lifecycle.DecideAsync("another-form", Context(Tenant));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            h.Lifecycle.PublishAsync(Coordinates("intake"), other).AsTask());

        Assert.Equal([WritePipelineStage.Authorize], h.TakeStages());
        Assert.Equal(FormDefinitionStatus.Draft, (await h.Store.GetAsync(Coordinates("intake"))).Status);
    }

    [Fact(DisplayName = "ck-10 S3b: decided workflow admissions for another definition or tenant stop at authorize")]
    public async Task Workflow_DecidedForAnotherDefinitionOrTenant_StopsAtAuthorize()
    {
        var h = new WorkflowHarness();
        using var authored = WorkflowAuthored("approval", Tenant);
        var other = await h.Lifecycle.DecideAsync("another-workflow", Context(Tenant));
        var foreign = await h.Lifecycle.DecideAsync("approval", Context(OtherTenant));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            h.Lifecycle.RegisterAsync(authored.RootElement, other).AsTask());
        Assert.Equal([WritePipelineStage.Authorize], h.TakeStages());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            h.Lifecycle.RegisterAsync(authored.RootElement, foreign).AsTask());
        Assert.Equal([WritePipelineStage.Authorize], h.TakeStages());

        Assert.Empty(await h.Definitions());
    }

    [Fact(DisplayName = "ck-10 S3b: a replayed admission for another pack stops a form publish at authorize")]
    public async Task Form_ReplayedForAnotherPack_StopsAtAuthorize()
    {
        var h = new FormHarness();
        var draft = await h.RegisterPackDraftAsync("vendor-a");

        var exception = await Assert.ThrowsAsync<PackProjectionAuthorityException>(() =>
            h.Lifecycle.PublishAsync(draft, Replayed("vendor-b", Tenant)).AsTask());

        Assert.Equal(PackProjectionAuthorityCodes.SourceMismatch, exception.Code);
        Assert.Equal([WritePipelineStage.Authorize], h.TakeStages());
        Assert.Equal(FormDefinitionStatus.Draft, (await h.Store.GetAsync(Coordinates(draft.Id.Value))).Status);
    }

    [Fact(DisplayName = "ck-10 S3b: a replayed admission for another tenant stops a form publish at authorize")]
    public async Task Form_ReplayedForAnotherTenant_StopsAtAuthorize()
    {
        var h = new FormHarness();
        var draft = await h.RegisterPackDraftAsync("vendor-a");

        var exception = await Assert.ThrowsAsync<PackProjectionAuthorityException>(() =>
            h.Lifecycle.PublishAsync(draft, Replayed("vendor-a", OtherTenant)).AsTask());

        Assert.Equal(PackProjectionAuthorityCodes.TenantMismatch, exception.Code);
        Assert.Equal([WritePipelineStage.Authorize], h.TakeStages());
        Assert.Equal(FormDefinitionStatus.Draft, (await h.Store.GetAsync(Coordinates(draft.Id.Value))).Status);
    }

    [Fact(DisplayName = "ck-10 S3b: a replayed admission whose provenance names another target stops at authorize")]
    public async Task Form_ReplayedNamingAnotherTarget_StopsAtAuthorize()
    {
        var h = new FormHarness();
        var draft = await h.RegisterPackDraftAsync("vendor-a");
        // Provenance whose stored target is not the pack it names: only the internal replay factory can mint it.
        var replayed = DefinitionWriteAdmission.Replay(
            Tenant, Operator, Permission.PackagesOperate, "vendor-a", "1.0.0", "pack", "vendor-z", At, pack: null);

        var exception = await Assert.ThrowsAsync<PackProjectionAuthorityException>(() =>
            h.Writer.TransitionAsync(
                replayed, PackTarget(draft), Coordinates(draft.Id.Value), DefinitionLifecycleTransition.Publish, At,
                static (_, _) => ValueTask.CompletedTask, default).AsTask());

        Assert.Equal(PackProjectionAuthorityCodes.TargetMismatch, exception.Code);
        Assert.Equal([WritePipelineStage.Authorize], h.TakeStages());
        Assert.Equal(FormDefinitionStatus.Draft, (await h.Store.GetAsync(Coordinates(draft.Id.Value))).Status);
    }

    [Fact(DisplayName = "ck-10 S3b: a replayed admission for another tenant stops a workflow register at authorize")]
    public async Task Workflow_ReplayedForAnotherTenant_StopsAtAuthorize()
    {
        var h = new WorkflowHarness();
        using var authored = WorkflowAuthored("approval", Tenant, pack: true);

        var exception = await Assert.ThrowsAsync<PackProjectionAuthorityException>(() =>
            h.Lifecycle.RegisterAsync(authored.RootElement, Replayed("vendor-a", OtherTenant)).AsTask());

        Assert.Equal(PackProjectionAuthorityCodes.TenantMismatch, exception.Code);
        Assert.Equal([WritePipelineStage.Authorize], h.TakeStages());
        Assert.Empty(await h.Definitions());
    }

    [Fact(DisplayName = "ck-10 S3b: a replayed admission matching the stored pack revision publishes it")]
    public async Task Form_MatchingReplayed_Publishes()
    {
        var h = new FormHarness();
        var draft = await h.RegisterPackDraftAsync("vendor-a");

        var published = await h.Lifecycle.PublishAsync(draft, Replayed("vendor-a", Tenant));

        Assert.Equal(SixStages, h.TakeStages());
        Assert.Equal(FormDefinitionStatus.Published, published.Status);
        var stored = await h.Store.GetAsync(Coordinates(draft.Id.Value));
        Assert.Equal(FormDefinitionStatus.Published, stored.Status);
        Assert.Equal("vendor-a", stored.PackSource!.PackId);
    }

    [Fact(DisplayName = "ck-10 S3b: a form whose lineage parent is not registered is refused at validate; a corrected retry stores it")]
    public async Task Form_UnregisteredLineageParent_RefusedAtValidate_ThenRetrySucceeds()
    {
        var h = new FormHarness();
        var child = Form("child") with
        {
            Lineage = new FormDefinitionLineage(new FormDefinitionId("parent"), new SemanticVersion(1, 0, 0)),
        };

        var refusal = await Assert.ThrowsAsync<FormDefinitionValidationException>(() =>
            h.Lifecycle.RegisterAsync(child, Context(Tenant)).AsTask());

        Assert.Contains("lineage references parent 'parent'", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(ThroughValidate, h.TakeStages());
        Assert.Empty(await h.Definitions());

        await h.Lifecycle.RegisterAsync(Form("parent"), Context(Tenant));
        h.TakeStages();
        await h.Lifecycle.RegisterAsync(child, Context(Tenant));

        Assert.Equal(SixStages, h.TakeStages());
        Assert.Equal(
            ["child", "parent"],
            (await h.Definitions()).Select(definition => definition.Id.Value).Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "ck-10 S3b: a repeated workflow register is refused at validate and leaves the stored record")]
    public async Task Workflow_RepeatedRegister_RefusedAtValidate()
    {
        var h = new WorkflowHarness();
        using var authored = WorkflowAuthored("approval", Tenant);
        await h.Lifecycle.RegisterAsync(authored.RootElement, Context(Tenant));
        await h.Lifecycle.PublishAsync(Coordinates("approval"), Context(Tenant));
        h.TakeStages();

        await Assert.ThrowsAsync<WorkflowDefinitionConflictException>(() =>
            h.Lifecycle.RegisterAsync(authored.RootElement, Context(Tenant)).AsTask());

        Assert.Equal(ThroughValidate, h.TakeStages());
        var stored = Assert.Single(await h.Definitions());
        Assert.Equal(WorkflowDefinitionStatus.Published, stored.Status);
    }

    [Fact(DisplayName = "ck-10 S3b: deprecating a draft form is an invalid transition refused at validate")]
    public async Task Form_DeprecateDraft_RefusedAtValidate()
    {
        var h = new FormHarness();
        await h.Lifecycle.RegisterAsync(Form("intake"), Context(Tenant));
        h.TakeStages();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Lifecycle.DeprecateAsync(Coordinates("intake"), Context(Tenant)).AsTask());

        Assert.Contains("cannot transition from Draft to Deprecated", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(ThroughValidate, h.TakeStages());
        Assert.Equal(FormDefinitionStatus.Draft, (await h.Store.GetAsync(Coordinates("intake"))).Status);
    }

    [Fact(DisplayName = "ck-10 S3b: publishing a withdrawn workflow is an invalid transition refused at validate")]
    public async Task Workflow_PublishWithdrawn_RefusedAtValidate()
    {
        var h = new WorkflowHarness();
        using var authored = WorkflowAuthored("approval", Tenant);
        await h.Lifecycle.RegisterAsync(authored.RootElement, Context(Tenant));
        await h.Lifecycle.WithdrawAsync(Coordinates("approval"), Context(Tenant));
        h.TakeStages();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Lifecycle.PublishAsync(Coordinates("approval"), Context(Tenant)).AsTask());

        Assert.Contains("cannot transition from Withdrawn to Published", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(ThroughValidate, h.TakeStages());
        Assert.Equal(WorkflowDefinitionStatus.Withdrawn, (await h.Store.GetAsync(Coordinates("approval"))).Status);
    }

    private static AuthorizationWriteContext Context(TenantId tenant) => new(Operator, tenant, At);

    private static DefinitionCoordinates Coordinates(string id) => new(Tenant, id, "1.0.0");

    private static DefinitionWriteTarget PackTarget(FormDefinition definition) =>
        new(definition.Tenant, "forms", Permission.FormsAuthor, definition.Id.Value)
        {
            DeclaredPackSource = definition.PackSource,
            CreatedAt = definition.CreatedAt,
            UpdatedAt = definition.UpdatedAt,
        };

    /// <summary>A pack authority replayed from a stored admission: no live decision, only its provenance.</summary>
    private static PackProjectionAuthority Replayed(string packId, TenantId tenant) =>
        PackProjectionAuthority.FromAdmission(new PackProjectionAdmission(
            Guid.NewGuid(), packId, "1.0.0", tenant, Operator, At, ["grant:operator", "definition:operator"], Projected: false));

    private static async Task<PackProjectionAuthority> LiveAuthority(string packId)
    {
        var scope = Harborline.Api.Foundation.IdentityAtlas.Permissions.ScopeExpression.Parse($"/records/{packId}");
        var decision = await TestAuthorization.AllowGate().DecideAsync(new AuthorizationGateRequest(
            new PermissionAtom(AuthorizationOperation.Parse(Permission.PackagesOperate), scope),
            Operator, Tenant, new AuthorizationTarget("pack", packId, scope), At));
        return new PackProjectionAuthority(decision, packId, "1.0.0", Tenant, Operator, At);
    }

    private static FormDefinition Form(string id, CascadeLayer layer = CascadeLayer.Tenant, string? packId = null) =>
        new(
            new DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>(
                new FormDefinitionId(id), new SemanticVersion(1, 0, 0), Tenant, layer,
                new FormDefinitionProvenance(IdentityRef.System, null), [],
                Contract: null),
            FormDefinitionStatus.Draft,
            new SchemaId("schema"),
            new HarborlineOverlay(new Dictionary<string, FieldOverlay>(), [], []),
            At,
            At)
        {
            PackSource = packId is null ? null : new PackProjectionSource(packId, "1.0.0"),
        };

    private static JsonDocument WorkflowAuthored(string id, TenantId tenant, bool pack = false) => JsonDocument.Parse($$"""
        {
          "key": "{{id}}", "version": "1.0.0", "tenant": "{{tenant.Value}}",
          "initialState": "start",
          "states": [ { "id": "start", "kind": "Normal" }, { "id": "done", "kind": "Terminal" } ],
          "triggers": [ { "id": "submit", "kind": "HumanAction", "task": "submit" } ],
          "transitions": [ { "id": "submit", "from": "start", "on": "submit", "to": "done" } ],
          "actions": [],
          "provenance": "{{(pack ? "Pack" : "Tenant")}}",
          "owner": { "scheme": "{{(pack ? "system" : "actor")}}", "value": "{{(pack ? "__sunfish" : "operator")}}" }
        }
        """);

    private abstract class Harness : IWritePipelineObserver
    {
        private readonly List<WritePipelineStage> stages = [];

        protected InMemoryEntityStore Entities { get; } = new(new InMemoryAssetStorage(), TimeProvider.System);

        public void OnStage(WritePipelineStage stage) => stages.Add(stage);

        /// <summary>The stages entered since the last call.</summary>
        public WritePipelineStage[] TakeStages()
        {
            var taken = stages.ToArray();
            stages.Clear();
            return taken;
        }
    }

    private sealed class FormHarness : Harness
    {
        public FormHarness()
        {
            Store = new EntityStoreFormDefinitionStore(Entities, TimeProvider.System);
            Lifecycle = new AuthorizedFormDefinitionLifecycle(
                Store, Entities, TimeProvider.System, TestAuthorization.AllowGate(), TestAuthorization.RoleGate(),
                legalHold: null, pipelineObserver: this);
            Writer = (AuthorizedFormDefinitionLifecycle.DefinitionWriter)typeof(AuthorizedFormDefinitionLifecycle)
                .GetField("writer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Lifecycle)!;
        }

        public EntityStoreFormDefinitionStore Store { get; }
        public AuthorizedFormDefinitionLifecycle Lifecycle { get; }
        public AuthorizedFormDefinitionLifecycle.DefinitionWriter Writer { get; }

        /// <summary>Registers a pack draft under a live authority for <paramref name="packId"/>.</summary>
        public async Task<FormDefinition> RegisterPackDraftAsync(string packId)
        {
            var registered = await Lifecycle.RegisterAsync(
                Form("pack-form", CascadeLayer.Pack), await LiveAuthority(packId));
            Assert.Equal(SixStages, TakeStages());
            return registered;
        }

        public async Task<IReadOnlyList<FormDefinition>> Definitions()
        {
            var definitions = new List<FormDefinition>();
            await foreach (var definition in Store.ListByTenantAsync(Tenant)) definitions.Add(definition);
            return definitions;
        }
    }

    private sealed class WorkflowHarness : Harness
    {
        public WorkflowHarness()
        {
            var admission = new WorkflowAdmissionValidator();
            Store = new EntityStoreWorkflowDefinitionStore(Entities, admission, TimeProvider.System);
            Lifecycle = new AuthorizedWorkflowDefinitionLifecycle(
                Store, Entities, admission, TimeProvider.System, TestAuthorization.AllowGate(),
                TestAuthorization.RoleGate(), pipelineObserver: this);
        }

        public EntityStoreWorkflowDefinitionStore Store { get; }
        public AuthorizedWorkflowDefinitionLifecycle Lifecycle { get; }

        public async Task<IReadOnlyList<WorkflowDefinitionRecord>> Definitions()
        {
            var definitions = new List<WorkflowDefinitionRecord>();
            await foreach (var definition in Store.ListByTenantAsync(Tenant)) definitions.Add(definition);
            return definitions;
        }
    }
}
