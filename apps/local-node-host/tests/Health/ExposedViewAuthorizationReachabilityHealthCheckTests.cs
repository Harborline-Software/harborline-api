using System.Text.Json;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>T-398 whole-catalogue authorization reachability from the two sealed platform roots.</summary>
public sealed class ExposedViewAuthorizationReachabilityHealthCheckTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000398");
    private const string ViewKey = "workshop.evolution";
    private const string Capability = "records:read";

    [Fact(DisplayName = "T-398: an exposed view with no platform-root binding names the view, capability and exact roots")]
    public async Task Unreachable_Exposed_View_Produces_Stable_Finding()
    {
        var (views, pack) = await ViewAndPackAsync(exposes: [ViewKey]);

        var finding = Assert.Single(await ExposedViewAuthorizationReachability.FindAsync(
            Tenant, [pack], views, new Definitions(), new InMemoryRoleVocabulary()));

        Assert.Equal(ExposedViewAuthorizationReachability.UnreachableCode, finding.Code);
        Assert.Equal("/contents/0/content/authorizationCapability", finding.Pointer);
        Assert.Equal(ViewKey, finding.DefinitionId);
        Assert.Equal("1.0.0", finding.Version);
        Assert.Equal("evolution", finding.Pack);
        Assert.Equal(Capability, finding.Capability);
        Assert.Equal(
            [RoleReference.Administrator.ToString(), RoleReference.Auditor.ToString()],
            finding.RolesChecked);
    }

    [Fact(DisplayName = "T-398: an exposed view with no capability emits a stable missing-capability finding")]
    public async Task Missing_Capability_Is_Visible_To_Health()
    {
        var (views, pack) = await ViewAndPackAsync(exposes: [ViewKey], capability: null);

        var finding = Assert.Single(await ExposedViewAuthorizationReachability.FindAsync(
            Tenant, [pack], views, new Definitions(), new InMemoryRoleVocabulary()));

        Assert.Equal(ExposedViewAuthorizationReachability.MissingCapabilityCode, finding.Code);
        Assert.Equal("/contents/0/content/authorizationCapability", finding.Pointer);
        Assert.Equal(ViewKey, finding.DefinitionId);
        Assert.Equal(ExposedViewAuthorizationReachability.UndeclaredCapability, finding.Capability);
    }

    [Theory(DisplayName = "T-398: either sealed platform root makes the exposed capability reachable")]
    [InlineData("administrator")]
    [InlineData("auditor")]
    public async Task Administrator_Or_Auditor_Binding_Is_Reachable(string root)
    {
        var role = root == "administrator" ? RoleReference.Administrator : RoleReference.Auditor;
        var (views, pack) = await ViewAndPackAsync(exposes: [ViewKey]);
        var definitions = new Definitions(Binding(Capability, role));

        Assert.Empty(await ExposedViewAuthorizationReachability.FindAsync(
            Tenant, [pack], views, definitions, new InMemoryRoleVocabulary()));
    }

    [Fact(DisplayName = "T-398: shipped packs without an explicit exposed interface produce exactly zero findings")]
    public async Task Pack_Without_Exposes_Produces_Explicit_Zero()
    {
        var (views, pack) = await ViewAndPackAsync(exposes: null);

        var findings = await ExposedViewAuthorizationReachability.FindAsync(
            Tenant, [pack], views, new Definitions(), new InMemoryRoleVocabulary());

        Assert.Empty(findings);
    }

    [Fact(DisplayName = "T-398: findings are non-refusing and refresh away after retraction")]
    public async Task Finding_Does_Not_Change_Activation_And_Retraction_Refreshes_Report()
    {
        var (views, active) = await ViewAndPackAsync(exposes: [ViewKey]);
        var reports = new ExposedViewAuthorizationReachabilityReports();
        await reports.RefreshAsync(
            Tenant, [active], views, new Definitions(), new InMemoryRoleVocabulary());

        Assert.Equal(PackLifecycleState.Active, active.Lifecycle);
        Assert.Single(reports.Inspect(Tenant));

        var inactive = active with { Lifecycle = PackLifecycleState.Inactive };
        await reports.RefreshAsync(
            Tenant, [inactive], views, new Definitions(), new InMemoryRoleVocabulary());

        Assert.Equal(PackLifecycleState.Inactive, inactive.Lifecycle);
        Assert.Empty(reports.Inspect(Tenant));
    }

    [Fact(DisplayName = "T-398: real activation is non-refusing and production startup reprojection restores the finding")]
    public async Task Activation_And_Production_Startup_Reprojection_Are_Stable()
    {
        var (_, active) = await ViewAndPackAsync(exposes: [ViewKey], capability: null);
        var store = new InMemoryPackInstallStore();
        var draft = active with { Lifecycle = PackLifecycleState.Draft };
        store.Commit(new PackInstallTransaction(
            Tenant, draft, new PackInstallWatermark(draft.PackKey, draft.Version, new Dictionary<string, int>()),
            Array.Empty<PackTenantOverride>()));

        using var services = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var firstViews = new InMemoryViewDefinitionRegistry(new AcceptViews());
        var firstReports = new ExposedViewAuthorizationReachabilityReports();
        var installer = Installer(store, Projector(store, services, firstViews, firstReports));

        var activated = installer.Activate(Tenant, draft.PackKey, draft.Version, DateTimeOffset.UtcNow, "test-operator");

        Assert.True(activated.Activated);
        Assert.True(activated.Projected);
        var before = Assert.Single(firstReports.Inspect(Tenant));
        Assert.Equal(ExposedViewAuthorizationReachability.MissingCapabilityCode, before.Code);

        var restartedViews = new InMemoryViewDefinitionRegistry(new AcceptViews());
        var restartedReports = new ExposedViewAuthorizationReachabilityReports();
        var restarted = Installer(store, Projector(store, services, restartedViews, restartedReports));
        var hosted = new PackSeedProjectionHostedService(
            restarted, new FixedActiveTeam(), NullLogger<PackSeedProjectionHostedService>.Instance,
            store, new InMemoryPackTrustStore([]), PackRevocationList.Empty, TimeProvider.System);

        await hosted.StartAsync(CancellationToken.None);

        var after = Assert.Single(restartedReports.Inspect(Tenant));
        Assert.Equal(before with { RolesChecked = Array.Empty<string>() },
            after with { RolesChecked = Array.Empty<string>() });
        Assert.Equal(before.RolesChecked, after.RolesChecked);
        Assert.NotNull(await restartedViews.GetDefinitionAsync(Tenant.Value, ViewKey, "1.0.0"));
    }

    [Fact(DisplayName = "T-398: Health degrades with structured reachability evidence rather than refusing activation")]
    public async Task Health_Reports_Structured_Finding()
    {
        var (views, pack) = await ViewAndPackAsync(exposes: [ViewKey]);
        var reports = new ExposedViewAuthorizationReachabilityReports();
        await reports.RefreshAsync(
            Tenant, [pack], views, new Definitions(), new InMemoryRoleVocabulary());

        var result = await new ExposedViewAuthorizationReachabilityHealthCheck(reports)
            .CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ExposedViewAuthorizationReachabilityFinding>>(
            result.Data["exposedViewAuthorizationReachabilityFindings"]));
    }

    private static async Task<(IViewDefinitionRegistry Views, InstalledPack Pack)> ViewAndPackAsync(
        IReadOnlyList<string>? exposes,
        string? capability = Capability)
    {
        var body = JsonSerializer.SerializeToElement(new
        {
            key = ViewKey,
            version = "1.0.0",
            tenant = Tenant.Value,
            schemaVersion = 1,
            viewKind = "entity-table",
            title = "Evolution",
            authorizationCapability = capability,
            parameters = new { },
        });
        var definition = JsonSerializer.Deserialize<ViewDefinition>(
            body.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var views = new InMemoryViewDefinitionRegistry(new AcceptViews());
        await views.RegisterAsync(definition);
        var item = new PackSeedItem(
            ViewKey, PackContentKind.ViewDefinition, "1.0.0", body.GetRawText(), Cid.FromBytes([]));
        var pack = new InstalledPack(
            "evolution", "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Active, [item],
            new Dictionary<string, int>(), DateTimeOffset.UnixEpoch,
            PrincipalId.FromBytes(new byte[32]), 1, TrustScope.OwnRoster,
            Array.Empty<PackDependencyRef>(), Exposes: exposes, InterfaceVersion: exposes is null ? null : 1);
        return (views, pack);
    }

    private static PackInstaller Installer(InMemoryPackInstallStore store, PackSeedProjector projector)
    {
        var installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), store,
            new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        ((IPackProjectionReconciler)installer).AttachProjector(projector);
        return installer;
    }

    private static PackSeedProjector Projector(
        InMemoryPackInstallStore store,
        IServiceProvider services,
        IViewDefinitionRegistry views,
        ExposedViewAuthorizationReachabilityReports reports) => new(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            time: TimeProvider.System,
            viewDefinitions: views,
            roleVocabulary: new InMemoryRoleVocabulary(),
            viewReachabilityReports: reports,
            authorizationDefinitionCatalogue: new Definitions());

    private sealed class FixedActiveTeam : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = new(
            new TeamId(Guid.Parse(Tenant.Value)), "T-398", new ServiceCollection().BuildServiceProvider(),
            TimeProvider.System);
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private static AuthorizationDefinitionBindingView Binding(string capability, RoleReference role)
    {
        var operation = AuthorizationOperation.Parse(capability);
        var definition = new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.NewGuid()), "test", 1, operation,
            new PermissionAtom(operation, ScopeExpression.Parse("/")), RoleBindingSet.Of(role));
        return new AuthorizationDefinitionBindingView(definition, 1, RoleBindingSet.Of(role), null);
    }

    private sealed class Definitions(params AuthorizationDefinitionBindingView[] bindings)
        : IAuthorizationDefinitionCatalogueReader
    {
        public ValueTask<IReadOnlyList<AuthorizationDefinitionBindingView>> ListAsync(
            TenantId tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<AuthorizationDefinitionBindingView>>(bindings);

        public ValueTask<AuthorizationDefinitionBindingView?> FindAsync(
            TenantId tenantId,
            AuthorizationCapabilityDefinitionId definitionId,
            CancellationToken ct = default) =>
            ValueTask.FromResult(bindings.FirstOrDefault(binding =>
                binding.Definition.DefinitionId == definitionId));
    }

    private sealed class AcceptViews : IViewDefinitionDescriptorRegistry
    {
        public ValueTask AdmitAsync(ViewDefinition definition, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
