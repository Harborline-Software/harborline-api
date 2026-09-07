using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
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
using Harborline.Api.Foundation.ReportDefinitions;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Ticket 208 slice 2 (L633, L631) — REPLACEMENT REMOVES. Activating a newer version of an installed
/// pack supersedes the version it replaces; every definition the replaced version projected and the
/// replacement does not re-declare is removed inside the same projection admission, AFTER the
/// replacement is admitted and only when nothing was refused, so a replacement that cannot land leaves
/// the replaced package exactly as it was and its admission open for the next boot to re-run. A tuple the
/// replacement re-declares is left alone, which keeps an identical replacement a no-op rather than a
/// remove-and-republish. Unit-level over <see cref="PackSeedProjector"/> and the real
/// <see cref="PackInstaller"/> — view and report are the two kinds ticket 208 slice 6 lands on, and
/// they had a projection case but never a retraction case before this slice.
/// </summary>
public sealed class PackReplacementRemovalTests
{
    private const string PackKey = "harborline.access-administration";
    private const string ViewKey = "access.who-holds-what";
    private const string ReportKey = "access.held-by-person";
    private const string ItemVersion = "1.0.0";

    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000208");
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "208 s2: a replacement that omits a definition leaves no trace of it")]
    public async Task Replacement_That_Omits_A_Definition_Leaves_No_Trace()
    {
        var world = new World();
        await world.InstallAndActivateAsync("1.0.0", withView: true, withReport: true);
        var first = await world.ProjectAsync();
        Assert.Empty(first.Refusals);
        Assert.NotNull(await world.ReadViewAsync());
        Assert.NotNull(await world.ReadReportAsync());

        // The replacement ships the view only — the report is the surface it drops.
        await world.InstallAndActivateAsync("1.1.0", withView: true, withReport: false);
        var summary = await world.ProjectAsync();

        Assert.Empty(summary.Refusals);
        Assert.Null(await world.ReadReportAsync());               // catalogue read: gone.
        Assert.NotNull(await world.ReadViewAsync());              // the replacement's own surface stands.
        Assert.Equal(                                             // summary: removed, by kind.
            1, summary.RetractedByKind.GetValueOrDefault(PackContentKind.ReportDefinition));
        Assert.Equal(
            0, summary.RetractedByKind.GetValueOrDefault(PackContentKind.ViewDefinition));
    }

    [Fact(DisplayName = "208 s2: replacing with an identical pack is a no-op that keeps the same definition ids")]
    public async Task Identical_Replacement_Is_A_No_Op()
    {
        var world = new World();
        await world.InstallAndActivateAsync("1.0.0", withView: true, withReport: true);
        await world.ProjectAsync();
        var viewBefore = await world.ReadViewAsync();
        var reportBefore = await world.ReadReportAsync();

        await world.InstallAndActivateAsync("1.1.0", withView: true, withReport: true);
        var summary = await world.ProjectAsync();

        Assert.Empty(summary.Refusals);
        Assert.Empty(summary.RetractedByKind);
        // Same pinned (key, version) rows, same content: a withdraw-and-republish would have minted new
        // rows and the equality below is the only thing separating the two.
        var viewAfter = await world.ReadViewAsync();
        var reportAfter = await world.ReadReportAsync();
        Assert.Equal(viewBefore!.Key, viewAfter!.Key);
        Assert.Equal(viewBefore.Version, viewAfter.Version);
        Assert.Same(viewBefore, viewAfter);
        Assert.Equal(reportBefore!.Key, reportAfter!.Key);
        Assert.Equal(reportBefore.Version, reportAfter.Version);
        Assert.Same(reportBefore, reportAfter);
    }

    [Fact(DisplayName = "208 s2: deactivate then reactivate is a reversible round trip")]
    public async Task Deactivate_Then_Reactivate_Round_Trip_Is_Reversible()
    {
        var world = new World();
        await world.InstallAndActivateAsync("1.0.0", withView: true, withReport: true);
        await world.ProjectAsync();

        var deactivation = world.Installer.Deactivate(Tenant, PackKey, "1.0.0", Now, "test-operator");
        Assert.True(deactivation.Deactivated, deactivation.Error);
        var down = await world.ProjectAsync();
        Assert.Null(await world.ReadViewAsync());
        Assert.Null(await world.ReadReportAsync());
        Assert.Equal(1, down.RetractedByKind.GetValueOrDefault(PackContentKind.ViewDefinition));
        Assert.Equal(1, down.RetractedByKind.GetValueOrDefault(PackContentKind.ReportDefinition));

        var reactivation = world.Installer.Activate(Tenant, PackKey, "1.0.0", Now, "test-operator");
        Assert.True(reactivation.Activated, $"{reactivation.Error}: {reactivation.Detail}");
        var up = await world.ProjectAsync();

        Assert.Empty(up.Refusals);
        Assert.NotNull(await world.ReadViewAsync());
        Assert.NotNull(await world.ReadReportAsync());
    }

    [Fact(DisplayName = "208 s2: a crash mid-admit is repaired by the next boot")]
    public async Task Crash_Mid_Admit_Is_Repaired_By_The_Next_Boot()
    {
        var world = new World();
        await world.InstallAndActivateAsync("1.0.0", withView: false, withReport: true);
        await world.ProjectAsync();
        Assert.NotNull(await world.ReadReportAsync());

        // The replacement drops the report and ships the view. The node is torn down inside the pass,
        // while admitting the view — which now runs FIRST, so the replaced report is still standing when
        // the process dies. Either way the admission stays incomplete and the next boot re-runs the pass.
        world.Views.TearDownOnRegister = true;
        world.Install("1.1.0", withView: true, withReport: false);
        world.AttachProjector();
        Assert.ThrowsAny<OperationCanceledException>(
            () => world.Installer.Activate(world.Context, PackKey, "1.1.0"));

        Assert.NotNull(await world.ReadReportAsync());              // still standing: nothing was removed …
        Assert.Null(await world.ReadViewAsync());                   // … and the replacement never landed.
        Assert.NotEmpty(world.IncompleteAdmissions());              // the durable intent the boot completes.

        // Next boot: the process restart clears the projector's in-process replay guard (a crashed
        // authority is only replayable across a restart, which is exactly the case under test), then the
        // same reconciliation the host's startup hosted service runs re-runs the whole pass.
        world.Views.TearDownOnRegister = false;
        World.SimulateProcessRestart();
        world.Reconciler.ReconcilePending();

        Assert.NotNull(await world.ReadViewAsync());
        Assert.Null(await world.ReadReportAsync());
        Assert.Empty(world.IncompleteAdmissions());
    }

    [Fact(DisplayName = "208 s2: a refused replacement definition removes nothing and is not admitted")]
    public async Task Refused_Replacement_Removes_Nothing()
    {
        var world = new World();
        await world.InstallAndActivateAsync("1.0.0", withView: false, withReport: true);
        await world.ProjectAsync();
        var reportBefore = await world.ReadReportAsync();
        Assert.NotNull(reportBefore);

        // The replacement drops the report and ships a view the registry's governance refuses. A refusal
        // is a VALUE, not an exception: removal-first would have destroyed the report and still recorded a
        // COMPLETE admission, leaving nothing live and nothing to repair it.
        world.Views.RefuseOnRegister = true;
        world.Install("1.1.0", withView: true, withReport: false);
        world.AttachProjector();
        var activation = world.Installer.Activate(world.Context, PackKey, "1.1.0");
        Assert.True(activation.Activated, $"{activation.Error}: {activation.Detail}");

        var summary = Assert.IsType<PackSeedProjectionSummary>(activation.ProjectionResult);
        Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.ViewDefinition, summary.Refusals[0].ContentKind);
        Assert.Empty(summary.RetractedByKind);                      // zero removals.
        Assert.Same(reportBefore, await world.ReadReportAsync());   // the replaced package's copy, untouched.
        Assert.Null(await world.ReadViewAsync());
        Assert.NotEmpty(world.IncompleteAdmissions());              // refused, not complete.

        // The refusal is what holds the admission open: once the registry admits, the next boot's
        // reconciliation completes the same pass — the view lands and only then does the report go.
        world.Views.RefuseOnRegister = false;
        World.SimulateProcessRestart();
        world.Reconciler.ReconcilePending();

        Assert.NotNull(await world.ReadViewAsync());
        Assert.Null(await world.ReadReportAsync());
        Assert.Empty(world.IncompleteAdmissions());
    }

    [Fact(DisplayName = "208 s2: reversing a refused activation still removes nothing")]
    public async Task Reversed_Refused_Activation_Removes_Nothing()
    {
        var world = new World();
        await world.InstallAndActivateAsync("1.0.0", withView: false, withReport: true);
        await world.ProjectAsync();
        var reportBefore = await world.ReadReportAsync();
        Assert.NotNull(reportBefore);

        world.Views.RefuseOnRegister = true;
        world.Install("1.1.0", withView: true, withReport: false);
        world.AttachProjector();
        var activation = world.Installer.Activate(world.Context, PackKey, "1.1.0");
        Assert.True(activation.Activated, $"{activation.Error}: {activation.Detail}");
        Assert.Same(reportBefore, await world.ReadReportAsync());

        // What AccessAdministrationPreloadHostedService does next: the refused activation is reversed.
        // That deactivate pass refuses nothing, so a leg gated only on "zero refusals" would run with an
        // empty admitted set — 1.1.0 now Inactive, 1.0.0 still Superseded — and retract the replaced
        // package's every definition. Removal belongs to an ACTIVE replacement only.
        var deactivation = world.Installer.Deactivate(Tenant, PackKey, "1.1.0", Now, "test-operator");
        Assert.True(deactivation.Deactivated, deactivation.Error);
        await world.ProjectAsync();

        Assert.Same(reportBefore, await world.ReadReportAsync());
        Assert.Null(await world.ReadViewAsync());
    }

    /// <summary>One tenant, one pack key, the real installer, and the two registries under test.</summary>
    private sealed class World
    {
        private readonly KeyPair _keyPair = KeyPair.Generate();
        private readonly PackFileCodec _codec = new();
        private readonly InMemoryPackInstallStore _store = new();
        private readonly ServiceProvider _services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();

        public World()
        {
            Views = new TearDownableViewRegistry();
            Reports = new InMemoryReportDefinitionRegistry(new AcceptAllReports());
            Installer = new PackInstaller(
                new PackVerifier(new Ed25519Verifier(), _codec),
                _store,
                new WorkflowRefusingPackContentAdmission(),
                new InMemoryPackInstallAudit(),
                Authorization.TestAuthorization.AllowGate());
            Projector = new PackSeedProjector(
                _store,
                _services.GetRequiredService<IEntityTypeRegistry>(),
                NullLogger<PackSeedProjector>.Instance,
                viewDefinitions: Views,
                reportDefinitions: Reports,
                time: TimeProvider.System);
            Context = new PackInstallContext(
                Tenant,
                new InMemoryPackTrustStore(
                    [new PackTrustRoot(TrustScope.OwnRoster, _keyPair.PrincipalId, 1, TrustRootStatus.Current)]),
                PackRevocationList.Empty,
                Now,
                TimeSpan.FromDays(30),
                Principal: "test-operator");
        }

        public TearDownableViewRegistry Views { get; }

        public InMemoryReportDefinitionRegistry Reports { get; }

        public PackInstaller Installer { get; }

        public PackSeedProjector Projector { get; }

        public PackInstallContext Context { get; }

        public IPackProjectionReconciler Reconciler => Installer;

        public void AttachProjector() => Reconciler.AttachProjector(Projector);

        /// <summary>Clears <c>PackSeedProjector.ConsumedAuthorities</c> — the per-process replay guard,
        /// which a real node restart empties. Test-only; nothing production reaches this field.</summary>
        public static void SimulateProcessRestart()
        {
            var field = typeof(PackSeedProjector).GetField(
                "ConsumedAuthorities",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            ((System.Collections.Concurrent.ConcurrentDictionary<Guid, byte>)field.GetValue(null)!).Clear();
        }

        public IReadOnlyList<PackProjectionAdmission> IncompleteAdmissions() =>
            [.. ((IPackProjectionAdmissionStore)_store).ListIncompleteProjectionAdmissions()];

        public Task<PackSeedProjectionSummary> ProjectAsync() =>
            ((IPackSeedProjector)Projector).ProjectActivePacksAsync(Tenant);

        public ValueTask<ViewDefinition?> ReadViewAsync() =>
            Views.GetDefinitionAsync(Tenant.Value, ViewKey, ItemVersion);

        public ValueTask<ReportDefinition?> ReadReportAsync() =>
            Reports.GetDefinitionAsync(Tenant.Value, ReportKey, ItemVersion);

        public async Task InstallAndActivateAsync(string packVersion, bool withView, bool withReport)
        {
            Install(packVersion, withView, withReport);
            var activation = Installer.Activate(Tenant, PackKey, packVersion, Now, "test-operator");
            Assert.True(activation.Activated, $"{activation.Error}: {activation.Detail}");
            await Task.CompletedTask;
        }

        public void Install(string packVersion, bool withView, bool withReport)
        {
            var contents = new List<PackContentSource>();
            if (withView)
            {
                contents.Add(new PackContentSource(
                    ViewKey, PackContentKind.ViewDefinition, ItemVersion,
                    JsonSerializer.SerializeToNode(new ViewDefinition
                    {
                        Key = ViewKey,
                        Version = ItemVersion,
                        Tenant = Tenant.Value,
                        SchemaVersion = 1,
                        ViewKind = "views.entity-list/grid",
                        Title = "Who holds what",
                        Parameters = JsonSerializer.SerializeToElement(new { groupBy = "role", pageSize = 25 }),
                        CascadeLayer = CascadeLayer.Tenant,
                        Provenance = Provenance(),
                    })!));
            }

            if (withReport)
            {
                contents.Add(new PackContentSource(
                    ReportKey, PackContentKind.ReportDefinition, ItemVersion,
                    JsonSerializer.SerializeToNode(new ReportDefinition
                    {
                        Key = ReportKey,
                        Version = ItemVersion,
                        Tenant = Tenant.Value,
                        SchemaVersion = 1,
                        ReportKind = "reports.table/basic",
                        Title = "Access held, by person",
                        Parameters = JsonSerializer.SerializeToElement(new { groupBy = "person" }),
                        CascadeLayer = CascadeLayer.Tenant,
                        Provenance = Provenance(),
                    })!));
            }

            var exporter = new PackExporter(
                new PackContentCanonicalizer(),
                new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()),
                new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
                _codec,
                timeProvider: TimeProvider.System);
            var export = exporter.ExportAsync(
                    new PackExportRequest(
                        Key: PackKey,
                        Version: packVersion,
                        Name: "Access administration",
                        Description: "Exercises pack replacement removal.",
                        ScopeTier: PackScopeTier.Horizontal,
                        Contents: contents,
                        Dependencies: Array.Empty<PackDependencyRef>(),
                        CapabilityRequirements: Array.Empty<string>(),
                        Epoch: 1,
                        Dcp: DomainComplianceProfile.General("access-administration-test-author")),
                    new Ed25519Signer(_keyPair))
                .GetAwaiter().GetResult();
            Assert.True(export.Succeeded, string.Join(
                "; ", export.Validation.Errors.Select(error => $"{error.Code}: {error.Message}")));

            var install = Installer.Install(export.FileBytes!, Context);
            Assert.True(install.Installed, string.Join("; ", install.RefusalCodes));
        }

        private static JsonElement Provenance() => JsonDocument.Parse("""
            {"derivedAt":"2026-09-07T12:00:00+00:00","exportingActor":"access-administration-test-author","originPackKey":"harborline.access-administration","tier":"Civilian"}
            """).RootElement.Clone();
    }

    /// <summary>
    /// The view registry, with a switch that simulates the node being torn down mid-admit: the throw is
    /// the one exception family the projector deliberately does NOT convert to a refusal, so it leaves
    /// the pass exactly where an abrupt process exit would — after the removal, before the admission.
    /// </summary>
    private sealed class TearDownableViewRegistry : IViewDefinitionRegistry
    {
        private readonly InMemoryViewDefinitionRegistry _inner = new(new AcceptAllViews());

        public bool TearDownOnRegister { get; set; }

        /// <summary>Refuses the registration the way real governance does — a refusal the projector turns
        /// into a summary row, not an exception that aborts the pass.</summary>
        public bool RefuseOnRegister { get; set; }

        public ValueTask<ViewDefinition> RegisterAsync(
            ViewDefinition definition, CancellationToken cancellationToken = default)
            => TearDownOnRegister
                ? throw new OperationCanceledException("Simulated node teardown between remove and admit.")
                : RefuseOnRegister
                    ? throw new ViewDefinitionGovernanceException("view_definition.descriptor_refused")
                    : _inner.RegisterAsync(definition, cancellationToken);

        public ValueTask<bool> RemoveAsync(
            string tenant, string key, string version, CancellationToken cancellationToken = default)
            => _inner.RemoveAsync(tenant, key, version, cancellationToken);

        public ValueTask<ViewDefinition?> GetDefinitionAsync(
            string tenant, string key, string version, CancellationToken cancellationToken = default)
            => _inner.GetDefinitionAsync(tenant, key, version, cancellationToken);

        public ValueTask<IReadOnlyList<ViewDefinition>> ListDefinitionsAsync(
            string tenant, CancellationToken cancellationToken = default)
            => _inner.ListDefinitionsAsync(tenant, cancellationToken);

        public ValueTask<ViewDefinitionVersionList?> ListVersionsAsync(
            string tenant, string key, CancellationToken cancellationToken = default)
            => _inner.ListVersionsAsync(tenant, key, cancellationToken);
    }

    private sealed class AcceptAllViews : IViewDefinitionDescriptorRegistry
    {
        public ValueTask AdmitAsync(ViewDefinition definition, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class AcceptAllReports : IReportDefinitionDescriptorRegistry
    {
        public ValueTask AdmitAsync(ReportDefinition definition, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
