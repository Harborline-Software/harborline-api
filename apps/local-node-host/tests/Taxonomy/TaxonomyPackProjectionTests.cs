using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
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
using Harborline.Api.Foundation.Taxonomy.Models;
using Harborline.Api.Foundation.Taxonomy.Services;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Taxonomy;

public sealed class TaxonomyPackProjectionTests
{
    [Fact]
    public void Pack_content_kind_maps_to_taxonomy_pillar()
    {
        var pillar = PackPillarMap.ForKind(PackContentKind.TaxonomyDefinition);

        Assert.Equal(PackPillar.Taxonomy, pillar);
        Assert.Equal("taxonomy", PackPillarMap.GenericNoun(PackContentKind.TaxonomyDefinition));
    }

    [Fact]
    public async Task Authoritative_taxonomy_from_tenant_owned_pack_is_refused()
    {
        var tenant = new TenantId("aaaaaaaa-0000-0000-0000-000000000027");
        var definition = new TaxonomyDefinition
        {
            Id = new TaxonomyDefinitionId("Sunfish", "Compliance", "Controls"),
            Version = new TaxonomyVersion(2, 3, 4),
            Governance = TaxonomyGovernanceRegime.Authoritative,
            Description = "Authoritative controls",
            Owner = ActorId.Harborline,
            PublishedAt = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero),
        };
        var store = new InMemoryPackInstallStore();
        using var keyPair = KeyPair.Generate();
        await InstallAndActivatePackAsync(store, tenant, definition, TrustScope.OwnRoster, keyPair);
        var taxonomyRegistry = new InMemoryTaxonomyRegistry(TimeProvider.System);
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            taxonomies: taxonomyRegistry, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(tenant);

        var refusal = Assert.Single(summary.Refusals);
        Assert.Equal(PackContentKind.TaxonomyDefinition, refusal.ContentKind);
        Assert.Equal("pack.taxonomy.authoritative_requires_vendor_pack", refusal.Code);
        Assert.Null(await taxonomyRegistry.GetDefinitionAsync(
            tenant, definition.Id, definition.Version, CancellationToken.None));
    }

    [Fact]
    public async Task Tenant_taxonomy_round_trip_preserves_version_and_lineage()
    {
        var tenant = new TenantId("bbbbbbbb-0000-0000-0000-000000000027");
        var lineage = new TaxonomyLineage
        {
            Operation = TaxonomyLineageOp.Extend,
            AncestorDefinition = new TaxonomyDefinitionId("Acme", "Operations", "BaseTerms"),
            AncestorVersion = new TaxonomyVersion(2, 1, 0),
            DerivedBy = new ActorId("acme-taxonomy-admin"),
            DerivedAt = new DateTimeOffset(2026, 7, 15, 9, 30, 0, TimeSpan.Zero),
            Reason = "Add regional operating terms",
        };
        var definition = new TaxonomyDefinition
        {
            Id = new TaxonomyDefinitionId("Acme", "Operations", "RegionalTerms"),
            Version = new TaxonomyVersion(3, 2, 1),
            Governance = TaxonomyGovernanceRegime.Civilian,
            Description = "Tenant-owned regional terms",
            Owner = new ActorId("acme-taxonomy-admin"),
            PublishedAt = new DateTimeOffset(2026, 8, 18, 13, 0, 0, TimeSpan.Zero),
            DerivedFrom = lineage,
        };
        var store = new InMemoryPackInstallStore();
        using var keyPair = KeyPair.Generate();
        await InstallAndActivatePackAsync(store, tenant, definition, TrustScope.OwnRoster, keyPair);
        var taxonomyRegistry = new InMemoryTaxonomyRegistry(TimeProvider.System);
        using var services = new ServiceCollection()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .BuildServiceProvider();
        IPackSeedProjector projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            taxonomies: taxonomyRegistry, time: TimeProvider.System);

        var summary = await projector.ProjectActivePacksAsync(tenant);
        var readBack = await taxonomyRegistry.GetDefinitionAsync(
            tenant, definition.Id, definition.Version, CancellationToken.None);

        Assert.Empty(summary.Refusals);
        Assert.NotNull(readBack);
        Assert.Equal(new TaxonomyVersion(3, 2, 1), readBack.Version);
        Assert.Equal(lineage, readBack.DerivedFrom);
    }

    private static async Task InstallAndActivatePackAsync(
        InMemoryPackInstallStore store,
        TenantId tenant,
        TaxonomyDefinition definition,
        TrustScope trustScope,
        KeyPair keyPair)
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
                Key: "taxonomy.test",
                Version: "1.0.0",
                Name: "Taxonomy test pack",
                Description: "Exercises taxonomy pack projection.",
                ScopeTier: PackScopeTier.Horizontal,
                Contents:
                [
                    new PackContentSource(
                        definition.Id.Value,
                        PackContentKind.TaxonomyDefinition,
                        definition.Version.ToString(),
                        JsonSerializer.SerializeToNode(definition)!),
                ],
                Dependencies: Array.Empty<PackDependencyRef>(),
                CapabilityRequirements: Array.Empty<string>(),
                Epoch: 1,
                Dcp: DomainComplianceProfile.General("taxonomy-test-author")),
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
}
