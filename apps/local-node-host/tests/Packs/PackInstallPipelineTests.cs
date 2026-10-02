using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
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
        Assert.Contains(_audit.Query(Tenant), entry => entry.PreDecision && entry.Detail == PackInstallCodes.RefusedAuthorizationDenied);
    }

    [Fact]
    public async Task An_artifact_that_does_not_verify_stops_at_validate_and_nothing_is_installed()
    {
        var tampered = await PackAsync("1.0.0");
        tampered[^1] ^= 0xFF;

        var outcome = await _installer.InstallAsync(tampered, Context());

        Assert.False(outcome.Installed);
        Assert.Equal([PackInstallCodes.RefusedNotVerified], outcome.RefusalCodes);
        Assert.Equal([Authorize, Bind, Mutate, Validate], _stages.Entered);
        Assert.Null(_store.GetVersion(Tenant, PackKey, "1.0.0"));
        Assert.Null(_store.GetWatermark(Tenant, PackKey));
        Assert.Contains(_audit.Query(Tenant), entry => entry.Action == PackInstallAuditAction.Refused
            && entry.Detail == PackInstallCodes.RefusedNotVerified);
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
        Assert.Contains(PackInstallCodes.RefusedDowngrade, refused.RefusalCodes);
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

    private IPackProjectionAdmissionStore Admissions() => _store;

    private PackInstallContext Context() => new(
        Tenant,
        new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, _keys.PrincipalId, 1, TrustRootStatus.Current)]),
        PackRevocationList.Empty, Now, TimeSpan.FromDays(30), Principal: "test-operator");

    private async Task<byte[]> PackAsync(string version)
    {
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            _codec,
            TimeProvider.System);
        var export = await exporter.ExportAsync(new PackExportRequest(
            PackKey, version, "S5c install pipeline", "Install pipeline fixture",
            PackScopeTier.Horizontal,
            [new PackContentSource("s5c-form", PackContentKind.FormDefinition, version, new JsonObject { ["title"] = "s5c" })],
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

        public void StageProjection(PackProjectionTransaction transaction) { }

        public object? Project(PackProjectionAuthority authority, CancellationToken cancellationToken = default)
        {
            Projections++;
            return new Report(Refuse);
        }

        private sealed record Report(bool ProjectionRefused) : IPackProjectionRefusalReport;
    }
}
