using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Views;

public sealed class ViewDefinitionPackProjectionTests
{
    [Fact]
    public void Pack_content_kind_maps_to_views_pillar()
    {
        var pillar = PackPillarMap.ForKind(PackContentKind.ViewDefinition);

        Assert.Equal(PackPillar.Views, pillar);
        Assert.Equal("view", PackPillarMap.GenericNoun(PackContentKind.ViewDefinition));
    }

    [Fact]
    public async Task Authoritative_view_definition_from_tenant_owned_pack_is_refused()
    {
        const string tenantValue = "aaaaaaaa-0000-0000-0000-000000000040";
        var tenant = new TenantId(tenantValue);
        var definition = new ViewDefinition
        {
            Key = "compliance-controls",
            Version = "2.3.4",
            Tenant = tenantValue,
            SchemaVersion = 1,
            ViewKind = "views.entity-list/grid",
            Title = "Authoritative compliance controls",
            Parameters = JsonSerializer.SerializeToElement(new { entityType = "equipment.condenser", pageSize = 25 }),
            // Authority tier travels as the envelope-backed CascadeLayer: any non-Tenant layer
            // demands a vendor-vouched (HarborlineChannel) pack per the projector's authority rule.
            CascadeLayer = CascadeLayer.Base,
            Provenance = JsonDocument.Parse("""
                {"derivedAt":"2026-08-18T12:00:00+00:00","exportingActor":"view-definition-test-author","originPackKey":"view-definition.test","tier":"Civilian"}
                """).RootElement.Clone(),
        };
        var store = new InMemoryPackInstallStore();
        using var keyPair = KeyPair.Generate();
        await InstallAndActivatePackAsync(
            store,
            tenant,
            definition.Key,
            definition.Version,
            JsonSerializer.SerializeToNode(definition)!,
            TrustScope.OwnRoster,
            keyPair);
        IViewDefinitionRegistry viewDefinitionRegistry = new InMemoryViewDefinitionRegistry(new AcceptAllDescriptorRegistry());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            viewDefinitions: viewDefinitionRegistry, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(tenant);

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.ViewDefinition, refusal.ContentKind);
        Assert.Equal("pack.view-definition.authoritative_requires_vendor_pack", refusal.Code);
        Assert.Null(await viewDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, definition.Key, definition.Version, CancellationToken.None));
    }

    [Fact]
    public async Task Tenant_view_definition_round_trip_preserves_version_and_provenance()
    {
        const string tenantValue = "bbbbbbbb-0000-0000-0000-000000000040";
        const string provenanceJson =
            "{\"derivedAt\":\"2026-07-15T09:30:00+00:00\",\"exportingActor\":\"acme-view-admin\",\"originPackKey\":\"view-definition.test\",\"tier\":\"Civilian\"}";
        var tenant = new TenantId(tenantValue);
        var definition = new ViewDefinition
        {
            Key = "regional-operations",
            Version = "3.2.1",
            Tenant = tenantValue,
            SchemaVersion = 1,
            ViewKind = "views.entity-list/grid",
            Title = "Tenant-owned regional operations",
            Parameters = JsonSerializer.SerializeToElement(new { entityType = "equipment.condenser", pageSize = 25 }),
            Provenance = JsonDocument.Parse(provenanceJson).RootElement.Clone(),
        };
        var store = new InMemoryPackInstallStore();
        using var keyPair = KeyPair.Generate();
        await InstallAndActivatePackAsync(
            store,
            tenant,
            definition.Key,
            definition.Version,
            JsonSerializer.SerializeToNode(definition)!,
            TrustScope.OwnRoster,
            keyPair);
        var viewDefinitionRegistry = new InMemoryViewDefinitionRegistry(new AcceptAllDescriptorRegistry());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            viewDefinitions: viewDefinitionRegistry, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(tenant);
        var readBack = await viewDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, "regional-operations", "3.2.1", CancellationToken.None);

        Assert.Empty(summary.Refusals);
        Assert.NotNull(readBack);
        Assert.Equal("3.2.1", readBack.Version);
        Assert.Equal(provenanceJson, readBack.Provenance.GetRawText());
    }

    [Fact]
    public async Task Mismatched_item_key_is_refused_as_malformed()
    {
        const string tenantValue = "aaaaaaaa-0000-0000-0000-000000000041";
        var tenant = new TenantId(tenantValue);
        var definition = CreateCivilianDefinition(
            tenantValue,
            "payload-view-key",
            "1.4.0",
            "Payload view title");
        var store = new InMemoryPackInstallStore();
        using var keyPair = KeyPair.Generate();
        await InstallAndActivatePackAsync(
            store,
            tenant,
            "content-item-key",
            "1.4.0",
            JsonSerializer.SerializeToNode(definition)!,
            TrustScope.OwnRoster,
            keyPair);
        var viewDefinitionRegistry = new InMemoryViewDefinitionRegistry(new AcceptAllDescriptorRegistry());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            viewDefinitions: viewDefinitionRegistry, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(tenant);

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.ViewDefinition, refusal.ContentKind);
        Assert.Equal("pack.view-definition.malformed", refusal.Code);
        Assert.Null(await viewDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, "content-item-key", "1.4.0", CancellationToken.None));
        Assert.Null(await viewDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, "payload-view-key", "1.4.0", CancellationToken.None));
    }

    [Fact]
    public async Task Missing_registry_refuses_not_silently_skips()
    {
        const string tenantValue = "bbbbbbbb-0000-0000-0000-000000000041";
        var tenant = new TenantId(tenantValue);
        var definition = CreateCivilianDefinition(
            tenantValue,
            "unwired-view",
            "1.0.0",
            "Unwired view");
        var store = new InMemoryPackInstallStore();
        using var keyPair = KeyPair.Generate();
        await InstallAndActivatePackAsync(
            store,
            tenant,
            definition.Key,
            definition.Version,
            JsonSerializer.SerializeToNode(definition)!,
            TrustScope.OwnRoster,
            keyPair);
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(tenant);

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.ViewDefinition, refusal.ContentKind);
        Assert.Equal("pack.view-definition.registry_not_wired", refusal.Code);
    }

    [Fact]
    public async Task Pinned_tuple_conflict_refuses_second_content()
    {
        const string tenantValue = "aaaaaaaa-0000-0000-0000-000000000042";
        var tenant = new TenantId(tenantValue);
        var original = CreateCivilianDefinition(
            tenantValue,
            "monthly-operations",
            "3.2.1",
            "Original title");
        var conflicting = CreateCivilianDefinition(
            tenantValue,
            "monthly-operations",
            "3.2.1",
            "Conflicting title");
        var store = new InMemoryPackInstallStore();
        using var firstKeyPair = KeyPair.Generate();
        await InstallAndActivatePackAsync(
            store,
            tenant,
            original.Key,
            original.Version,
            JsonSerializer.SerializeToNode(original)!,
            TrustScope.OwnRoster,
            firstKeyPair);
        var viewDefinitionRegistry = new InMemoryViewDefinitionRegistry(new AcceptAllDescriptorRegistry());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            viewDefinitions: viewDefinitionRegistry, time: TimeProvider.System);

        var firstSummary = await projector.ProjectActivePacksAsync(tenant);

        Assert.Empty(firstSummary.Refusals);

        using var secondKeyPair = KeyPair.Generate();
        await InstallAndActivatePackAsync(
            store,
            tenant,
            conflicting.Key,
            conflicting.Version,
            JsonSerializer.SerializeToNode(conflicting)!,
            TrustScope.OwnRoster,
            secondKeyPair,
            // Same pack key at a higher pack version: the installer treats it as an update, so the
            // content-key collision guard stays quiet and the PROJECTOR's pinned-tuple rule is what
            // sees the same (tenant, key, version) carrying different content.
            packVersion: "1.0.1");

        var secondSummary = await projector.ProjectActivePacksAsync(tenant);

        var refusal = Assert.Single(secondSummary.Refusals);
        Assert.Equal(PackContentKind.ViewDefinition, refusal.ContentKind);
        Assert.Equal("pack.view-definition.pinned_tuple_conflict", refusal.Code);
        var readBack = await viewDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, "monthly-operations", "3.2.1", CancellationToken.None);
        Assert.NotNull(readBack);
        Assert.Equal("Original title", readBack.Title);
    }

    [Fact]
    public async Task Reprojection_is_idempotent()
    {
        const string tenantValue = "bbbbbbbb-0000-0000-0000-000000000042";
        var tenant = new TenantId(tenantValue);
        var definition = CreateCivilianDefinition(
            tenantValue,
            "daily-inventory",
            "4.1.2",
            "Daily inventory");
        var store = new InMemoryPackInstallStore();
        using var keyPair = KeyPair.Generate();
        await InstallAndActivatePackAsync(
            store,
            tenant,
            definition.Key,
            definition.Version,
            JsonSerializer.SerializeToNode(definition)!,
            TrustScope.OwnRoster,
            keyPair);
        var viewDefinitionRegistry = new InMemoryViewDefinitionRegistry(new AcceptAllDescriptorRegistry());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            viewDefinitions: viewDefinitionRegistry, time: TimeProvider.System);

        var firstSummary = await projector.ProjectActivePacksAsync(tenant);
        var secondSummary = await projector.ProjectActivePacksAsync(tenant);
        var readBack = await viewDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, "daily-inventory", "4.1.2", CancellationToken.None);

        Assert.Empty(firstSummary.Refusals);
        Assert.Empty(secondSummary.Refusals);
        Assert.NotNull(readBack);
        Assert.Equal("Daily inventory", readBack.Title);
        Assert.Equal("4.1.2", readBack.Version);
    }

    [Fact]
    public async Task Unknown_view_kind_is_refused()
    {
        const string tenantValue = "aaaaaaaa-0000-0000-0000-000000000043";
        var tenant = new TenantId(tenantValue);
        var definition = CreateCivilianDefinition(
            tenantValue,
            "unknown-kind-view",
            "1.0.0",
            "Unknown kind view",
            viewKind: "views.unknown/not-registered");
        var store = new InMemoryPackInstallStore();
        using var keyPair = KeyPair.Generate();
        await InstallAndActivatePackAsync(
            store,
            tenant,
            definition.Key,
            definition.Version,
            JsonSerializer.SerializeToNode(definition)!,
            TrustScope.OwnRoster,
            keyPair);
        var viewDefinitionRegistry = new InMemoryViewDefinitionRegistry(new RefuseUnknownKindDescriptorRegistry());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            viewDefinitions: viewDefinitionRegistry, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(tenant);

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.ViewDefinition, refusal.ContentKind);
        // Descriptor refusals (kind unknown, parameters not an object) fold into the malformed
        // family; only registry pinned-tuple governance maps to pinned_tuple_conflict.
        Assert.Equal("pack.view-definition.malformed", refusal.Code);
        Assert.Null(await viewDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, "unknown-kind-view", "1.0.0", CancellationToken.None));
    }

    private static ViewDefinition CreateCivilianDefinition(
        string tenant,
        string key,
        string version,
        string title,
        string viewKind = "views.entity-list/grid")
    {
        return new ViewDefinition
        {
            Key = key,
            Version = version,
            Tenant = tenant,
            SchemaVersion = 1,
            ViewKind = viewKind,
            Title = title,
            Parameters = JsonSerializer.SerializeToElement(new { entityType = "equipment.condenser", pageSize = 25 }),
            Provenance = JsonDocument.Parse("""
                {"derivedAt":"2026-08-18T12:00:00+00:00","exportingActor":"view-definition-test-author","originPackKey":"view-definition.test","tier":"Civilian"}
                """).RootElement.Clone(),
        };
    }

    private static async Task InstallAndActivatePackAsync(
        InMemoryPackInstallStore store,
        TenantId tenant,
        string itemKey,
        string itemVersion,
        JsonNode definition,
        TrustScope trustScope,
        KeyPair keyPair,
        string packKey = "view-definition.test",
        string packVersion = "1.0.0")
    {
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec, timeProvider: TimeProvider.System);
        var export = await exporter.ExportAsync(
            new PackExportRequest(
                Key: packKey,
                Version: packVersion,
                Name: "View definition test pack",
                Description: "Exercises view-definition pack projection.",
                ScopeTier: PackScopeTier.Horizontal,
                Contents:
                [
                    new PackContentSource(
                        itemKey,
                        PackContentKind.ViewDefinition,
                        itemVersion,
                        definition),
                ],
                Dependencies: Array.Empty<PackDependencyRef>(),
                CapabilityRequirements: Array.Empty<string>(),
                Epoch: 1,
                Dcp: DomainComplianceProfile.General("view-definition-test-author")),
            new Ed25519Signer(keyPair));
        Assert.True(export.Succeeded, string.Join(
            "; ", export.Validation.Errors.Select(error => $"{error.Code}: {error.Message}")));
        var trustStore = new InMemoryPackTrustStore(
        [
            new PackTrustRoot(trustScope, keyPair.PrincipalId, 1, TrustRootStatus.Current),
        ]);
        var installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), codec),
            store,
            new WorkflowRefusingPackContentAdmission(),
            new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        var install = installer.Install(
            export.FileBytes!,
            new PackInstallContext(
                tenant,
                trustStore,
                PackRevocationList.Empty,
                new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero),
                TimeSpan.FromDays(30),
                Principal: "test-operator"));
        Assert.True(install.Installed, string.Join("; ", install.RefusalCodes));

        var activation = installer.Activate(
            tenant,
            install.PackKey,
            install.Version,
            new DateTimeOffset(2026, 8, 18, 12, 1, 0, TimeSpan.Zero),
            "test-operator");
        Assert.True(activation.Activated, $"{activation.Error}: {activation.Detail}");
    }

    private sealed class AcceptAllDescriptorRegistry : IViewDefinitionDescriptorRegistry
    {
        public ValueTask AdmitAsync(ViewDefinition definition, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class RefuseUnknownKindDescriptorRegistry : IViewDefinitionDescriptorRegistry
    {
        public ValueTask AdmitAsync(ViewDefinition definition, CancellationToken cancellationToken = default)
            => throw new ViewDefinitionGovernanceException("view_definition.kind_unknown");
    }
}
