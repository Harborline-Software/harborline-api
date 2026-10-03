using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

using static Harborline.Api.Kernel.Runtime.WritePipelineStage;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// ck-10 S5c (DES-0029): pack install and the startup projection reconcile each run the six ADR 0038 stages
/// through <see cref="WritePipeline.RunAsync"/>. Every case asserts the stages the real installer entered and
/// the store's state, not only the returned outcome.
/// </summary>
public sealed class PackInstallPipelineTests : IDisposable
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-0000000005c5");
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string PackKey = "test.s5c";

    private readonly KeyPair _keys = KeyPair.Generate();
    private readonly PackFileCodec _codec = new();
    private readonly InMemoryPackInstallStore _store = new();
    private readonly InMemoryPackInstallAudit _audit = new();
    private readonly StageRecorder _stages = new();
    private readonly CountingProjector _projector = new();
    private bool _allow = true;
    private readonly PackInstaller _installer;

    public PackInstallPipelineTests()
    {
        PlatformPackTestPreload.Activate(_store, Tenant);
        _installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), _codec), _store,
            new WorkflowRefusingPackContentAdmission(), _audit,
            TestAuthorization.Gate(_ => _allow), _projector, pipelineObserver: _stages);
    }

    public void Dispose() => _keys.Dispose();

    [Fact]
    public async Task A_valid_install_runs_the_six_stages_and_the_version_is_installed()
    {
        var outcome = await _installer.InstallAsync(await PackAsync("1.0.0"), Context());

        Assert.True(outcome.Installed, string.Join(",", outcome.RefusalCodes));
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Equal(PackLifecycleState.Draft, _store.GetVersion(Tenant, PackKey, "1.0.0")?.Lifecycle);
        Assert.Equal("1.0.0", _store.GetWatermark(Tenant, PackKey)?.Version);
        Assert.Contains(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Installed && entry.PackKey == PackKey);
    }

    [Fact]
    public async Task A_refused_install_decision_stops_at_authorize_and_nothing_is_installed()
    {
        var bytes = await PackAsync("1.0.0");
        _allow = false;

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => _installer.InstallAsync(bytes, Context()));

        Assert.Equal([Authorize], _stages.Entered);
        Assert.Null(_store.GetVersion(Tenant, PackKey, "1.0.0"));
        Assert.Null(_store.GetWatermark(Tenant, PackKey));
        Assert.Contains(_audit.Query(Tenant), entry => entry.PreDecision && entry.Detail == "pack.install.refused.authorization_denied");
    }

    [Fact]
    public async Task An_artifact_that_does_not_verify_stops_at_validate_and_nothing_is_installed()
    {
        var tampered = await PackAsync("1.0.0");
        tampered[^1] ^= 0xFF;

        var outcome = await _installer.InstallAsync(tampered, Context());

        Assert.False(outcome.Installed);
        Assert.Equal(["pack.install.refused.not_verified"], outcome.RefusalCodes);
        Assert.Equal([Authorize, Bind, Mutate, Validate], _stages.Entered);
        Assert.Null(_store.GetVersion(Tenant, PackKey, "1.0.0"));
        Assert.Null(_store.GetWatermark(Tenant, PackKey));
        Assert.Contains(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Refused
            && entry.Detail == "pack.install.refused.not_verified");
    }

    [Fact]
    public async Task A_watermark_refusal_stops_at_validate_and_the_break_glass_ceremony_then_installs_with_its_own_audit_entry()
    {
        Assert.True((await _installer.InstallAsync(await PackAsync("2.0.0"), Context())).Installed);
        var downgrade = await PackAsync("1.0.0");
        _stages.Entered.Clear();

        var refused = await _installer.InstallAsync(downgrade, Context());

        Assert.False(refused.Installed);
        Assert.Equal(PackInstallVerdict.RequiresBreakGlass, refused.Preview.Verdict);
        Assert.False(refused.BrokeGlass);
        Assert.Contains("pack.install.refused.downgrade", refused.RefusalCodes);
        Assert.Equal([Authorize, Bind, Mutate, Validate], _stages.Entered);
        Assert.Null(_store.GetVersion(Tenant, PackKey, "1.0.0"));
        Assert.DoesNotContain(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.BreakGlassOverride);

        // Recovery: the same install under the explicit ceremony commits, with its distinct audit entry.
        _stages.Entered.Clear();
        var brokeGlass = await _installer.InstallAsync(downgrade, Context() with
        {
            BreakGlass = new BreakGlass("s5c downgrade exercise", "test-authorizer"),
        });

        Assert.True(brokeGlass.Installed, string.Join(",", brokeGlass.RefusalCodes));
        Assert.True(brokeGlass.BrokeGlass);
        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.NotNull(_store.GetVersion(Tenant, PackKey, "1.0.0"));
        Assert.Equal("2.0.0", _store.GetWatermark(Tenant, PackKey)?.Version);
        var ceremony = Assert.Single(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.BreakGlassOverride);
        Assert.Equal("s5c downgrade exercise", ceremony.BreakGlassJustification);
        Assert.Equal("test-authorizer", ceremony.BreakGlassAuthorizingPrincipal);
    }

    [Fact]
    public async Task A_reconcile_of_a_pending_projection_runs_the_six_stages_and_marks_it_completed()
    {
        Assert.True((await _installer.InstallAsync(await PackAsync("1.0.0"), Context())).Installed);
        Assert.True((await _installer.ActivateAsync(Context(), PackKey, "1.0.0")).Activated);
        // The deactivation's projection is refused, so its admission stays pending for the next boot.
        _projector.Refuse = true;
        Assert.True((await _installer.DeactivateAsync(Context(), PackKey, "1.0.0")).Deactivated);
        var pending = Assert.Single(Admissions().ListIncompleteProjectionAdmissions());
        Assert.Equal(PackKey, pending.PackId);
        _projector.Refuse = false;
        var projections = _projector.Projections;
        _stages.Entered.Clear();

        await ((IPackProjectionReconciler)_installer).ReconcilePendingAsync();

        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Equal(projections + 1, _projector.Projections);
        Assert.Empty(Admissions().ListIncompleteProjectionAdmissions());
    }

    [Fact]
    public async Task A_reconcile_with_nothing_pending_writes_nothing()
    {
        var installed = _store.ListInstalled(Tenant);
        var audited = _audit.Query(Tenant).Count;

        await ((IPackProjectionReconciler)_installer).ReconcilePendingAsync();

        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], _stages.Entered);
        Assert.Equal(0, _projector.Projections);
        Assert.Empty(Admissions().ListIncompleteProjectionAdmissions());
        Assert.Equal(installed, _store.ListInstalled(Tenant));
        Assert.Equal(audited, _audit.Query(Tenant).Count);
    }

    [Fact(DisplayName = "ck-10 S5c: a pending admission survives a durable store restart, and a new installer's reconcile completes it and makes the definitions live")]
    public async Task A_pending_admission_is_completed_by_a_new_installer_over_the_restarted_durable_store()
    {
        // The registry stands in for the durable definition store, which outlives the process.
        var reports = PackActivationPipelineTests.Reports();
        await using var origin = await PacksTestStore.CreateAsync();
        {
            // The first process records an activation and its projection admission, then dies before projecting.
            var store = new DurablePackInstallStore(origin.Factory);
            PlatformPackTestPreload.Activate(store, Tenant);
            PackActivationPipelineTests.CommitReportPack(store, Tenant);
            ((IPackProjectionAdmissionStore)store).ActivateAndRecordProjectionAdmission(
                Tenant, PackActivationPipelineTests.ReportPackKey, "1.0.0",
                new PackProjectionAdmission(Guid.NewGuid(), PackActivationPipelineTests.ReportPackKey, "1.0.0", Tenant,
                    new ActorId("test-operator"), Now, ["s5c-derivation"], Projected: false));
        }
        Assert.Null(await reports.GetDefinitionAsync(Tenant.Value, PackActivationPipelineTests.ReportKey, PackActivationPipelineTests.ItemVersion));

        await using var restart = PacksTestStore.Reopen(origin);
        var reopened = new DurablePackInstallStore(restart.Factory);
        var audit = new InMemoryPackInstallAudit();
        var stages = new StageRecorder();
        var reconciler = (IPackProjectionReconciler)PackActivationPipelineTests.Installer(reopened, audit, stages);
        reconciler.AttachProjector(PackActivationPipelineTests.Projector(reopened, reports));
        Assert.Contains(((IPackProjectionAdmissionStore)reopened).ListIncompleteProjectionAdmissions(),
            admission => admission.PackId == PackActivationPipelineTests.ReportPackKey);

        await reconciler.ReconcilePendingAsync();

        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], stages.Entered);
        Assert.Empty(((IPackProjectionAdmissionStore)reopened).ListIncompleteProjectionAdmissions());
        var live = await reports.GetDefinitionAsync(Tenant.Value, PackActivationPipelineTests.ReportKey, PackActivationPipelineTests.ItemVersion);
        Assert.NotNull(live);

        // A second reconcile has nothing pending and writes nothing: same installed rows, no audit, same definition.
        var installed = System.Text.Json.JsonSerializer.Serialize(reopened.ListInstalled(Tenant));
        stages.Entered.Clear();

        await reconciler.ReconcilePendingAsync();

        Assert.Equal([Authorize, Bind, Mutate, Validate, Commit, React], stages.Entered);
        Assert.Empty(((IPackProjectionAdmissionStore)reopened).ListIncompleteProjectionAdmissions());
        Assert.Equal(installed, System.Text.Json.JsonSerializer.Serialize(reopened.ListInstalled(Tenant)));
        Assert.Empty(audit.Query(Tenant));
        Assert.Same(live, await reports.GetDefinitionAsync(Tenant.Value, PackActivationPipelineTests.ReportKey, PackActivationPipelineTests.ItemVersion));
    }

    [Fact]
    public async Task A_first_install_audits_installed_and_a_newer_version_audits_upgraded()
    {
        Assert.True((await _installer.InstallAsync(await PackAsync("1.0.0"), Context())).Installed);
        var upgrade = await _installer.InstallAsync(await PackAsync("2.0.0"), Context());

        Assert.True(upgrade.Installed, string.Join(",", upgrade.RefusalCodes));
        var entries = _audit.Query(Tenant).Where(entry => entry.PackKey == PackKey).ToList();
        var installed = Assert.Single(entries, entry => entry.Version == "1.0.0");
        Assert.Equal(PackInstallAuditAction.Installed, installed.Action);
        Assert.Equal("pack.install.installed", installed.Detail);
        var upgraded = Assert.Single(entries, entry => entry.Version == "2.0.0");
        Assert.Equal(PackInstallAuditAction.Upgraded, upgraded.Action);
        Assert.Equal("pack.install.upgraded", upgraded.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_concurrent_install_refuses_a_stale_watermark_before_committing(bool durable)
    {
        await using var database = durable ? await PacksTestStore.CreateAsync() : null;
        IPackInstallStore store = durable ? new DurablePackInstallStore(database!.Factory) : new InMemoryPackInstallStore();
        PlatformPackTestPreload.Activate((IPackInstallMutationStore)store, Tenant);
        var audit = new InMemoryPackInstallAudit();
        var verifier = new PackVerifier(new Ed25519Verifier(), _codec);
        var newer = new PackInstaller(verifier, store, new WorkflowRefusingPackContentAdmission(), audit,
            TestAuthorization.Gate(_ => true));
        Assert.True((await newer.InstallAsync(await PackAsync("1.0.0"), Context())).Installed);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new PackInstaller(new PausedVerifier(verifier, entered, release), store,
            new WorkflowRefusingPackContentAdmission(), audit, TestAuthorization.Gate(_ => true));
        var lowerBytes = await PackAsync("2.0.0");
        var higherBytes = await PackAsync("3.0.0");
        var lowerTask = Task.Run(() => delayed.InstallAsync(lowerBytes, Context()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True((await newer.InstallAsync(higherBytes, Context())).Installed);
        }
        finally { release.Set(); }
        var lower = await lowerTask.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(lower.Installed);
        Assert.False(lower.BrokeGlass);
        Assert.Equal(["pack.install.refused.watermark_changed"], lower.RefusalCodes);
        Assert.Equal("3.0.0", store.GetWatermark(Tenant, PackKey)!.Version);
        Assert.Null(store.GetVersion(Tenant, PackKey, "2.0.0"));
        Assert.DoesNotContain(audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.BreakGlassOverride);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_different_key_install_invalidates_tenant_admission_snapshot_before_commit(bool durable)
    {
        await using var database = durable ? await PacksTestStore.CreateAsync() : null;
        IPackInstallStore store = durable ? new DurablePackInstallStore(database!.Factory) : new InMemoryPackInstallStore();
        PlatformPackTestPreload.Activate((IPackInstallMutationStore)store, Tenant);
        var audit = new InMemoryPackInstallAudit();
        var verifier = new PackVerifier(new Ed25519Verifier(), _codec);
        var immediate = new PackInstaller(verifier, store, new WorkflowRefusingPackContentAdmission(), audit,
            TestAuthorization.Gate(_ => true));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new PackInstaller(new PausedVerifier(verifier, entered, release), store,
            new WorkflowRefusingPackContentAdmission(), audit, TestAuthorization.Gate(_ => true));
        var candidate = await PackAsync("1.0.0");
        var other = await PackAsync("1.0.0", "test.other", "other-form");
        var pending = Task.Run(() => delayed.InstallAsync(candidate, Context()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True((await immediate.InstallAsync(other, Context())).Installed);
        }
        finally { release.Set(); }
        var refused = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(refused.Installed);
        Assert.False(refused.BrokeGlass);
        Assert.Equal(["pack.install.refused.installed_state_changed"], refused.RefusalCodes);
        Assert.Null(store.GetVersion(Tenant, "test.s5c", "1.0.0"));
        Assert.Null(store.GetWatermark(Tenant, "test.s5c"));
        Assert.NotNull(store.GetVersion(Tenant, "test.other", "1.0.0"));
        Assert.Equal("1.0.0", store.GetWatermark(Tenant, "test.other")!.Version);
        Assert.DoesNotContain(audit.Query(Tenant), row => row.PackKey == "test.s5c"
            && row.Action == PackInstallAuditAction.Installed);
        // A fresh bind against the unchanged new state remains admissible: refusal requires retry, not break-glass.
        Assert.True((await immediate.InstallAsync(candidate, Context())).Installed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_concurrent_admission_input_change_refuses_install_without_losing_the_write(bool durable, bool ownership)
    {
        await using var database = durable ? await PacksTestStore.CreateAsync() : null;
        IPackInstallStore store = durable ? new DurablePackInstallStore(database!.Factory) : new InMemoryPackInstallStore();
        var mutations = (IPackInstallMutationStore)store;
        PlatformPackTestPreload.Activate(mutations, Tenant);
        var audit = new InMemoryPackInstallAudit();
        var verifier = new PackVerifier(new Ed25519Verifier(), _codec);
        var immediate = new PackInstaller(verifier, store, new WorkflowRefusingPackContentAdmission(), audit,
            TestAuthorization.Gate(_ => true));
        Assert.True((await immediate.InstallAsync(await PackAsync("1.0.0"), Context())).Installed);
        Assert.True((await immediate.ActivateAsync(Context(), PackKey, "1.0.0")).Activated);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new PackInstaller(verifier, store,
            new WorkflowRefusingPackContentAdmission(), audit, TestAuthorization.Gate(_ => true),
            pipelineObserver: new PausedCommitObserver(entered, release));
        var candidate = await PackAsync("2.0.0");
        var pending = Task.Run(() => delayed.InstallAsync(candidate, Context()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (ownership) mutations.RecordKeyOwnership(Tenant, "s5c-form", PackKey);
            else
            {
                var decision = TestAuthorization.AllowedDecision(Tenant, PackKey, "pack", Permission.PackagesOperate, at: Now);
                Assert.True((await immediate.NarrowAsync(Context(), PackKey, "s5c-form",
                    new JsonObject { ["title"] = null }, decision)).Recorded);
            }
        }
        finally { release.Set(); }
        var refused = await pending.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(refused.Installed);
        Assert.False(refused.BrokeGlass);
        Assert.Equal(["pack.install.refused.installed_state_changed"], refused.RefusalCodes);
        Assert.Null(store.GetVersion(Tenant, PackKey, "2.0.0"));
        Assert.Equal("1.0.0", store.GetWatermark(Tenant, PackKey)!.Version);
        if (ownership) Assert.Equal("test.s5c", store.GetKeyOwnership(Tenant)["s5c-form"]);
        else Assert.Equal("{\"title\":null}", Assert.Single(store.GetOverrides(Tenant, PackKey)).OverlayPatch.ToJsonString());
        Assert.DoesNotContain(audit.Query(Tenant), row => row.Version == "2.0.0"
            && row.Action is PackInstallAuditAction.Upgraded or PackInstallAuditAction.BreakGlassOverride);
        Assert.True((await immediate.InstallAsync(candidate, Context())).Installed);
        if (!ownership) Assert.Equal("{\"title\":null}", Assert.Single(store.GetOverrides(Tenant, PackKey)).OverlayPatch.ToJsonString());
    }

    private sealed class PausedCommitObserver(TaskCompletionSource entered, ManualResetEventSlim release) : IWritePipelineObserver
    {
        public void OnStage(WritePipelineStage stage)
        {
            if (stage != Commit) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("The concurrent admission write did not release commit.");
        }
    }

    private sealed class PausedVerifier(IPackVerifier inner, TaskCompletionSource entered, ManualResetEventSlim release) : IPackVerifier
    {
        public PackVerificationResult Verify(ReadOnlySpan<byte> bytes, IPackTrustStore trustStore)
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("The concurrent install did not release verification.");
            return inner.Verify(bytes, trustStore);
        }
    }

    [Fact]
    public async Task A_blank_principal_is_refused_at_authorize_before_any_write()
    {
        var bytes = await PackAsync("1.0.0");

        var thrown = await Assert.ThrowsAsync<ArgumentException>(() => _installer.InstallAsync(bytes, Context() with { Principal = "  " }));

        Assert.Equal("context.Principal", thrown.ParamName);
        Assert.Equal([Authorize], _stages.Entered);
        Assert.Null(_store.GetVersion(Tenant, PackKey, "1.0.0"));
        Assert.Null(_store.GetWatermark(Tenant, PackKey));
        var refusal = Assert.Single(_audit.Query(Tenant), entry => entry.PackKey == PackKey);
        Assert.True(refusal.PreDecision);
        Assert.Equal("pack.install.refused.no_principal", refusal.Detail);
    }

    [Fact]
    public async Task An_empty_correlation_id_is_refused_before_the_decision_and_nothing_is_installed()
    {
        var bytes = await PackAsync("1.0.0");

        var thrown = await Assert.ThrowsAsync<ArgumentException>(() => _installer.InstallAsync(bytes, Context() with { CorrelationId = Guid.Empty }));

        Assert.Equal("correlationId", thrown.ParamName);
        Assert.Equal([Authorize], _stages.Entered);
        Assert.Null(_store.GetVersion(Tenant, PackKey, "1.0.0"));
        Assert.Null(_store.GetWatermark(Tenant, PackKey));
    }

    [Fact]
    public async Task Null_arguments_to_the_public_writes_throw_before_any_stage_runs()
    {
        var bytes = await PackAsync("1.0.0");
        var decision = TestAuthorization.AllowedDecision(Tenant, PackKey, "pack", Permission.PackagesOperate, at: Now);
        var patch = new JsonObject { ["title"] = null };

        Assert.Equal("context", (await Assert.ThrowsAsync<ArgumentNullException>(() => _installer.InstallAsync(bytes, null!))).ParamName);
        Assert.Equal("context", (await Assert.ThrowsAsync<ArgumentNullException>(() => _installer.ActivateAsync(null!, PackKey, "1.0.0"))).ParamName);
        Assert.Equal("context", (await Assert.ThrowsAsync<ArgumentNullException>(() => _installer.DeactivateAsync(null!, PackKey, "1.0.0"))).ParamName);
        Assert.Equal("context", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _installer.NarrowAsync(null!, PackKey, "s5c-form", patch, decision))).ParamName);
        Assert.Equal("overlayPatch", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _installer.NarrowAsync(Context(), PackKey, "s5c-form", null!, decision))).ParamName);
        Assert.Equal("decision", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _installer.NarrowAsync(Context(), PackKey, "s5c-form", patch, null!))).ParamName);

        Assert.Empty(_stages.Entered);
        Assert.Null(_store.GetVersion(Tenant, PackKey, "1.0.0"));
    }

    [Fact]
    public async Task A_reconcile_with_no_projector_composed_fails_at_authorize_and_writes_nothing()
    {
        await PendTwoAdmissionsAsync();
        var pending = PendingIds();
        var stages = new StageRecorder();
        var uncomposed = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), _codec), _store,
            new WorkflowRefusingPackContentAdmission(), _audit,
            TestAuthorization.Gate(_ => true), pipelineObserver: stages);
        var projections = _projector.Projections;
        var audited = _audit.Query(Tenant).Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => ((IPackProjectionReconciler)uncomposed).ReconcilePendingAsync());

        Assert.Equal([Authorize], stages.Entered);
        Assert.Equal(projections, _projector.Projections);
        Assert.Equal(pending, PendingIds());
        Assert.Equal(audited, _audit.Query(Tenant).Count);
    }

    [Fact]
    public async Task A_reconcile_completes_every_pending_admission_and_retires_each_replayed_authority()
    {
        await PendTwoAdmissionsAsync();
        Assert.Equal(2, PendingIds().Count);
        _projector.Authorities.Clear();
        // Each replayed authority is usable while it is projected.
        _projector.OnProject = authority => authority.EnsureUsable();

        await ((IPackProjectionReconciler)_installer).ReconcilePendingAsync();

        Assert.Empty(PendingIds());
        Assert.Equal(2, _projector.Authorities.Count);
        foreach (var authority in _projector.Authorities)
        {
            var replayed = Assert.Throws<PackProjectionAuthorityException>(authority.EnsureUsable);
            Assert.Equal("pack.projection.authority.replayed", replayed.Code);
        }
    }

    [Fact]
    public async Task Cancellation_during_a_reconcile_stops_before_the_next_admission_and_leaves_it_pending()
    {
        await PendTwoAdmissionsAsync();
        var pending = PendingIds();
        Assert.Equal(2, pending.Count);
        _projector.Authorities.Clear();
        using var cancellation = new CancellationTokenSource();
        // The first projection requests cancellation; the pass must not project the second admission.
        _projector.OnProject = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((IPackProjectionReconciler)_installer).ReconcilePendingAsync(cancellation.Token));

        Assert.Single(_projector.Authorities);
        Assert.Equal([pending[1]], PendingIds());
    }

    [Fact]
    public async Task Cancellation_requested_before_a_reconcile_stops_it_at_authorize_with_nothing_completed()
    {
        await PendTwoAdmissionsAsync();
        var pending = PendingIds();
        var projections = _projector.Projections;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((IPackProjectionReconciler)_installer).ReconcilePendingAsync(new CancellationToken(canceled: true)));

        Assert.Equal([Authorize], _stages.Entered);
        Assert.Equal(projections, _projector.Projections);
        Assert.Equal(pending, PendingIds());
    }

    [Fact]
    public async Task A_deactivation_projection_that_throws_is_carried_on_the_outcome_and_the_admission_stays_pending()
    {
        Assert.True((await _installer.InstallAsync(await PackAsync("1.0.0"), Context())).Installed);
        Assert.True((await _installer.ActivateAsync(Context(), PackKey, "1.0.0")).Activated);
        Assert.Empty(PendingIds());
        var failure = new InvalidOperationException("s5c projector failure");
        _projector.OnProject = _ => throw failure;

        var outcome = await _installer.DeactivateAsync(Context(), PackKey, "1.0.0");

        Assert.True(outcome.Deactivated);
        Assert.False(outcome.Projected);
        Assert.Same(failure, outcome.ProjectionResult);
        Assert.Equal(PackKey, Assert.Single(Admissions().ListIncompleteProjectionAdmissions()).PackId);
    }

    [Fact]
    public async Task A_deactivation_projection_that_refuses_reports_the_refusal_and_the_admission_stays_pending()
    {
        Assert.True((await _installer.InstallAsync(await PackAsync("1.0.0"), Context())).Installed);
        Assert.True((await _installer.ActivateAsync(Context(), PackKey, "1.0.0")).Activated);
        _projector.Refuse = true;

        var outcome = await _installer.DeactivateAsync(Context(), PackKey, "1.0.0");

        Assert.True(outcome.Deactivated);
        Assert.True(outcome.Projected);
        Assert.True(Assert.IsAssignableFrom<IPackProjectionRefusalReport>(outcome.ProjectionResult).ProjectionRefused);
        Assert.Equal(PackKey, Assert.Single(Admissions().ListIncompleteProjectionAdmissions()).PackId);
    }

    /// <summary>Two deactivations whose projections are refused leave two admissions pending.</summary>
    private async Task PendTwoAdmissionsAsync()
    {
        Assert.True((await _installer.InstallAsync(await PackAsync("1.0.0"), Context())).Installed);
        for (var round = 0; round < 2; round++)
        {
            _projector.Refuse = false;
            Assert.True((await _installer.ActivateAsync(Context(), PackKey, "1.0.0")).Activated);
            _projector.Refuse = true;
            Assert.True((await _installer.DeactivateAsync(Context(), PackKey, "1.0.0")).Deactivated);
        }
        _projector.Refuse = false;
        _stages.Entered.Clear();
    }

    private List<Guid> PendingIds() =>
        Admissions().ListIncompleteProjectionAdmissions().Select(admission => admission.AdmissionId).ToList();

    private IPackProjectionAdmissionStore Admissions() => _store;

    private PackInstallContext Context() => new(
        Tenant,
        new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, _keys.PrincipalId, 1, TrustRootStatus.Current)]),
        PackRevocationList.Empty, Now, TimeSpan.FromDays(30), Principal: "test-operator");

    private async Task<byte[]> PackAsync(string version, string packKey = PackKey, string contentKey = "s5c-form")
    {
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            _codec,
            TimeProvider.System);
        var export = await exporter.ExportAsync(new PackExportRequest(
            packKey, version, "S5c install pipeline", "Install pipeline fixture",
            PackScopeTier.Horizontal,
            [new PackContentSource(contentKey, PackContentKind.FormDefinition, version, new JsonObject { ["title"] = "s5c" })],
            Array.Empty<PackDependencyRef>(),
            Array.Empty<string>(),
            1,
            Dcp: DomainComplianceProfile.General("s5c-author")), new Ed25519Signer(_keys));
        Assert.True(export.Succeeded, string.Join("; ", export.Validation.Errors.Select(error => error.Code)));
        return export.FileBytes!;
    }

    private sealed class StageRecorder : IWritePipelineObserver
    {
        public List<WritePipelineStage> Entered { get; } = [];

        public void OnStage(WritePipelineStage stage) => Entered.Add(stage);
    }

    private sealed class CountingProjector : IPackProjectionDispatcher
    {
        public bool Refuse { get; set; }
        public int Projections { get; private set; }
        public Action<PackProjectionAuthority>? OnProject { get; set; }
        public List<PackProjectionAuthority> Authorities { get; } = [];

        public void StageProjection(PackProjectionTransaction transaction) { }

        public object? Project(PackProjectionAuthority authority, CancellationToken cancellationToken = default)
        {
            Projections++;
            Authorities.Add(authority);
            OnProject?.Invoke(authority);
            return new Report(Refuse);
        }

        private sealed record Report(bool ProjectionRefused) : IPackProjectionRefusalReport;
    }
}
