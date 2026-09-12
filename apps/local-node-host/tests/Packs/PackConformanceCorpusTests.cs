using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
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
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>Ticket 401's compiler-style corpus: every committed pack is activated or refused by its directory.</summary>
public sealed class PackConformanceCorpusTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000401");
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "ticket 401: enumerated pack corpus activates accepted fixtures and pins refusal reasons")]
    public async Task Enumerated_corpus_activates_or_refuses_every_pack()
    {
        var root = Environment.GetEnvironmentVariable("HARBORLINE_PACK_CONFORMANCE_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "Conformance", "Packs");
        var activate = EnumerateCases(Path.Combine(root, "must-activate"), requiresExpectedReason: false);
        var refuse = EnumerateCases(Path.Combine(root, "must-refuse"), requiresExpectedReason: true);

        Assert.True(activate.Count > 0, $"Pack conformance corpus matched no must-activate packs under '{root}'.");
        Assert.True(refuse.Count > 0, $"Pack conformance corpus matched no must-refuse packs under '{root}'.");

        using var keyPair = KeyPair.Generate();
        using var services = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var store = new InMemoryPackInstallStore();
        var codec = new PackFileCodec();
        var installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), codec),
            store,
            new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator()),
            new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        var context = new PackInstallContext(
            Tenant,
            new InMemoryPackTrustStore(
            [
                new PackTrustRoot(TrustScope.OwnRoster, keyPair.PrincipalId, 1, TrustRootStatus.Current),
            ]),
            PackRevocationList.Empty,
            Now,
            TimeSpan.FromDays(30),
            Principal: "conformance-corpus");
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec,
            timeProvider: TimeProvider.System);
        var projector = new PackSeedProjector(
            store,
            services.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance,
            time: TimeProvider.System);

        foreach (var testCase in activate)
        {
            var exported = await ExportAsync(exporter, testCase.PackPath, keyPair);
            var installed = installer.Install(exported, context);
            Assert.True(installed.Installed,
                $"Must-activate pack '{testCase.PackPath}' was refused: {string.Join(", ", installed.RefusalCodes)}");
            var activation = installer.Activate(Tenant, installed.PackKey, installed.Version, Now, "conformance-corpus");
            Assert.True(activation.Activated,
                $"Must-activate pack '{testCase.PackPath}' was not activated: {activation.Error}: {activation.Detail}");
        }

        var projection = await projector.ProjectActivePacksAsync(Tenant);
        Assert.Empty(projection.Refusals);
        Assert.Empty(projection.PlatformRefusals);

        foreach (var testCase in refuse)
        {
            var exported = await ExportAsync(exporter, testCase.PackPath, keyPair);
            var outcome = installer.Install(exported, context);
            Assert.False(outcome.Installed, $"Must-refuse pack '{testCase.PackPath}' installed.");
            var actual = Assert.Single(outcome.RefusalCodes);
            Assert.Equal(testCase.ExpectedReason, actual);
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Harborline.Api.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Harborline.Api.slnx was not found above the test output directory.");
    }

    private static IReadOnlyList<CorpusCase> EnumerateCases(string directory, bool requiresExpectedReason)
    {
        Assert.True(Directory.Exists(directory), $"Pack conformance directory is missing: '{directory}'.");
        var cases = new List<CorpusCase>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(path => path, StringComparer.Ordinal))
        {
            Assert.True(Directory.Exists(entry), $"Pack conformance entry is not a case directory: '{entry}'.");
            var name = Path.GetFileName(entry);
            var expectedPack = Path.Combine(entry, $"{name}-pack.export.json");
            var expectedReason = Path.Combine(entry, "expected.json");
            // A shipped pack stays under _shared/packs (first-boot install and the operator CLI read it there);
            // the corpus refers to it with a pack.ref holding its repository-relative path instead of a copy that drifts.
            var packReference = Path.Combine(entry, "pack.ref");
            var allowed = requiresExpectedReason ? new[] { expectedPack, expectedReason } : new[] { expectedPack, packReference };
            foreach (var file in Directory.EnumerateFileSystemEntries(entry))
                Assert.Contains(file, allowed, StringComparer.Ordinal);
            if (File.Exists(packReference))
            {
                Assert.False(File.Exists(expectedPack), $"Pack conformance case has both a pack file and a pack.ref: '{entry}'.");
                var relative = File.ReadAllText(packReference).Trim();
                expectedPack = Path.GetFullPath(Path.Combine(RepositoryRoot(), relative));
            }
            Assert.True(File.Exists(expectedPack), $"Pack conformance case is missing its pack file: '{expectedPack}'.");
            if (!requiresExpectedReason)
            {
                cases.Add(new CorpusCase(expectedPack, null));
                continue;
            }

            Assert.True(File.Exists(expectedReason), $"Must-refuse case is missing expected.json: '{expectedReason}'.");
            using var expected = JsonDocument.Parse(File.ReadAllText(expectedReason));
            var reason = expected.RootElement.GetProperty("reason").GetString();
            Assert.False(string.IsNullOrWhiteSpace(reason), $"Must-refuse case has no reason: '{expectedReason}'.");
            cases.Add(new CorpusCase(expectedPack, reason));
        }

        return cases;
    }

    private static async Task<byte[]> ExportAsync(PackExporter exporter, string path, KeyPair keyPair)
    {
        var request = ReadRequest(path);
        var outcome = await exporter.ExportAsync(request, new Ed25519Signer(keyPair));
        Assert.True(outcome.Succeeded,
            $"Corpus pack '{path}' could not be exported: {string.Join(", ", outcome.Validation.Errors.Select(error => error.Code))}");
        return outcome.FileBytes!;
    }

    private static PackExportRequest ReadRequest(string path)
    {
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path)));
        var contents = Assert.IsType<JsonArray>(root["contents"])
            .Select(item => Assert.IsType<JsonObject>(item))
            .Select(item => new PackContentSource(
                item["key"]!.GetValue<string>(),
                Enum.Parse<PackContentKind>(item["kind"]!.GetValue<string>(), ignoreCase: false),
                item["version"]!.GetValue<string>(),
                item["content"]!.DeepClone()))
            .ToArray();
        var dependencies = root["dependencies"] is JsonArray dependencyArray
            ? dependencyArray.Select(item => Assert.IsType<JsonObject>(item)).Select(item => new PackDependencyRef(
                item["key"]!.GetValue<string>(), item["version"]!.GetValue<string>(),
                item["declaredDependencyKeys"] is JsonArray declared
                    ? declared.Select(value => value!.GetValue<string>()).ToArray()
                    : Array.Empty<string>())).ToArray()
            : Array.Empty<PackDependencyRef>();
        var capabilityRequirements = root["capabilityRequirements"] is JsonArray capabilities
            ? capabilities.Select(item => item!.GetValue<string>()).ToArray()
            : Array.Empty<string>();
        return new PackExportRequest(
            root["key"]!.GetValue<string>(),
            root["version"]!.GetValue<string>(),
            root["name"]!.GetValue<string>(),
            root["description"]!.GetValue<string>(),
            Enum.Parse<PackScopeTier>(root["scopeTier"]!.GetValue<string>(), ignoreCase: false),
            contents,
            dependencies,
            capabilityRequirements,
            Epoch: 1,
            Dcp: DomainComplianceProfile.General("conformance-corpus"));
    }

    private sealed record CorpusCase(string PackPath, string? ExpectedReason);
}
