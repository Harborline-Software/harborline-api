using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
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

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>T-396: an ADR 0006 requirement links only to an exact active pack interface claim.</summary>
public sealed class PackInterfaceActivationTests : IDisposable
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000396");
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    private readonly KeyPair _keyPair = KeyPair.Generate();
    private readonly InMemoryPackInstallStore _store = new();
    private readonly PackInstaller _installer;
    private readonly PackInstallContext _context;

    public PackInterfaceActivationTests()
    {
        var codec = new PackFileCodec();
        _installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), codec), _store,
            new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        _context = new PackInstallContext(Tenant, new InMemoryPackTrustStore(
            [new PackTrustRoot(TrustScope.OwnRoster, _keyPair.PrincipalId, 1, TrustRootStatus.Current)]),
            PackRevocationList.Empty, Now, TimeSpan.FromDays(30), Principal: "interface-test");
    }

    public void Dispose() => _keyPair.Dispose();

    [Fact(DisplayName = "T-396: access@2 refuses at activation by name and before the catalogue changes")]
    public async Task Activate_With_Unmet_Exact_Interface_Refuses_Before_Write()
    {
        var access = await ExportAsync("access", new JsonObject { ["title"] = "access parent" },
            PackContentKind.FormDefinition, exposes: ["access.definition"], interfaceVersion: 1);
        InstallAndActivate(access);
        var consumer = await ExportAsync("consumer", Referencing("access@2"), PackContentKind.AssetTypeDefinition,
            dependencies: [new PackDependencyRef("access", "1.0.0")]);
        var installed = _installer.Install(consumer, _context);
        Assert.True(installed.Installed, string.Join(",", installed.RefusalCodes));
        var before = CatalogueHash();

        var activation = _installer.Activate(_context, "consumer", "1.0.0");

        Assert.False(activation.Activated);
        Assert.Equal(PackInstallCodes.ActivateUnmetInterfaceRequirement, activation.Error);
        Assert.Contains("access@2", activation.Detail, StringComparison.Ordinal);
        Assert.Equal(new PackInstallRefusal(PackInstallCodes.ActivateUnmetInterfaceRequirement, "/contents/0/contentBase64"), activation.Refusal);
        Assert.Equal(before, CatalogueHash());
    }

    [Fact(DisplayName = "T-396: a cross-pack definition reference outside exposes refuses before the catalogue changes")]
    public async Task Activate_Into_Unexposed_Definition_Refuses_Before_Write()
    {
        var access = await ExportAsync("access", new JsonObject { ["title"] = "access parent" },
            PackContentKind.FormDefinition, exposes: ["access.other"], interfaceVersion: 1);
        InstallAndActivate(access);
        var consumer = await ExportAsync("consumer", Referencing("access@1"), PackContentKind.AssetTypeDefinition,
            dependencies: [new PackDependencyRef("access", "1.0.0")]);
        Assert.True(_installer.Install(consumer, _context).Installed);
        var before = CatalogueHash();

        var activation = _installer.Activate(_context, "consumer", "1.0.0");

        Assert.False(activation.Activated);
        Assert.Equal(PackInstallCodes.ActivateUnexposedDefinition, activation.Error);
        Assert.Equal(new PackInstallRefusal(PackInstallCodes.ActivateUnexposedDefinition, "/contents/0/contentBase64"), activation.Refusal);
        Assert.Equal(before, CatalogueHash());
    }

    [Fact(DisplayName = "T-396: the active catalogue retains a pack's frozen exposed interface")]
    public async Task Install_Persists_Exposes_And_InterfaceVersion()
    {
        var bytes = await ExportAsync("access", new JsonObject { ["title"] = "access parent" },
            PackContentKind.FormDefinition, exposes: ["access.definition"], interfaceVersion: 1);
        InstallAndActivate(bytes);

        var active = Assert.Single(_store.ListInstalled(Tenant));
        Assert.Equal(PackLifecycleState.Active, active.Lifecycle);
        Assert.Equal(new[] { "access.definition" }, active.Exposes);
        Assert.Equal(1, active.InterfaceVersion);
    }

    private void InstallAndActivate(byte[] bytes)
    {
        var installed = _installer.Install(bytes, _context);
        Assert.True(installed.Installed, string.Join(",", installed.RefusalCodes));
        Assert.True(_installer.Activate(_context, installed.PackKey, installed.Version).Activated);
    }

    private async Task<byte[]> ExportAsync(
        string key, JsonObject content, PackContentKind kind,
        IReadOnlyList<PackDependencyRef>? dependencies = null,
        IReadOnlyList<string>? exposes = null, int? interfaceVersion = null)
    {
        var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            new PackFileCodec(), TimeProvider.System);
        var outcome = await exporter.ExportAsync(new PackExportRequest(key, "1.0.0", key, "T-396 test pack",
            PackScopeTier.Horizontal, [new PackContentSource($"{key}.definition", kind, "1.0.0", content)],
            dependencies ?? Array.Empty<PackDependencyRef>(), Array.Empty<string>(), 1,
            Dcp: DomainComplianceProfile.General("test-author"), Exposes: exposes, InterfaceVersion: interfaceVersion),
            new Ed25519Signer(_keyPair));
        Assert.True(outcome.Succeeded, string.Join(",", outcome.Validation.Errors.Select(error => error.Code)));
        return outcome.FileBytes!;
    }

    private static JsonObject Referencing(string requirement) => new()
    {
        ["parentType"] = "access.definition",
        ["definitionEnvelope"] = new JsonObject
        {
            ["requires"] = new JsonArray(new JsonObject { ["capability"] = requirement }),
        },
    };

    private string CatalogueHash()
    {
        var json = JsonSerializer.Serialize(_store.ListInstalled(Tenant));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
