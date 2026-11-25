using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.Foundation.Packs.Install.Audit;

using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Authorization;
using Harborline.Blocks.BuilderDefinitions;
using System.Text.Json.Nodes;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>Composition-root proof that Layout's host boundary and health probe are shipping services.</summary>
public sealed class LayoutCompositionTests
{
    [Fact(DisplayName = "T-735: the final local-node composition resolves the Layout surface host and denial health check")]
    public async Task The_final_local_node_composition_resolves_the_layout_services()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"layout-composition-{Guid.NewGuid():N}");
        IServiceProvider? provider = null;
        try
        {
            await Assert.ThrowsAsync<CompositionProbeCompleteException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    [
                        "--environment=Production",
                        "--LocalNode:RootSeedHex=" + new string('7', 64),
                        "--LocalNode:MultiTeam:Enabled=false",
                        "--LocalNode:Diagnostics:CommsDiagnosticLogging=false",
                        "--Logging:EventLog:LogLevel:Default=None",
                    ],
                    sessionTokenOverride: "layout-composition-probe",
                    dataDirectory: directory,
                    installFootprintRootOverride: directory,
                    finalServiceProviderProbe: (services, factory) =>
                    {
                        provider = factory.CreateServiceProvider(factory.CreateBuilder(services));
                        _ = provider.GetRequiredService<LayoutSurfaceHost>();
                        _ = provider.GetRequiredService<LayoutDenialHealthCheck>();
                        throw new CompositionProbeCompleteException();
                    }));
        }
        finally
        {
            if (provider is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (provider is IDisposable disposable) disposable.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // Oracle: T-733/production-register-wiring acceptance 1 and 2; codes are specified literals.
    [Theory(DisplayName = "layout-bound-3 layout-bound-8: the composed installer uses the host registers")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Composed_installer_uses_the_selected_register(bool rule, bool omitted)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"layout-register-composition-{Guid.NewGuid():N}");
        IServiceProvider? provider = null;
        try
        {
            await Assert.ThrowsAsync<CompositionProbeCompleteException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    ["--environment=Production", "--LocalNode:RootSeedHex=" + new string('7', 64),
                     "--LocalNode:MultiTeam:Enabled=false", "--LocalNode:Diagnostics:CommsDiagnosticLogging=false",
                     "--Logging:EventLog:LogLevel:Default=None"],
                    sessionTokenOverride: "layout-register-probe", dataDirectory: directory,
                    installFootprintRootOverride: directory,
                    finalServiceProviderProbe: (services, factory) =>
                    {
                        services.AddSingleton(Tests.Authorization.TestAuthorization.AllowGate());
                        services.AddSingleton<IPackInstallAudit>(new InMemoryPackInstallAudit());
                        if (omitted)
                            services.AddSingleton(new LayoutPublishRegisters(new(),
                                new(SupplyFieldControls: rule, SupplyValidationRules: !rule)));
                        provider = factory.CreateServiceProvider(factory.CreateBuilder(services));
                        _ = provider.GetRequiredService<LayoutPublishRegisters>();
                        throw new CompositionProbeCompleteException();
                    }));
            var database = provider!.GetRequiredService<IDbContextFactory<NodeLocalPacksDbContext>>();
            await using (var db = await database.CreateDbContextAsync()) await db.Database.MigrateAsync();
            using var key = KeyPair.Generate();
            var codec = new PackFileCodec();
            var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
                codec, timeProvider: TimeProvider.System);
            var definition = LayoutDenialTestKit.RegisterSurface(rule ? "rule" : "text");
            var export = await exporter.ExportAsync(new PackExportRequest(
                "acme.registers", "1.0.0", "Registers", "T-733 composition fixture", PackScopeTier.Horizontal,
                [new PackContentSource("acme.surface", PackContentKind.Layout, "1.0.0",
                    JsonNode.Parse(LayoutDefinitionJson.SerializeCanonical(definition))!)], [], [], 1,
                Dcp: DomainComplianceProfile.General("test-author")), new Ed25519Signer(key));
            Assert.True(export.Succeeded);
            var context = new PackInstallContext(LayoutDenialTestKit.Tenant,
                new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, key.PrincipalId, 1, TrustRootStatus.Current)]),
                PackRevocationList.Empty, AdmittedInstant.FromRecordedAct(DateTimeOffset.UtcNow), TimeSpan.FromDays(30),
                Principal: "test-operator");
            var outcome = await provider!.GetRequiredService<IPackInstaller>().InstallAsync(export.FileBytes!, context);
            Assert.Contains("layout.pack.capability_unsupported", outcome.RefusalCodes);
            var code = rule ? "layout.capture.validation_rule_unknown" : "layout.capture.control_unknown";
            Assert.Equal(rule ? !omitted : omitted, outcome.RefusalCodes.Contains(code));
        }
        finally
        {
            if (provider is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (provider is IDisposable disposable) disposable.Dispose();
            // Preserve the composition's temporary database for inspection.
        }
    }

    private sealed class CompositionProbeCompleteException : Exception;
}
