using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Merge;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Foundation.ReportDefinitions;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

using static Harborline.Api.Kernel.Runtime.WritePipelineStage;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// ck-10 S5b (DES-0029): pack activation, deactivation and narrowing each run the six ADR 0038 stages
/// through <see cref="WritePipeline.RunAsync"/>. Every case asserts the stages the real installer entered
/// and the store's state, not only the returned outcome.
/// </summary>
public sealed class PackActivationPipelineTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-0000000005b5");
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string PackKey = "test.s5b";
    private const string ContentKey = "s5b-item";
    private const string Seed = """{"label":"reviewed","note":"publisher"}""";

    private readonly InMemoryPackInstallStore _store = new();
    private readonly InMemoryPackInstallAudit _audit = new();
    private readonly StageRecorder _stages = new();
    private bool _allow = true;
    private int _decisions;
    private readonly PackInstaller _installer;

    public PackActivationPipelineTests()
    {
        _installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), _store,
            new WorkflowRefusingPackContentAdmission(), _audit,
            TestAuthorization.Gate(_ => { _decisions++; return _allow; }), pipelineObserver: _stages);
        SeedPack();
    }

    [Fact]
    public async Task An_allowed_activation_runs_the_six_stages_and_makes_the_version_active()
    {
        PlatformPackTestPreload.Activate(_store, Tenant);

        var outcome = await _installer.ActivateAsync(Context(), PackKey, "1.0.0");

        Assert.True(outcome.Activated, outcome.Error + " " + outcome.Detail);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
        Assert.Contains(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Activated && entry.PackKey == PackKey);
    }

    [Fact]
    public async Task Activation_under_a_projection_read_lease_refuses_without_a_transaction_or_state_change()
    {
        PlatformPackTestPreload.Activate(_store, Tenant);
        using (PackProjectionActivationBarrier.Read())
        {
            var outcome = await _installer.ActivateAsync(Context(), PackKey, "1.0.0");

            Assert.False(outcome.Activated);
            Assert.Equal(PackInstallCodes.ActivateProjectionFailed, outcome.Error);
            Assert.Equal("A projection read lease cannot be upgraded to activation.", outcome.Detail);
            Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
            Assert.Null(_store.GetActive(Tenant, PackKey));
            var refusal = Assert.Single(_audit.Query(Tenant));
            Assert.Equal(PackInstallAuditAction.Refused, refusal.Action);
            Assert.Equal("pack.install.activate.projection_failed", refusal.Detail);
        }

        _stages.Entered.Clear();
        var retry = await _installer.ActivateAsync(Context(), PackKey, "1.0.0");
        Assert.True(retry.Activated, retry.Error + " " + retry.Detail);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
    }

    [Fact]
    public async Task A_refused_activation_decision_stops_at_authorize_and_nothing_becomes_active()
    {
        PlatformPackTestPreload.Activate(_store, Tenant);
        _allow = false;

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => _installer.ActivateAsync(Context(), PackKey, "1.0.0"));

        Assert.Equal([Authorize], _stages.Entered);
        Assert.Null(_store.GetActive(Tenant, PackKey));
        Assert.Contains(_audit.Query(Tenant), entry => entry.PreDecision && entry.Detail == PackInstallCodes.RefusedAuthorizationDenied);
    }

    [Fact]
    public async Task A_validate_refusal_stops_at_validate_with_nothing_active_and_a_retry_activates_once_the_requirement_is_met()
    {
        var refused = await _installer.ActivateAsync(Context(), PackKey, "1.0.0");

        Assert.False(refused.Activated);
        Assert.Equal(PackInstallCodes.ActivatePlatformPackRequired, refused.Error);
        Assert.Equal([Authorize, Bind, Mutate, Validate], _stages.Entered);
        Assert.Null(_store.GetActive(Tenant, PackKey));
        Assert.Contains(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Refused
            && entry.PackKey == PackKey && entry.Detail!.StartsWith(PackInstallCodes.ActivatePlatformPackRequired, StringComparison.Ordinal));

        // Recovery: the platform pack becomes active, and the same activation now commits.
        PlatformPackTestPreload.Activate(_store, Tenant);
        _stages.Entered.Clear();
        var retried = await _installer.ActivateAsync(Context(), PackKey, "1.0.0");

        Assert.True(retried.Activated, retried.Error + " " + retried.Detail);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
    }

    [Fact]
    public async Task Deactivation_runs_the_six_stages_and_leaves_the_pack_inactive()
    {
        await ActivateAsync();

        var outcome = await _installer.DeactivateAsync(Context(), PackKey, "1.0.0");

        Assert.True(outcome.Deactivated, outcome.Error);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Null(_store.GetActive(Tenant, PackKey));
        var audit = Assert.Single(_audit.Query(Tenant).Where(entry => entry.Action == PackInstallAuditAction.Deactivated));
        Assert.Equal(PackKey, audit.PackKey);
        Assert.Equal("pack.install.deactivated", audit.Detail);
        Assert.DoesNotContain(_audit.Query(Tenant), entry => entry.PreDecision);
    }

    [Fact]
    public async Task A_refused_deactivation_decision_stops_at_authorize_and_leaves_the_pack_active()
    {
        await ActivateAsync();
        _allow = false;

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => _installer.DeactivateAsync(Context(), PackKey, "1.0.0"));

        Assert.Equal([Authorize], _stages.Entered);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
    }

    [Fact]
    public async Task A_deactivation_refused_at_validate_leaves_the_platform_pack_active()
    {
        await ActivateAsync();

        var outcome = await _installer.DeactivateAsync(Context(), PlatformPackTestPreload.PackKey, PlatformPackTestPreload.Version);

        Assert.False(outcome.Deactivated);
        Assert.Equal(PackInstallCodes.DeactivateDependentsActive, outcome.Error);
        Assert.Equal([Authorize, Bind, Mutate, Validate], _stages.Entered);
        Assert.Equal(PlatformPackTestPreload.Version, _store.GetActive(Tenant, PlatformPackTestPreload.PackKey)?.Version);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
    }

    [Fact]
    public async Task Narrowing_runs_the_six_stages_and_stores_the_override()
    {
        await ActivateAsync();

        var outcome = await _installer.NarrowAsync(Context(), PackKey, ContentKey, JsonNode.Parse("""{"note":null}""")!, Decision());

        Assert.True(outcome.Recorded, outcome.RefusalCode);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        var stored = Assert.Single(_store.GetOverrides(Tenant, PackKey));
        Assert.Equal(ContentKey, stored.ContentKey);
        Assert.Equal("""{"note":null}""", stored.OverlayPatch.ToJsonString());
        Assert.DoesNotContain(_audit.Query(Tenant), entry => entry.PreDecision);
        var narrowed = Assert.Single(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Narrowed);
        Assert.Equal(PackKey, narrowed.PackKey);
        Assert.Equal("1.0.0", narrowed.Version);
        Assert.Equal("pack.install.narrowed:" + ContentKey, narrowed.Detail);
        Assert.Equal("test-operator", narrowed.ActingPrincipal);
    }

    [Fact]
    public async Task A_widening_narrowing_is_refused_at_validate_with_the_override_unchanged()
    {
        await ActivateAsync();
        Assert.True((await _installer.NarrowAsync(Context(), PackKey, ContentKey, JsonNode.Parse("""{"note":null}""")!, Decision())).Recorded);
        _stages.Entered.Clear();

        var outcome = await _installer.NarrowAsync(Context(), PackKey, ContentKey, JsonNode.Parse("""{"extra":"added"}""")!, Decision());

        Assert.False(outcome.Recorded);
        Assert.Equal(PackTenantNarrowing.WideningRefusedCode, outcome.RefusalCode);
        Assert.Equal("/extra", outcome.WideningPath);
        Assert.Equal([Authorize, Bind, Mutate, Validate], _stages.Entered);
        var stored = Assert.Single(_store.GetOverrides(Tenant, PackKey));
        Assert.Equal("""{"note":null}""", stored.OverlayPatch.ToJsonString());
    }

    [Fact]
    public async Task A_refused_narrowing_decision_stops_at_authorize_with_no_override()
    {
        await ActivateAsync();
        var denied = await TestAuthorization.Gate(false).DecideAsync(
            new AuthorizationWriteContext(new ActorId("test-operator"), Tenant, Now)
                .Request(AuthorizationOperation.Parse(Permission.PackagesOperate), "pack", PackKey));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            _installer.NarrowAsync(Context(), PackKey, ContentKey, JsonNode.Parse("""{"note":null}""")!, denied));

        Assert.Equal([Authorize], _stages.Entered);
        Assert.Empty(_store.GetOverrides(Tenant, PackKey));
    }

    [Fact(DisplayName = "ck-10 S5b: a request cancelled at the commit-to-react boundary still audits, retires the definitions and returns the committed outcome")]
    public async Task Cancellation_at_react_still_completes_the_committed_deactivation()
    {
        var reports = Reports();
        ((IPackProjectionReconciler)_installer).AttachProjector(Projector(_store, reports));
        CommitReportPack(_store);
        PlatformPackTestPreload.Activate(_store, Tenant);
        Assert.True((await _installer.ActivateAsync(Context(), ReportPackKey, "1.0.0")).Activated);
        Assert.NotNull(await reports.GetDefinitionAsync(Tenant.Value, ReportKey, ItemVersion));
        _stages.Entered.Clear();

        // The request disconnects exactly when commit has returned and react is about to run.
        using var request = new CancellationTokenSource();
        _stages.OnEnter = stage => { if (stage == React) request.Cancel(); };

        var outcome = await _installer.DeactivateAsync(Context(), ReportPackKey, "1.0.0", request.Token);

        Assert.True(request.IsCancellationRequested);
        Assert.True(outcome.Deactivated, outcome.Error);
        Assert.True(outcome.Projected);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Null(_store.GetActive(Tenant, ReportPackKey));
        Assert.Null(await reports.GetDefinitionAsync(Tenant.Value, ReportKey, ItemVersion));
        Assert.Contains(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Deactivated && entry.PackKey == ReportPackKey);
        Assert.Empty(((IPackProjectionAdmissionStore)_store).ListIncompleteProjectionAdmissions());
    }

    [Fact(DisplayName = "ck-10 S5b: a crash between deactivation commit and react leaves a durable admission that a restarted installer's reconcile completes")]
    public async Task A_crash_after_deactivation_commit_is_retired_by_reconcile_after_restart()
    {
        // The registry stands in for the durable definition store, which outlives the process.
        var reports = Reports();
        await using var origin = await PacksTestStore.CreateAsync();
        var store = new DurablePackInstallStore(origin.Factory);
        PlatformPackTestPreload.Activate(store, Tenant);
        CommitReportPack(store);
        var stages = new StageRecorder();
        var installer = Installer(store, new InMemoryPackInstallAudit(), stages);
        ((IPackProjectionReconciler)installer).AttachProjector(Projector(store, reports));
        Assert.True((await installer.ActivateAsync(Context(), ReportPackKey, "1.0.0")).Activated);
        Assert.NotNull(await reports.GetDefinitionAsync(Tenant.Value, ReportKey, ItemVersion));

        // The process dies after commit returns and before react runs.
        stages.OnEnter = stage => { if (stage == React) throw new SimulatedCrash(); };
        await Assert.ThrowsAsync<SimulatedCrash>(() => installer.DeactivateAsync(Context(), ReportPackKey, "1.0.0"));
        Assert.NotNull(await reports.GetDefinitionAsync(Tenant.Value, ReportKey, ItemVersion));

        await using var restart = PacksTestStore.Reopen(origin);
        var reopened = new DurablePackInstallStore(restart.Factory);
        var reconciler = (IPackProjectionReconciler)Installer(reopened, new InMemoryPackInstallAudit(), new StageRecorder());
        reconciler.AttachProjector(Projector(reopened, reports));
        Assert.Null(reopened.GetActive(Tenant, ReportPackKey));
        Assert.Contains(((IPackProjectionAdmissionStore)reopened).ListIncompleteProjectionAdmissions(),
            admission => admission.PackId == ReportPackKey);

        reconciler.ReconcilePending();

        Assert.Null(await reports.GetDefinitionAsync(Tenant.Value, ReportKey, ItemVersion));
        Assert.Empty(((IPackProjectionAdmissionStore)reopened).ListIncompleteProjectionAdmissions());
    }

    [Theory(DisplayName = "ck-10 S5b: a projection that refuses or throws inside the activation transaction leaves the pointer, key ownership and admission as before")]
    [InlineData(false, PackInstallCodes.ActivateProjectionRefused)]
    [InlineData(true, PackInstallCodes.ActivateProjectionFailed)]
    public async Task A_failed_projection_rolls_back_the_whole_activation(bool projectorThrows, string expectedError)
    {
        PlatformPackTestPreload.Activate(_store, Tenant);
        ((IPackProjectionReconciler)_installer).AttachProjector(projectorThrows
            ? new ProjectorDouble(() => throw new InvalidOperationException("projector down"))
            : new ProjectorDouble(() => new RefusedProjection()));

        var outcome = await _installer.ActivateAsync(Context(ownership: new Dictionary<string, string> { [ContentKey] = PackKey }), PackKey, "1.0.0");

        Assert.False(outcome.Activated);
        Assert.Equal(expectedError, outcome.Error);
        Assert.Null(_store.GetActive(Tenant, PackKey));
        Assert.Equal(PackLifecycleState.Draft, _store.GetVersion(Tenant, PackKey, "1.0.0")?.Lifecycle);
        Assert.Empty(_store.GetKeyOwnership(Tenant));
        Assert.Empty(((IPackProjectionAdmissionStore)_store).ListIncompleteProjectionAdmissions());
    }

    [Fact(DisplayName = "ck-10 S5b: a split composition rolls back the mutation face and the projection-admission face when the projection refuses")]
    public async Task A_refused_projection_rolls_back_each_enlisted_store_face()
    {
        PlatformPackTestPreload.Activate(_store, Tenant);
        var mutations = new InMemoryPackInstallStore();
        var projection = new InMemoryPackInstallStore();
        CommitPack(projection, PackKey, "1.0.0", Item(ContentKey, PackContentKind.FormDefinition, Seed));
        var installer = SplitInstaller(mutations, projection);
        ((IPackProjectionReconciler)installer).AttachProjector(new ProjectorDouble(() => new RefusedProjection()));

        var outcome = await installer.ActivateAsync(Context(ownership: new Dictionary<string, string> { [ContentKey] = PackKey }), PackKey, "1.0.0");

        Assert.Equal(PackInstallCodes.ActivateProjectionRefused, outcome.Error);
        Assert.Empty(mutations.GetKeyOwnership(Tenant));
        Assert.Null(projection.GetActive(Tenant, PackKey));
        Assert.Empty(((IPackProjectionAdmissionStore)projection).ListIncompleteProjectionAdmissions());
    }

    [Fact(DisplayName = "ck-10 S5b: a transition-state failure inside the activation transaction reports the version as not installed")]
    public async Task A_transition_state_failure_at_commit_maps_to_not_installed()
    {
        PlatformPackTestPreload.Activate(_store, Tenant);
        // The projection-admission face has never seen this version, so its pointer flip is a transition-state failure.
        var projection = new InMemoryPackInstallStore();
        var installer = SplitInstaller(_store, projection);

        var outcome = await installer.ActivateAsync(Context(), PackKey, "1.0.0");

        Assert.False(outcome.Activated);
        Assert.Equal(PackInstallCodes.ActivateNotInstalled, outcome.Error);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Null(_store.GetActive(Tenant, PackKey));
    }

    [Fact(DisplayName = "ck-10 S5b: a refusal reached at commit is audited as Refused with its code, a committed activation as Activated")]
    public async Task The_react_audit_records_the_committed_action()
    {
        PlatformPackTestPreload.Activate(_store, Tenant);
        var projector = new ProjectorDouble(() => new RefusedProjection());
        ((IPackProjectionReconciler)_installer).AttachProjector(projector);

        Assert.False((await _installer.ActivateAsync(Context(), PackKey, "1.0.0")).Activated);

        var refused = Assert.Single(_audit.Query(Tenant), entry => entry.PackKey == PackKey);
        Assert.Equal(PackInstallAuditAction.Refused, refused.Action);
        Assert.Equal(PackInstallCodes.ActivateProjectionRefused, refused.Detail);

        projector.Result = () => null;
        var activated = await _installer.ActivateAsync(Context(), PackKey, "1.0.0");

        Assert.True(activated.Activated, activated.Error + " " + activated.Detail);
        Assert.Null(activated.Detail);
        var entries = _audit.Query(Tenant).Where(entry => entry.PackKey == PackKey).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(PackInstallAuditAction.Activated, entries[1].Action);
        Assert.Equal("pack.install.activated", entries[1].Detail);
    }

    [Theory(DisplayName = "ck-10 S5b: an audit or observer failure after commit still reports the activation, with the failure in its detail")]
    [InlineData(true, false, "Activation committed; audit notification failed: audit down")]
    [InlineData(false, true, "Projection committed; post-commit notification or cleanup failed. (observer down)")]
    [InlineData(true, true, "Activation committed; audit notification failed: audit down Projection committed; post-commit notification or cleanup failed. (observer down)")]
    public async Task A_notification_failure_after_commit_keeps_the_activation(bool auditFails, bool observerFails, string expectedDetail)
    {
        PlatformPackTestPreload.Activate(_store, Tenant);
        var installer = Installer(_store, new ActivatedAuditDouble(_audit, auditFails), _stages);
        ((IPackProjectionReconciler)installer).AttachProjector(new ProjectorDouble(() => null, observerFails));

        var outcome = await installer.ActivateAsync(Context(), PackKey, "1.0.0");

        Assert.True(outcome.Activated, outcome.Error);
        Assert.Equal(expectedDetail, outcome.Detail);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
        Assert.Empty(((IPackProjectionAdmissionStore)_store).ListIncompleteProjectionAdmissions());
    }

    [Fact(DisplayName = "ck-10 S5b: deactivating with nothing active is refused at validate")]
    public async Task Deactivating_when_nothing_is_active_is_refused_at_validate()
    {
        var outcome = await _installer.DeactivateAsync(Context(), PackKey, "1.0.0");

        Assert.False(outcome.Deactivated);
        Assert.Equal(PackInstallCodes.DeactivateNotActive, outcome.Error);
        Assert.Equal([Authorize, Bind, Mutate, Validate], _stages.Entered);
        Assert.Null(_store.GetActive(Tenant, PackKey));
    }

    [Fact(DisplayName = "ck-10 S5b: deactivating a version other than the active one is refused at validate and the active version stays")]
    public async Task Deactivating_a_version_that_is_not_active_is_refused_at_validate()
    {
        CommitPack(_store, PackKey, "2.0.0", Item(ContentKey, PackContentKind.FormDefinition, Seed));
        await ActivateAsync();

        var outcome = await _installer.DeactivateAsync(Context(), PackKey, "2.0.0");

        Assert.False(outcome.Deactivated);
        Assert.Equal(PackInstallCodes.DeactivateNotActive, outcome.Error);
        Assert.Equal([Authorize, Bind, Mutate, Validate], _stages.Entered);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
    }

    [Theory(DisplayName = "ck-10 S5b: a blank pack key, content key or principal is refused before any decision, audited, with nothing changed")]
    [InlineData(" ", ContentKey, "test-operator", PackInstallCodes.RefusedBlankPackKey, "packKey")]
    [InlineData(PackKey, " ", "test-operator", PackInstallCodes.NarrowUnknownContentKey, "contentKey")]
    [InlineData(PackKey, ContentKey, " ", PackInstallCodes.NarrowRefusedNoPrincipal, "principal")]
    public async Task A_blank_narrowing_input_is_refused_before_the_decision(
        string packKey, string contentKey, string principal, string expectedCode, string expectedParam)
    {
        await ActivateAsync();
        var before = _audit.Query(Tenant).Count;

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            _installer.NarrowAsync(Context(principal), packKey, contentKey, JsonNode.Parse("""{"note":null}""")!, Decision()));

        Assert.Equal(expectedParam, refused.ParamName);
        Assert.Equal([Authorize], _stages.Entered);
        var refusal = Assert.Single(_audit.Query(Tenant).Skip(before));
        Assert.True(refusal.PreDecision);
        Assert.Equal(PackInstallAuditAction.Refused, refusal.Action);
        Assert.Equal(expectedCode, refusal.Detail);
        Assert.Empty(_store.GetOverrides(Tenant, PackKey));
    }

    [Theory(DisplayName = "ck-10 S5b: a carried decision for another principal or another instant is refused with the override unchanged")]
    [InlineData("other-operator", 0)]
    [InlineData("test-operator", 1)]
    public async Task A_decision_that_does_not_match_the_narrowing_context_is_refused(string decisionPrincipal, int decisionOffsetMinutes)
    {
        await ActivateAsync();
        var decision = TestAuthorization.AllowedDecision(Tenant, PackKey, "pack", Permission.PackagesOperate,
            principal: decisionPrincipal, at: Now.AddMinutes(decisionOffsetMinutes));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            _installer.NarrowAsync(Context(), PackKey, ContentKey, JsonNode.Parse("""{"note":null}""")!, decision));

        Assert.Equal("decision", refused.ParamName);
        Assert.Equal([Authorize], _stages.Entered);
        Assert.Empty(_store.GetOverrides(Tenant, PackKey));
    }

    [Fact(DisplayName = "ck-10 S5b: narrowing a cascade-defaults item admits it composed with the stored overrides")]
    public async Task Narrowing_a_cascade_item_admits_the_composed_cascade()
    {
        var admission = SeedCascadePack();
        var installer = Installer(_store, _audit, _stages, admission);

        var outcome = await installer.NarrowAsync(Context(), CascadePackKey, "cascade-a", JsonNode.Parse("""{"note":null}""")!, CascadeDecision());

        Assert.True(outcome.Recorded, outcome.RefusalCode);
        var composed = Assert.Single(admission.Admitted);
        Assert.Equal(
            [("cascade-a", """{"label":"reviewed"}"""), ("cascade-b", """{"tone":"formal"}""")],
            composed.Select(item => (item.Key, item.CanonicalJson)).ToArray());
        Assert.Equal(
            [("cascade-b", """{"size":null}"""), ("cascade-a", """{"note":null}""")],
            _store.GetOverrides(Tenant, CascadePackKey).Select(o => (o.ContentKey, o.OverlayPatch.ToJsonString())).ToArray());
    }

    [Fact(DisplayName = "ck-10 S5b: narrowing a non-cascade item stores its override without composing the cascade")]
    public async Task Narrowing_a_non_cascade_item_does_not_compose_the_cascade()
    {
        var admission = SeedCascadePack();
        var installer = Installer(_store, _audit, _stages, admission);

        var outcome = await installer.NarrowAsync(Context(), CascadePackKey, "form-c", JsonNode.Parse("""{"note":null}""")!, CascadeDecision());

        Assert.True(outcome.Recorded, outcome.RefusalCode);
        Assert.Empty(admission.Admitted);
        Assert.Equal(
            [("cascade-b", """{"size":null}"""), ("form-c", """{"note":null}""")],
            _store.GetOverrides(Tenant, CascadePackKey).Select(o => (o.ContentKey, o.OverlayPatch.ToJsonString())).ToArray());
    }

    [Theory(DisplayName = "ck-10 S5b: a blank activation pack key, version or principal throws after its pre-decision audit, with no decision and nothing active")]
    [InlineData(" ", "1.0.0", "test-operator", PackInstallCodes.RefusedBlankPackKey, "packKey")]
    [InlineData(PackKey, " ", "test-operator", PackInstallCodes.RefusedBlankVersion, "version")]
    [InlineData(PackKey, "1.0.0", " ", PackInstallCodes.ActivateRefusedNoPrincipal, "actingPrincipal")]
    public async Task A_blank_activation_input_is_refused_before_the_decision(
        string packKey, string version, string principal, string expectedCode, string expectedParam)
    {
        PlatformPackTestPreload.Activate(_store, Tenant);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => _installer.ActivateAsync(Context(principal), packKey, version));

        Assert.Equal(expectedParam, refused.ParamName);
        Assert.Equal([Authorize], _stages.Entered);
        Assert.Equal(0, _decisions);
        var refusal = Assert.Single(_audit.Query(Tenant));
        Assert.True(refusal.PreDecision);
        Assert.Equal(PackInstallAuditAction.Refused, refusal.Action);
        Assert.Equal(expectedCode, refusal.Detail);
        Assert.Null(_store.GetActive(Tenant, PackKey));
    }

    [Theory(DisplayName = "ck-10 S5b: a blank deactivation pack key, version or principal throws after its pre-decision audit, with no decision and the pack still active")]
    [InlineData(" ", "1.0.0", "test-operator", PackInstallCodes.RefusedBlankPackKey, "packKey")]
    [InlineData(PackKey, " ", "test-operator", PackInstallCodes.RefusedBlankVersion, "version")]
    [InlineData(PackKey, "1.0.0", " ", PackInstallCodes.DeactivateRefusedNoPrincipal, "actingPrincipal")]
    public async Task A_blank_deactivation_input_is_refused_before_the_decision(
        string packKey, string version, string principal, string expectedCode, string expectedParam)
    {
        await ActivateAsync();
        _decisions = 0;
        var before = _audit.Query(Tenant).Count;

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => _installer.DeactivateAsync(Context(principal), packKey, version));

        Assert.Equal(expectedParam, refused.ParamName);
        Assert.Equal([Authorize], _stages.Entered);
        Assert.Equal(0, _decisions);
        var refusal = Assert.Single(_audit.Query(Tenant).Skip(before));
        Assert.True(refusal.PreDecision);
        Assert.Equal(PackInstallAuditAction.Refused, refusal.Action);
        Assert.Equal(expectedCode, refusal.Detail);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
    }

    [Fact(DisplayName = "ck-10 S5b: narrowing a pack that is not active is refused at bind, audited, with no override")]
    public async Task Narrowing_an_inactive_pack_is_refused_at_bind()
    {
        var outcome = await _installer.NarrowAsync(Context(), PackKey, ContentKey, JsonNode.Parse("""{"note":null}""")!, Decision());

        AssertNarrowingRefused(outcome, PackInstallCodes.NarrowNotActive, ContentKey, [Authorize, Bind]);
        Assert.Empty(_store.GetOverrides(Tenant, PackKey));
    }

    [Fact(DisplayName = "ck-10 S5b: narrowing a content key the active pack does not ship is refused at bind, audited, with no override")]
    public async Task Narrowing_an_unknown_content_key_is_refused_at_bind()
    {
        await ActivateAsync();

        var outcome = await _installer.NarrowAsync(Context(), PackKey, "missing-item", JsonNode.Parse("""{"note":null}""")!, Decision());

        AssertNarrowingRefused(outcome, PackInstallCodes.NarrowUnknownContentKey, "missing-item", [Authorize, Bind]);
        Assert.Empty(_store.GetOverrides(Tenant, PackKey));
    }

    [Fact(DisplayName = "ck-10 S5b: a narrowing whose composed cascade is inadmissible is refused at validate, audited, with the stored overrides unchanged")]
    public async Task Narrowing_an_inadmissible_cascade_is_refused_at_validate()
    {
        SeedCascadePack();

        // The default admission does not consume cascade defaults, so the composed cascade is refused.
        var outcome = await _installer.NarrowAsync(Context(), CascadePackKey, "cascade-a", JsonNode.Parse("""{"note":null}""")!, CascadeDecision());

        AssertNarrowingRefused(outcome, "pack.install.admission.not_wired", "cascade-a", [Authorize, Bind, Mutate, Validate]);
        var stored = Assert.Single(_store.GetOverrides(Tenant, CascadePackKey));
        Assert.Equal("cascade-b", stored.ContentKey);
        Assert.Equal("""{"size":null}""", stored.OverlayPatch.ToJsonString());
    }

    [Fact(DisplayName = "ck-10 S5b: a request cancelled after the projection and before the commit rolls the activation back")]
    public async Task Cancellation_between_projection_and_commit_rolls_the_activation_back()
    {
        PlatformPackTestPreload.Activate(_store, Tenant);
        using var request = new CancellationTokenSource();
        // The projector runs synchronously inside the commit stage, so the request disconnects exactly after projection.
        ((IPackProjectionReconciler)_installer).AttachProjector(new ProjectorDouble(() => { request.Cancel(); return null; }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _installer.ActivateAsync(Context(), PackKey, "1.0.0", request.Token));

        Assert.Null(_store.GetActive(Tenant, PackKey));
        Assert.Equal(PackLifecycleState.Draft, _store.GetVersion(Tenant, PackKey, "1.0.0")?.Lifecycle);
        Assert.Empty(((IPackProjectionAdmissionStore)_store).ListIncompleteProjectionAdmissions());
        Assert.DoesNotContain(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Activated);
    }

    private void AssertNarrowingRefused(PackNarrowingOutcome outcome, string code, string contentKey, WritePipelineStage[] stages)
    {
        Assert.False(outcome.Recorded);
        Assert.Equal(code, outcome.RefusalCode);
        Assert.Equal(stages, _stages.Entered);
        var refusal = Assert.Single(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Refused);
        Assert.False(refusal.PreDecision);
        Assert.Equal($"{code}:{contentKey}", refusal.Detail);
        Assert.DoesNotContain(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Narrowed);
    }

    [Fact(DisplayName = "ck-10 S5b: a pack deactivated between validate and commit is refused at commit as not active, audited, with the racing deactivation standing")]
    public async Task A_deactivation_raced_by_another_deactivation_is_refused_at_commit()
    {
        await ActivateAsync();
        var before = _audit.Query(Tenant).Count;
        var admissions = Admissions(_store);
        _stages.OnEnter = stage => { if (stage == Commit) _store.Deactivate(Tenant, PackKey, "1.0.0"); };

        var outcome = await _installer.DeactivateAsync(Context(), PackKey, "1.0.0");

        Assert.False(outcome.Deactivated);
        Assert.Equal(PackInstallCodes.DeactivateNotActive, outcome.Error);
        Assert.Empty(outcome.Dependents!);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Null(_store.GetActive(Tenant, PackKey));
        Assert.Equal(PackInstallCodes.DeactivateNotActive, AssertSingleDeactivationRefusal(before).Detail);
        Assert.Equal(admissions, Admissions(_store));
    }

    [Fact(DisplayName = "ck-10 S5b: a projection face that stops seeing the pack active before commit refuses the flip as not active, audited, with the reader unchanged")]
    public async Task A_deactivation_whose_projection_face_is_raced_is_refused_at_commit()
    {
        await ActivateAsync();
        // The reader still shows the pack active, so commit's re-check passes and the flip itself refuses.
        var projection = new InMemoryPackInstallStore();
        PlatformPackTestPreload.Activate(projection, Tenant);
        CommitPack(projection, PackKey, "1.0.0", Item(ContentKey, PackContentKind.FormDefinition, Seed));
        projection.Activate(Tenant, PackKey, "1.0.0");
        var installer = SplitInstaller(_store, projection);
        var before = _audit.Query(Tenant).Count;
        _stages.OnEnter = stage => { if (stage == Commit) projection.Deactivate(Tenant, PackKey, "1.0.0"); };

        var outcome = await installer.DeactivateAsync(Context(), PackKey, "1.0.0");

        Assert.False(outcome.Deactivated);
        Assert.Equal(PackInstallCodes.DeactivateNotActive, outcome.Error);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
        Assert.Null(projection.GetActive(Tenant, PackKey));
        Assert.Equal(PackInstallCodes.DeactivateNotActive, AssertSingleDeactivationRefusal(before).Detail);
        Assert.Empty(((IPackProjectionAdmissionStore)projection).ListIncompleteProjectionAdmissions());
    }

    [Fact(DisplayName = "ck-10 S5b: a dependent activated between validate and commit refuses the deactivation at commit, naming it, with both packs active")]
    public async Task A_deactivation_raced_by_a_dependent_activation_is_refused_at_commit()
    {
        await ActivateAsync();
        const string dependent = "test.s5b-dependent";
        _store.Commit(new PackInstallTransaction(Tenant,
            new InstalledPack(dependent, "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Draft, [],
                new Dictionary<string, int>(), Now, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
                TrustScope.OwnRoster, [new PackDependencyRef(PackKey, "1.0.0")]),
            new PackInstallWatermark(dependent, "1.0.0", new Dictionary<string, int>()), []));
        var before = _audit.Query(Tenant).Count;
        var admissions = Admissions(_store);
        _stages.OnEnter = stage => { if (stage == Commit) _store.Activate(Tenant, dependent, "1.0.0"); };

        var outcome = await _installer.DeactivateAsync(Context(), PackKey, "1.0.0");

        Assert.False(outcome.Deactivated);
        Assert.Equal(PackInstallCodes.DeactivateDependentsActive, outcome.Error);
        Assert.Equal([dependent], outcome.Dependents!);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, PackKey)?.Version);
        Assert.Equal("1.0.0", _store.GetActive(Tenant, dependent)?.Version);
        Assert.Equal($"{PackInstallCodes.DeactivateDependentsActive}: {dependent}", AssertSingleDeactivationRefusal(before).Detail);
        Assert.Equal(admissions, Admissions(_store));
    }

    private static Guid[] Admissions(IPackProjectionAdmissionStore store) =>
        store.ListIncompleteProjectionAdmissions().Select(admission => admission.AdmissionId).ToArray();

    private PackInstallAuditEntry AssertSingleDeactivationRefusal(int before)
    {
        var entries = _audit.Query(Tenant).Skip(before).ToArray();
        Assert.DoesNotContain(entries, entry => entry.Action == PackInstallAuditAction.Deactivated);
        var refusal = Assert.Single(entries);
        Assert.Equal(PackInstallAuditAction.Refused, refusal.Action);
        Assert.False(refusal.PreDecision);
        Assert.Equal(PackKey, refusal.PackKey);
        Assert.Equal("1.0.0", refusal.Version);
        Assert.Equal("test-operator", refusal.ActingPrincipal);
        return refusal;
    }

    private async Task ActivateAsync()
    {
        PlatformPackTestPreload.Activate(_store, Tenant);
        Assert.True((await _installer.ActivateAsync(Context(), PackKey, "1.0.0")).Activated);
        _stages.Entered.Clear();
    }

    private static AuthorizationDecision Decision() =>
        TestAuthorization.AllowedDecision(Tenant, PackKey, "pack", Permission.PackagesOperate, at: Now);

    private static PackInstallContext Context(
        string principal = "test-operator", IReadOnlyDictionary<string, string>? ownership = null) => new(
        Tenant, new InMemoryPackTrustStore([]), PackRevocationList.Empty, Now, TimeSpan.FromHours(1),
        Principal: principal, OwnershipResolutions: ownership);

    private void SeedPack() => CommitPack(_store, PackKey, "1.0.0", Item(ContentKey, PackContentKind.FormDefinition, Seed));

    private static PackSeedItem Item(string key, PackContentKind kind, string json) =>
        new(key, kind, "1.0.0", json, Harborline.Api.Foundation.Blobs.Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(json)));

    private static void CommitPack(IPackInstallMutationStore store, string packKey, string version, params PackSeedItem[] items) =>
        store.Commit(new PackInstallTransaction(Tenant,
            new InstalledPack(packKey, version, PackScopeTier.Horizontal, PackLifecycleState.Draft, items,
                new Dictionary<string, int>(), Now, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
                TrustScope.OwnRoster, [new PackDependencyRef(PlatformPackTestPreload.PackKey, PlatformPackTestPreload.Version)]),
            new PackInstallWatermark(packKey, version, new Dictionary<string, int>()), []));

    private const string ReportPackKey = "test.s5b-report";
    private const string ReportKey = "s5b.report";
    private const string ItemVersion = "1.0.0";

    private static PackInstaller Installer(
        IPackInstallStore store, IPackInstallAudit audit, IWritePipelineObserver observer, IPackContentAdmission? admission = null) =>
        new(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), store,
            admission ?? new WorkflowRefusingPackContentAdmission(), audit, TestAuthorization.AllowGate(), pipelineObserver: observer);

    private PackInstaller SplitInstaller(IPackInstallMutationStore mutations, IPackProjectionAdmissionStore projection) =>
        new(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), _store, mutations, projection,
            new WorkflowRefusingPackContentAdmission(), _audit, TestAuthorization.AllowGate(), pipelineObserver: _stages);

    private const string CascadePackKey = "test.s5b-cascade";

    private static AuthorizationDecision CascadeDecision() =>
        TestAuthorization.AllowedDecision(Tenant, CascadePackKey, "pack", Permission.PackagesOperate, at: Now);

    /// <summary>Two cascade-defaults items and a form, active, with a stored override on the second cascade item.</summary>
    private RecordingAdmission SeedCascadePack()
    {
        PlatformPackTestPreload.Activate(_store, Tenant);
        CommitPack(_store, CascadePackKey, "1.0.0",
            Item("cascade-a", PackContentKind.CascadeDefaults, Seed),
            Item("cascade-b", PackContentKind.CascadeDefaults, """{"tone":"formal","size":"large"}"""),
            Item("form-c", PackContentKind.FormDefinition, Seed));
        _store.Activate(Tenant, CascadePackKey, "1.0.0");
        _store.SaveOverride(Tenant, CascadePackKey, new PackTenantOverride("cascade-b", JsonNode.Parse("""{"size":null}""")!));
        return new RecordingAdmission();
    }

    private sealed class RecordingAdmission : IPackContentAdmission
    {
        public List<PackComposedItem[]> Admitted { get; } = [];

        public PackAdmissionResult Admit(IReadOnlyList<PackComposedItem> composed, TenantId tenant)
        {
            Admitted.Add(composed.ToArray());
            return PackAdmissionResult.Admissible;
        }
    }

    private sealed class RefusedProjection : IPackProjectionRefusalReport
    {
        public bool ProjectionRefused => true;
    }

    private sealed class ProjectorDouble(Func<object?> result, bool observerFails = false) : IPackProjectionDispatcher
    {
        public Func<object?> Result { get; set; } = result;

        public void StageProjection(PackProjectionTransaction transaction)
        {
            if (observerFails) transaction.AfterCommit(() => throw new InvalidOperationException("observer down"));
        }

        public object? Project(PackProjectionAuthority authority, CancellationToken cancellationToken = default) => Result();
    }

    /// <summary>Records into the shared audit, failing only the post-commit Activated entry when asked.</summary>
    private sealed class ActivatedAuditDouble(IPackInstallAudit inner, bool fails) : IPackInstallAudit
    {
        public void Append(PackInstallAuditEntry entry) => inner.Append(entry);

        public void AppendAuthorized(PackInstallAuditEntry entry, AuthorizationDecision decision)
        {
            if (fails && entry.Action == PackInstallAuditAction.Activated) throw new InvalidOperationException("audit down");
            inner.AppendAuthorized(entry, decision);
        }

        public IReadOnlyList<PackInstallAuditEntry> Query(TenantId tenant) => inner.Query(tenant);
    }

    private static InMemoryReportDefinitionRegistry Reports() => new(new AcceptAllReports());

    private static PackSeedProjector Projector(IPackInstallStore store, InMemoryReportDefinitionRegistry reports) =>
        new(store,
            new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider()
                .GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance, reportDefinitions: reports, time: TimeProvider.System);

    private static void CommitReportPack(IPackInstallMutationStore store)
    {
        var json = JsonSerializer.Serialize(new ReportDefinition
        {
            Key = ReportKey,
            Version = ItemVersion,
            Tenant = Tenant.Value,
            SchemaVersion = 1,
            ReportKind = "reports.table/basic",
            Title = "S5b report",
            Parameters = JsonSerializer.SerializeToElement(new { groupBy = "person" }),
            CascadeLayer = CascadeLayer.Tenant,
            Provenance = JsonDocument.Parse(
                $$"""{"derivedAt":"2026-09-07T12:00:00+00:00","exportingActor":"s5b-test-author","originPackKey":"{{ReportPackKey}}","tier":"Civilian"}""")
                .RootElement.Clone(),
        });
        var item = new PackSeedItem(ReportKey, PackContentKind.ReportDefinition, ItemVersion, json,
            Harborline.Api.Foundation.Blobs.Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(json)));
        store.Commit(new PackInstallTransaction(Tenant,
            new InstalledPack(ReportPackKey, "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Draft, [item],
                new Dictionary<string, int>(), Now, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
                TrustScope.OwnRoster, [new PackDependencyRef(PlatformPackTestPreload.PackKey, PlatformPackTestPreload.Version)]),
            new PackInstallWatermark(ReportPackKey, "1.0.0", new Dictionary<string, int>()), []));
    }

    private sealed class AcceptAllReports : IReportDefinitionDescriptorRegistry
    {
        public ValueTask AdmitAsync(ReportDefinition definition, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

#pragma warning disable CA1032, CA1064 // A test-only stand-in for process death.
    private sealed class SimulatedCrash : Exception;
#pragma warning restore CA1032, CA1064

    private sealed class StageRecorder : IWritePipelineObserver
    {
        public List<WritePipelineStage> Entered { get; } = [];

        public Action<WritePipelineStage>? OnEnter { get; set; }

        public void OnStage(WritePipelineStage stage)
        {
            Entered.Add(stage);
            OnEnter?.Invoke(stage);
        }
    }
}
