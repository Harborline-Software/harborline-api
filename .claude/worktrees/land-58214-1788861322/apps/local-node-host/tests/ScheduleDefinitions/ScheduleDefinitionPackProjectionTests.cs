using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.ScheduleDefinitions;
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

namespace Harborline.Api.LocalNodeHost.Tests.Scheduling;

public sealed class ScheduleDefinitionPackProjectionTests
{
    [Fact]
    public void Pack_content_kind_maps_to_scheduling_pillar()
    {
        var pillar = PackPillarMap.ForKind(PackContentKind.ScheduleDefinition);

        Assert.Equal(PackPillar.Scheduling, pillar);
        Assert.Equal("schedule", PackPillarMap.GenericNoun(PackContentKind.ScheduleDefinition));
    }

    [Fact]
    public async Task Authoritative_schedule_definition_from_tenant_owned_pack_is_refused()
    {
        const string tenantValue = "aaaaaaaa-0000-0000-0000-000000000032";
        var tenant = new TenantId(tenantValue);
        var definition = new ScheduleDefinition
        {
            Key = "compliance-controls",
            Version = "2.3.4",
            Tenant = tenantValue,
            SchemaVersion = 1,
            ScheduleKind = "harborline.scheduling-definition-draft/v0",
            Title = "Authoritative compliance controls",
            Body = CreateSchedulingBody("Authoritative compliance controls"),
            // Authority tier travels as the envelope-backed CascadeLayer: any non-Tenant layer
            // demands a vendor-vouched (HarborlineChannel) pack per the projector's authority rule.
            CascadeLayer = CascadeLayer.Base,
            Provenance = JsonDocument.Parse("""
                {"originPackKey":"schedule-definition.test","exportingActor":"schedule-definition-test-author","derivedAt":"2026-08-18T12:00:00+00:00"}
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
        IScheduleDefinitionRegistry scheduleDefinitionRegistry = new InMemoryScheduleDefinitionRegistry(new AcceptAllDescriptorRegistry());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            scheduleDefinitions: scheduleDefinitionRegistry, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(tenant);

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.ScheduleDefinition, refusal.ContentKind);
        Assert.Equal("pack.schedule-definition.authoritative_requires_vendor_pack", refusal.Code);
        Assert.Null(await scheduleDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, definition.Key, definition.Version, CancellationToken.None));
    }

    [Fact]
    public async Task Tenant_schedule_definition_round_trip_preserves_version_and_provenance()
    {
        const string tenantValue = "bbbbbbbb-0000-0000-0000-000000000032";
        const string provenanceJson =
            "{\"derivedAt\":\"2026-07-15T09:30:00+00:00\",\"exportingActor\":\"acme-schedule-admin\",\"originPackKey\":\"schedule-definition.test\",\"tier\":\"Civilian\"}";
        var tenant = new TenantId(tenantValue);
        var definition = new ScheduleDefinition
        {
            Key = "regional-operations",
            Version = "3.2.1",
            Tenant = tenantValue,
            SchemaVersion = 1,
            ScheduleKind = "harborline.scheduling-definition-draft/v0",
            Title = "Tenant-owned regional operations",
            Body = CreateSchedulingBody("Tenant-owned regional operations"),
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
        var scheduleDefinitionRegistry = new InMemoryScheduleDefinitionRegistry(new AcceptAllDescriptorRegistry());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            scheduleDefinitions: scheduleDefinitionRegistry, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(tenant);
        var readBack = await scheduleDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, "regional-operations", "3.2.1", CancellationToken.None);

        Assert.Empty(summary.Refusals);
        Assert.NotNull(readBack);
        Assert.Equal("3.2.1", readBack.Version);
        Assert.Equal(provenanceJson, readBack.Provenance.GetRawText());
    }

    [Fact]
    public async Task Mismatched_item_key_is_refused_as_malformed()
    {
        const string tenantValue = "aaaaaaaa-0000-0000-0000-000000000033";
        var tenant = new TenantId(tenantValue);
        var definition = CreateCivilianDefinition(
            tenantValue,
            "payload-schedule-key",
            "1.4.0",
            "Payload schedule title");
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
        var scheduleDefinitionRegistry = new InMemoryScheduleDefinitionRegistry(new AcceptAllDescriptorRegistry());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            scheduleDefinitions: scheduleDefinitionRegistry, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(tenant);

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.ScheduleDefinition, refusal.ContentKind);
        Assert.Equal("pack.schedule-definition.malformed", refusal.Code);
        Assert.Null(await scheduleDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, "content-item-key", "1.4.0", CancellationToken.None));
        Assert.Null(await scheduleDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, "payload-schedule-key", "1.4.0", CancellationToken.None));
    }

    [Fact]
    public async Task Missing_registry_refuses_not_silently_skips()
    {
        const string tenantValue = "bbbbbbbb-0000-0000-0000-000000000033";
        var tenant = new TenantId(tenantValue);
        var definition = CreateCivilianDefinition(
            tenantValue,
            "unwired-schedule",
            "1.0.0",
            "Unwired schedule");
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
        Assert.Equal(PackContentKind.ScheduleDefinition, refusal.ContentKind);
        Assert.Equal("pack.schedule-definition.registry_not_wired", refusal.Code);
    }

    [Fact]
    public async Task Pinned_tuple_conflict_refuses_second_content()
    {
        const string tenantValue = "aaaaaaaa-0000-0000-0000-000000000034";
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
        var scheduleDefinitionRegistry = new InMemoryScheduleDefinitionRegistry(new AcceptAllDescriptorRegistry());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            scheduleDefinitions: scheduleDefinitionRegistry, time: TimeProvider.System);

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
        Assert.Equal(PackContentKind.ScheduleDefinition, refusal.ContentKind);
        Assert.Equal("pack.schedule-definition.pinned_tuple_conflict", refusal.Code);
        var readBack = await scheduleDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, "monthly-operations", "3.2.1", CancellationToken.None);
        Assert.NotNull(readBack);
        Assert.Equal("Original title", readBack.Title);
    }

    [Fact]
    public async Task Reprojection_is_idempotent()
    {
        const string tenantValue = "bbbbbbbb-0000-0000-0000-000000000034";
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
        var scheduleDefinitionRegistry = new InMemoryScheduleDefinitionRegistry(new AcceptAllDescriptorRegistry());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            scheduleDefinitions: scheduleDefinitionRegistry, time: TimeProvider.System);

        var firstSummary = await projector.ProjectActivePacksAsync(tenant);
        var secondSummary = await projector.ProjectActivePacksAsync(tenant);
        var readBack = await scheduleDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, "daily-inventory", "4.1.2", CancellationToken.None);

        Assert.Empty(firstSummary.Refusals);
        Assert.Empty(secondSummary.Refusals);
        Assert.NotNull(readBack);
        Assert.Equal("Daily inventory", readBack.Title);
        Assert.Equal("4.1.2", readBack.Version);
        // ASSUMPTION: Because the assumed registry surface has no enumeration API, successful
        // tuple read-back plus the conflict semantics implies the tuple has single storage.
    }

    [Fact]
    public async Task Unknown_schedule_kind_is_refused()
    {
        const string tenantValue = "aaaaaaaa-0000-0000-0000-000000000035";
        var tenant = new TenantId(tenantValue);
        var definition = CreateCivilianDefinition(
            tenantValue,
            "unknown-kind-schedule",
            "1.0.0",
            "Unknown kind schedule",
            scheduleKind: "harborline.scheduling-definition-draft/unknown");
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
        var scheduleDefinitionRegistry = new InMemoryScheduleDefinitionRegistry(new RefuseUnknownKindDescriptorRegistry());
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            scheduleDefinitions: scheduleDefinitionRegistry, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(tenant);

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.ScheduleDefinition, refusal.ContentKind);
        // Descriptor refusals (kind unknown, body invalid) fold into the malformed
        // family; only registry pinned-tuple governance maps to pinned_tuple_conflict.
        Assert.Equal("pack.schedule-definition.malformed", refusal.Code);
        Assert.Null(await scheduleDefinitionRegistry.GetDefinitionAsync(
            tenant.Value, "unknown-kind-schedule", "1.0.0", CancellationToken.None));
    }

    private static ScheduleDefinition CreateCivilianDefinition(
        string tenant,
        string key,
        string version,
        string title,
        string scheduleKind = "harborline.scheduling-definition-draft/v0")
    {
        return new ScheduleDefinition
        {
            Key = key,
            Version = version,
            Tenant = tenant,
            SchemaVersion = 1,
            ScheduleKind = scheduleKind,
            Title = title,
            Body = CreateSchedulingBody(title),
            Provenance = JsonDocument.Parse("""
                {"derivedAt":"2026-08-18T12:00:00+00:00","exportingActor":"schedule-definition-test-author","originPackKey":"schedule-definition.test","tier":"Civilian"}
                """).RootElement.Clone(),
        };
    }

    private static JsonElement CreateSchedulingBody(string title) =>
        JsonSerializer.SerializeToElement(new
        {
            schema = "harborline.scheduling-definition-draft/v0",
            title,
            timezone = "UTC",
            activities = new[] { new { id = "intro-visit", durationMinutes = 30 } },
            resourceRequirements = Array.Empty<string>(),
        });

    private static async Task InstallAndActivatePackAsync(
        InMemoryPackInstallStore store,
        TenantId tenant,
        string itemKey,
        string itemVersion,
        JsonNode definition,
        TrustScope trustScope,
        KeyPair keyPair,
        string packKey = "schedule-definition.test",
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
                Name: "Schedule definition test pack",
                Description: "Exercises schedule-definition pack projection.",
                ScopeTier: PackScopeTier.Horizontal,
                Contents:
                [
                    new PackContentSource(
                        itemKey,
                        PackContentKind.ScheduleDefinition,
                        itemVersion,
                        definition),
                ],
                Dependencies: Array.Empty<PackDependencyRef>(),
                CapabilityRequirements: Array.Empty<string>(),
                Epoch: 1,
                Dcp: DomainComplianceProfile.General("schedule-definition-test-author")),
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

    private sealed class AcceptAllDescriptorRegistry : IScheduleDefinitionDescriptorRegistry
    {
        public ValueTask AdmitAsync(ScheduleDefinition definition, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class RefuseUnknownKindDescriptorRegistry : IScheduleDefinitionDescriptorRegistry
    {
        public ValueTask AdmitAsync(ScheduleDefinition definition, CancellationToken cancellationToken = default)
            => throw new ScheduleDefinitionGovernanceException("schedule_definition.kind_unknown");
    }
}
