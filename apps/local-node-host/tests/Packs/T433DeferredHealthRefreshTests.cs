using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Security.Cryptography;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed partial class AccessAdministrationPreloadTests
{
    private readonly WorkflowCatalogueLintReports _workflowLintReports = new();
    private readonly ExposedViewAuthorizationReachabilityReports _viewReachabilityReports = new();
    private Action? _beforeDiagnosticRefresh;
    private Action? _beforeDiagnosticPackRead;
    private Action? _beforeInstallerPackRead;
    private Func<IServiceProvider, IEntityMutationStore> _workflowMutationAccessor = null!;

    [Theory]
    [InlineData("workflow")]
    [InlineData("view")]
    public async Task In_progress_health_refresh_fences_new_activation_until_its_snapshot_is_published(string report)
    {
        await PreloadPlatformThenAccessAsync();
        var context = ReplacementContext();
        var first = HealthReplacement("1.1.2", "1.0.0", hasFinding: false);
        var second = HealthReplacement("1.1.3", "1.0.1", hasFinding: true);
        Assert.True(_installer.Install(await ExportAsync(first), context).Installed);
        Assert.True(_installer.Install(await ExportAsync(second), context).Installed);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReadyToPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var refreshes = 0;
        var phase = 0;
        var diagnosticReads = 0;
        var expectedRead = report == "workflow" ? 1 : 2;
        _beforeDiagnosticRefresh = () =>
        {
            if (Interlocked.Increment(ref refreshes) == 1) Volatile.Write(ref phase, 1);
        };
        _beforeDiagnosticPackRead = () =>
        {
            if (Volatile.Read(ref phase) != 1 || Interlocked.Increment(ref diagnosticReads) != expectedRead) return;
            reading.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The test must release the consistent health snapshot read.");
            Volatile.Write(ref phase, 2);
        };

        var activationA = Task.Run(() => _installer.ActivateAsync(context, first.Key, first.Version));
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var guardReads = 0;
        _beforeInstallerPackRead = () =>
        {
            // This pack has no provider slot: interface and collision reads complete its guard snapshot.
            if (Interlocked.Increment(ref guardReads) == 2) secondReadyToPublish.TrySetResult();
        };
        var activationB = Task.Run(() => _installer.ActivateAsync(context, second.Key, second.Version));
        try
        {
            await secondReadyToPublish.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(activationB.IsCompleted);
            Assert.Equal(first.Version, _store.GetActive(Tenant, first.Key)!.Version);
        }
        finally { release.Set(); }

        Assert.True((await activationA.WaitAsync(TimeSpan.FromSeconds(10))).Activated);
        var completedB = await activationB.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(completedB.Activated, completedB.Detail);
        Assert.Equal(second.Version, _store.GetActive(Tenant, second.Key)!.Version);
        Assert.Contains("1.0.1", HealthFinding("view"), StringComparison.Ordinal);
        Assert.Empty(_workflowLintReports.Inspect(Tenant));
    }

    [Theory]
    [InlineData("workflow")]
    [InlineData("view")]
    public async Task Delayed_activation_health_refresh_cannot_erase_newer_active_findings(string report)
    {
        await PreloadPlatformThenAccessAsync();
        var context = ReplacementContext();
        var first = HealthReplacement("1.1.2", "1.0.0", hasFinding: false);
        var second = HealthReplacement("1.1.3", "1.0.1", hasFinding: true, workflowFinding: report == "workflow");
        var installedA = _installer.Install(await ExportAsync(first), context);
        Assert.True(installedA.Installed, JsonSerializer.Serialize(installedA));
        if (report == "workflow")
            SeedLegacyWorkflowDraft(first, second);
        else
        {
            var installedB = _installer.Install(await ExportAsync(second), context);
            Assert.True(installedB.Installed, JsonSerializer.Serialize(installedB));
        }

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var refreshes = 0;
        _beforeDiagnosticRefresh = () =>
        {
            if (Interlocked.Increment(ref refreshes) != 1) return;
            reached.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The test must release the first activation's diagnostic refresh.");
        };

        var activationA = Task.Run(() => _installer.ActivateAsync(context, first.Key, first.Version));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string currentFinding;
        try
        {
            Assert.Equal(first.Version, _store.GetActive(Tenant, first.Key)!.Version);
            if (report == "workflow") await SeedLegacyCompiledWorkflowAsync(second);
            var activationB = await _installer.ActivateAsync(context, second.Key, second.Version);
            Assert.True(activationB.Activated, activationB.Detail + JsonSerializer.Serialize(activationB.ProjectionResult));
            Assert.True(activationB.Projected);
            Assert.Equal(second.Version, _store.GetActive(Tenant, first.Key)!.Version);
            currentFinding = HealthFinding(report);
            Assert.Contains("1.0.1", currentFinding, StringComparison.Ordinal);
        }
        finally { release.Set(); }

        Assert.True((await activationA.WaitAsync(TimeSpan.FromSeconds(10))).Activated);
        Assert.Equal(currentFinding, HealthFinding(report));
        Assert.Equal(second.Version, _store.GetActive(Tenant, first.Key)!.Version);
    }

    private string HealthFinding(string report) => report == "workflow"
        ? JsonSerializer.Serialize(Assert.Single(_workflowLintReports.Inspect(Tenant), finding => finding.DefinitionId == "m6.workflow"))
        : JsonSerializer.Serialize(Assert.Single(_viewReachabilityReports.Inspect(Tenant), finding => finding.DefinitionId == "m6.early-view"));

    private void SeedLegacyWorkflowDraft(PackExportRequest first, PackExportRequest second)
    {
        // Current authoring admission rejects unreachable states. The diagnostic still has to describe
        // installed legacy evidence accurately; only setup bypasses today's authoring check. Activation,
        // authorization, runtime projection, lifecycle transitions and deferred diagnostics remain real.
        var canonicalizer = new PackContentCanonicalizer();
        var seeds = second.Contents.Select(canonicalizer.Canonicalize)
            .Select(item => new PackSeedItem(item.Key, item.Kind, item.Version,
                Encoding.UTF8.GetString(item.CanonicalBytes.Span), item.ContentAddress)).ToArray();
        var prior = _store.GetVersion(Tenant, first.Key, first.Version)!;
        var legacyDraft = prior with { Version = second.Version, SeedItems = seeds };
        _store.Commit(new(Tenant, legacyDraft,
            _store.GetWatermark(Tenant, first.Key)! with { Version = second.Version }, []));
    }

    private async Task SeedLegacyCompiledWorkflowAsync(PackExportRequest second)
    {
        // A legacy published tuple must accompany the legacy installed seed: today's runtime
        // registration also revalidates new tuples. Reuse A's genuine persisted envelope/stamps,
        // then seed the historical B revision directly into the same entity store for this test only.
        var prior = await _workflows.GetAsync(new(Tenant, "m6.workflow", "1.0.0"));
        var source = second.Contents.Single(item => item.Key == "m6.workflow");
        var authored = JsonNode.Parse(prior.Authored.GetRawText())!;
        authored["version"] = source.Version;
        authored["states"] = source.Content["states"]!.DeepClone();
        using var authoredDocument = JsonDocument.Parse(authored.ToJsonString());
        using var body = EntityStoreWorkflowDefinitionStore.SerializeEnvelope(
            prior.Envelope with { Version = WorkflowDefinitionVersion.Parse(source.Version) },
            WorkflowDefinitionStatus.Published, authoredDocument.RootElement,
            new(second.Key, second.Version), DateTimeOffset.UtcNow);
        var nonce = $"{Tenant.Value}|m6.workflow|{source.Version}";
        var localPart = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));
        await _workflowMutationAccessor(_app.Services).CreateAsync(
            EntityStoreWorkflowDefinitionStore.DefinitionSchema, body,
            new("workflowdef", "workflows", nonce, EntityStoreWorkflowDefinitionStore.DefinitionAuthor,
                Tenant, ExplicitLocalPart: localPart));
    }

    private PackExportRequest HealthReplacement(string packVersion, string definitionVersion, bool hasFinding, bool workflowFinding = false)
    {
        var source = MixedReplacement(packVersion, refuse: false);
        return source with
        {
            Exposes = ["m6.early-view"],
            InterfaceVersion = 1,
            Contents = source.Contents.Select(item =>
            {
                if (item.Key is not ("m6.workflow" or "m6.early-view")) return item;
                var content = item.Content.DeepClone();
                content["version"] = definitionVersion;
                if (item.Key == "m6.workflow" && workflowFinding)
                    content["states"]!.AsArray().Add(new JsonObject { ["id"] = "m6.orphan", ["kind"] = "Normal" });
                if (item.Key == "m6.early-view")
                    content["authorizationCapability"] = hasFinding ? null : "records:read";
                return item with { Version = definitionVersion, Content = content };
            }).ToArray(),
        };
    }

    // The ordinary edge-index notification precedes both health refreshes. Pausing it holds no
    // activation lease, so a real newer activation can commit and complete its own diagnostics.
    private sealed class DeferredHealthRefreshProbe(IPackContentEdgeIndexProvider inner, Action beforeRefresh)
        : IPackContentEdgeIndexProvider
    {
        public PackContentEdgeIndex GetOrRebuild(TenantId tenant) => inner.GetOrRebuild(tenant);
        public PackContentEdgeIndex Rebuild(TenantId tenant)
        {
            beforeRefresh();
            return inner.Rebuild(tenant);
        }
    }

    private sealed class DiagnosticReadStore(IPackInstallStore inner, Action beforeList) : IPackInstallStore
    {
        public IReadOnlyList<InstalledPack> ListInstalled(TenantId tenant)
        {
            var snapshot = inner.ListInstalled(tenant);
            beforeList();
            return snapshot;
        }
        public InstalledPack? GetActive(TenantId tenant, string key) => inner.GetActive(tenant, key);
        public InstalledPack? GetVersion(TenantId tenant, string key, string version) => inner.GetVersion(tenant, key, version);
        public bool AnyInstalled() => inner.AnyInstalled();
        public PackInstallWatermark? GetWatermark(TenantId tenant, string key) => inner.GetWatermark(tenant, key);
        public IReadOnlyList<PackTenantOverride> GetOverrides(TenantId tenant, string key) => inner.GetOverrides(tenant, key);
        public IReadOnlyDictionary<string, string> GetKeyOwnership(TenantId tenant) => inner.GetKeyOwnership(tenant);
    }
}
