using Harborline.Api.Foundation.Authorization;
using System.Text.Json;
using System.Security.Cryptography;
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

    [Fact(DisplayName = "ticket 401: enumerated pack corpus activates accepted fixtures and pins refusal reason, code, and pointer")]
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
        PlatformPackTestPreload.Activate(store, Tenant);
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
            AdmittedInstant.FromRecordedAct(Now),
            TimeSpan.FromDays(30),
            Principal: "conformance-corpus");
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec,
            timeProvider: TimeProvider.System);
        var projector = PackProjectionTestFixture.Create(store, services.GetRequiredService<IEntityTypeRegistry>());
        ((IPackProjectionReconciler)installer).AttachProjector(projector);

        foreach (var testCase in activate)
        {
            var exported = await ExportAsync(exporter, testCase.PackPath, keyPair);
            var installed = await installer.InstallAsync(exported, context);
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
            if (testCase.ActivationSetup is { } setup)
            {
                using var activationServices = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
                var activationStore = new InMemoryPackInstallStore();
                PlatformPackTestPreload.Activate(activationStore, Tenant);
                var activationInstaller = new PackInstaller(
                    new PackVerifier(new Ed25519Verifier(), codec),
                    activationStore,
                    new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator()),
                    new InMemoryPackInstallAudit(),
                    Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
                var activationProjector = PackProjectionTestFixture.Create(
                    activationStore, activationServices.GetRequiredService<IEntityTypeRegistry>());
                ((IPackProjectionReconciler)activationInstaller).AttachProjector(activationProjector);

                var provider = await exporter.ExportAsync(new PackExportRequest(
                    setup.PackKey, "1.0.0", setup.PackKey, "T-396 corpus provider", PackScopeTier.Horizontal,
                    [new PackContentSource(setup.ContentKey, PackContentKind.FormDefinition, "1.0.0",
                        JsonSerializer.SerializeToNode(PackProjectionTestFixture.FormContent("Provider form"))!)],
                    Array.Empty<PackDependencyRef>(), Array.Empty<string>(), 1,
                    Dcp: DomainComplianceProfile.General("conformance-corpus"),
                    Exposes: setup.Exposes, InterfaceVersion: setup.InterfaceVersion), new Ed25519Signer(keyPair));
                Assert.True(provider.Succeeded, string.Join(",", provider.Validation.Errors.Select(error => error.Code)));
                var providerInstalled = await activationInstaller.InstallAsync(provider.FileBytes!, context);
                Assert.True(providerInstalled.Installed, string.Join(",", providerInstalled.RefusalCodes));
                Assert.True(activationInstaller.Activate(Tenant, providerInstalled.PackKey, providerInstalled.Version, Now, "conformance-corpus").Activated);
                var consumerInstalled = await activationInstaller.InstallAsync(exported, context);
                Assert.True(consumerInstalled.Installed, $"Must-refuse activation pack '{testCase.PackPath}' did not install.");
                var activation = activationInstaller.Activate(Tenant, consumerInstalled.PackKey, consumerInstalled.Version, Now, "conformance-corpus");
                Assert.False(activation.Activated, $"Must-refuse activation pack '{testCase.PackPath}' activated.");
                Assert.Equal(testCase.ExpectedReason, activation.Error);
                AssertExpectedRefusal(testCase, Assert.IsType<PackInstallRefusal>(activation.Refusal));
                continue;
            }
            var outcome = await installer.InstallAsync(exported, context);
            Assert.False(outcome.Installed, $"Must-refuse pack '{testCase.PackPath}' installed.");
            var actual = Assert.Single(outcome.RefusalCodes);
            Assert.Equal(testCase.ExpectedReason, actual);
            AssertExpectedRefusal(testCase, Assert.Single(outcome.Preview.Refusals));
        }
    }

    [Fact]
    public void Must_refuse_case_without_code_fails_naming_expected_file()
    {
        var root = Path.Combine(Path.GetTempPath(), $"harborline-pack-corpus-{Guid.NewGuid():N}");
        var entry = Path.Combine(root, "missing-code");
        var expected = Path.Combine(entry, "expected.json");
        Directory.CreateDirectory(entry);
        try
        {
            File.WriteAllText(Path.Combine(entry, "missing-code-pack.export.json"), "{}");
            File.WriteAllText(expected, """{ "reason": "refusal", "pointer": "/" }""");

            var exception = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => EnumerateCases(root, requiresExpectedReason: true));

            Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
            Assert.Contains("no code", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Must_refuse_case_with_wrong_code_fails_showing_expected_and_actual()
    {
        var testCase = new CorpusCase(
            "fixture/export.json",
            "refusal.reason",
            "refusal.expected-code",
            "/contents/0/contentBase64");

        var exception = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertExpectedRefusal(
            testCase,
            new PackInstallRefusal("refusal.actual-code", "/contents/0/contentBase64")));

        Assert.Contains("fixture/export.json", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Expected 'refusal.expected-code'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("actual 'refusal.actual-code'", exception.Message, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Harborline.Api.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Harborline.Api.slnx was not found above the test output directory.");
    }

    [Theory]
    [InlineData("1.3.0", "ef1308048dfc3553a912594ca3e9133222905581b64a315d709a743a3f24e1c4")]
    [InlineData("1.6.0", "7717911ff08ed71c8b51d2f2a59a633d147eef5934ffde0c6a5e8de22c456919")]
    public async Task Exact_legacy_platform_recipes_remain_real_verified_signed_inputs(string version, string digest)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Conformance", "Packs", "platform",
            $"platform-pack-{version}.export.json");
        var recipe = await File.ReadAllBytesAsync(path);
        Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(recipe)));
        using var key = KeyPair.Generate();
        var codec = new PackFileCodec();
        var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
        var bytes = await ExportAsync(exporter, path, key);
        var verification = new PackVerifier(new Ed25519Verifier(), codec).Verify(bytes,
            new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, key.PrincipalId, 1, TrustRootStatus.Current)]));
        Assert.Equal("Verified", verification.Verdict.ToString());
        var manifest = Assert.IsType<PackManifest>(verification.Manifest);
        Assert.Equal("harborline.platform", manifest.Key);
        Assert.Equal(version, manifest.Version);
        Assert.Contains(manifest.Contents, item => item.Key == "FormDefinition" && item.Kind == PackContentKind.RecordType);
        Assert.Equal(recipe, await File.ReadAllBytesAsync(path));
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
                cases.Add(new CorpusCase(expectedPack, null, null, null));
                continue;
            }

            Assert.True(File.Exists(expectedReason), $"Must-refuse case is missing expected.json: '{expectedReason}'.");
            using var expected = JsonDocument.Parse(File.ReadAllText(expectedReason));
            var reason = RequiredExpectation(expected.RootElement, "reason", expectedReason);
            var code = RequiredExpectation(expected.RootElement, "code", expectedReason);
            var pointer = RequiredExpectation(expected.RootElement, "pointer", expectedReason);
            ActivationSetup? activationSetup = null;
            if (expected.RootElement.TryGetProperty("activationSetup", out var setup))
            {
                var setupPack = RequiredExpectation(setup, "packKey", expectedReason);
                var setupContent = RequiredExpectation(setup, "contentKey", expectedReason);
                var setupVersion = setup.GetProperty("interfaceVersion").GetInt32();
                var exposes = setup.GetProperty("exposes").EnumerateArray().Select(value => value.GetString()!).ToArray();
                activationSetup = new ActivationSetup(setupPack, setupContent, setupVersion, exposes);
            }
            cases.Add(new CorpusCase(expectedPack, reason, code, pointer, activationSetup));
        }

        return cases;
    }

    private static string RequiredExpectation(JsonElement expected, string property, string path)
    {
        var present = expected.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString());
        Assert.True(present, $"Must-refuse case has no {property}: '{path}'.");
        return value.GetString()!;
    }

    private static void AssertExpectedRefusal(CorpusCase testCase, PackInstallRefusal actual)
    {
        Assert.True(string.Equals(testCase.ExpectedCode, actual.Code, StringComparison.Ordinal),
            $"Must-refuse pack '{testCase.PackPath}' has unexpected code. " +
            $"Expected '{testCase.ExpectedCode}'; actual '{actual.Code}'.");
        Assert.True(string.Equals(testCase.ExpectedPointer, actual.Pointer, StringComparison.Ordinal),
            $"Must-refuse pack '{testCase.PackPath}' has unexpected pointer. " +
            $"Expected '{testCase.ExpectedPointer}'; actual '{actual.Pointer}'.");
    }

    private static async Task<byte[]> ExportAsync(PackExporter exporter, string path, KeyPair keyPair)
    {
        if (Path.GetFileName(path) is "platform-pack-1.3.0.export.json" or "platform-pack-1.6.0.export.json")
            return await LegacyPackFixtureSigner.SignPlatformSnapshotAsync(
                await File.ReadAllBytesAsync(path), new Ed25519Signer(keyPair));
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
        var exposes = root["exposes"] is JsonArray exposed
            ? exposed.Select(item => item!.GetValue<string>()).ToArray()
            : null;
        var interfaceVersion = root["interfaceVersion"]?.GetValue<int>();
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
            Dcp: DomainComplianceProfile.General("conformance-corpus"),
            Exposes: exposes,
            InterfaceVersion: interfaceVersion);
    }

    private sealed record CorpusCase(
        string PackPath,
        string? ExpectedReason,
        string? ExpectedCode,
        string? ExpectedPointer,
        ActivationSetup? ActivationSetup = null);

    private sealed record ActivationSetup(
        string PackKey, string ContentKey, int InterfaceVersion, IReadOnlyList<string> Exposes);
}
