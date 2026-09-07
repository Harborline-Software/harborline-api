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

/// <summary>
/// Ticket 152 — install PRESENCE-checks manifest-declared dependencies. The single-level-only rule
/// (A9, <c>PackValidator</c>) is unchanged; what was missing is that the ONE level v1 declares was never
/// checked to exist: a pack declaring a dependency that was never installed passed install silently.
/// Now every <c>manifest.Dependencies</c> entry must resolve to an installed pack at the pinned-or-newer
/// version or the install is REFUSED (hard, fail-closed) with
/// <see cref="PackInstallCodes.RefusedUnmetDependency"/> naming the missing dependency.
/// </summary>
public sealed class PackDependencyPresenceInstallTests : IDisposable
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000152");
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly KeyPair _keyPair = KeyPair.Generate();
    private readonly PackFileCodec _codec = new();
    private readonly InMemoryPackInstallStore _store = new();
    private readonly PackInstaller _installer;
    private readonly PackInstallContext _context;

    public PackDependencyPresenceInstallTests()
    {
        _installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), _codec),
            _store,
            new WorkflowRefusingPackContentAdmission(),
            new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        _context = new PackInstallContext(
            Tenant,
            new InMemoryPackTrustStore(
            [
                new PackTrustRoot(TrustScope.OwnRoster, _keyPair.PrincipalId, 1, TrustRootStatus.Current),
            ]),
            PackRevocationList.Empty,
            Now,
            TimeSpan.FromDays(30),
            Principal: "test-operator");
    }

    public void Dispose() => _keyPair.Dispose();

    [Fact(DisplayName = "ticket 152: a declared dependency that is not installed refuses install, naming it")]
    public async Task Install_With_Missing_Declared_Dependency_Is_Refused()
    {
        var dependent = await ExportAsync(
            "test.dependent", "1.0.0", new PackDependencyRef("test.dependency", "1.0.0"));

        var outcome = _installer.Install(dependent, _context);

        Assert.False(outcome.Installed);
        Assert.Contains(PackInstallCodes.RefusedUnmetDependency, outcome.RefusalCodes);
        var unmet = Assert.Single(outcome.Preview.UnmetDependencies);
        Assert.Equal("test.dependency", unmet.DependencyKey);
        Assert.Equal("1.0.0", unmet.PinnedVersion);
        Assert.Null(unmet.InstalledVersion);
        Assert.Empty(_store.ListInstalled(Tenant));
    }

    [Fact(DisplayName = "ticket 152: an installed dependency below the pin still refuses, naming both versions")]
    public async Task Install_With_Below_Pin_Dependency_Is_Refused()
    {
        var dependency = await ExportAsync("test.dependency", "1.0.0");
        Assert.True(_installer.Install(dependency, _context).Installed);

        var dependent = await ExportAsync(
            "test.dependent", "1.0.0", new PackDependencyRef("test.dependency", "2.0.0"));
        var outcome = _installer.Install(dependent, _context);

        Assert.False(outcome.Installed);
        Assert.Contains(PackInstallCodes.RefusedUnmetDependency, outcome.RefusalCodes);
        var unmet = Assert.Single(outcome.Preview.UnmetDependencies);
        Assert.Equal("test.dependency", unmet.DependencyKey);
        Assert.Equal("2.0.0", unmet.PinnedVersion);
        Assert.Equal("1.0.0", unmet.InstalledVersion);
    }

    [Fact(DisplayName = "ticket 152 review: a pre-release pin orders per SemVer — alpha.9 does NOT satisfy an alpha.10 pin")]
    public async Task Install_With_PreRelease_Dependency_Below_Pin_Is_Refused()
    {
        var dependency = await ExportAsync("test.dependency", "1.0.0-alpha.9");
        Assert.True(_installer.Install(dependency, _context).Installed);

        // Ordinal comparison ranks "alpha.9" ABOVE "alpha.10" — the fail-open this pins closed.
        var dependent = await ExportAsync(
            "test.dependent", "1.0.0", new PackDependencyRef("test.dependency", "1.0.0-alpha.10"));
        var outcome = _installer.Install(dependent, _context);

        Assert.False(outcome.Installed);
        Assert.Contains(PackInstallCodes.RefusedUnmetDependency, outcome.RefusalCodes);
        var unmet = Assert.Single(outcome.Preview.UnmetDependencies);
        Assert.Equal("1.0.0-alpha.10", unmet.PinnedVersion);
        Assert.Equal("1.0.0-alpha.9", unmet.InstalledVersion);
        Assert.False(unmet.MalformedPin);
    }

    [Fact(DisplayName = "ticket 152 review: a dependency pin that does not parse is refused distinctly, never trivially satisfied")]
    public async Task Install_With_Malformed_Dependency_Pin_Is_Refused()
    {
        var dependency = await ExportAsync("test.dependency", "1.0.0");
        Assert.True(_installer.Install(dependency, _context).Installed);

        // The pin passes the exporter's pinned-version regex but overflows the comparator's integer
        // segments, so pre-fix it degraded to 0.0.0 and ANY installed version satisfied it (fail-open
        // on a restrict — ADR 0038). Now it refuses with its own code, unread.
        var dependent = await ExportAsync(
            "test.dependent", "1.0.0", new PackDependencyRef("test.dependency", "99999999999999999999.0.0"));
        var outcome = _installer.Install(dependent, _context);

        Assert.False(outcome.Installed);
        Assert.Contains(PackInstallCodes.RefusedMalformedDependencyPin, outcome.RefusalCodes);
        var unmet = Assert.Single(outcome.Preview.UnmetDependencies);
        Assert.Equal("test.dependency", unmet.DependencyKey);
        Assert.True(unmet.MalformedPin);
        Assert.Single(_store.ListInstalled(Tenant)); // only the dependency — nothing committed.
    }

    [Fact(DisplayName = "ticket 152: with the dependency installed (pinned-or-newer), install proceeds")]
    public async Task Install_With_Present_Dependency_Proceeds()
    {
        var dependency = await ExportAsync("test.dependency", "1.5.0");
        Assert.True(_installer.Install(dependency, _context).Installed);

        // Pin 1.0.0 — a NEWER installed version satisfies (S-8 monotonic installs).
        var dependent = await ExportAsync(
            "test.dependent", "1.0.0", new PackDependencyRef("test.dependency", "1.0.0"));
        var outcome = _installer.Install(dependent, _context);

        Assert.True(outcome.Installed, string.Join("; ", outcome.RefusalCodes));
        Assert.Empty(outcome.Preview.UnmetDependencies);
        Assert.Equal(2, _store.ListInstalled(Tenant).Count);
    }

    private async Task<byte[]> ExportAsync(string key, string version, params PackDependencyRef[] dependencies)
    {
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            _codec, timeProvider: TimeProvider.System);
        var export = await exporter.ExportAsync(
            new PackExportRequest(
                Key: key,
                Version: version,
                Name: $"Dependency presence test pack {key}",
                Description: "Exercises the ticket 152 dependency presence check.",
                ScopeTier: PackScopeTier.Horizontal,
                Contents:
                [
                    new PackContentSource(
                        $"{key}.form",
                        PackContentKind.FormDefinition,
                        version,
                        new JsonObject { ["title"] = key }),
                ],
                Dependencies: dependencies,
                CapabilityRequirements: Array.Empty<string>(),
                Epoch: 1,
                Dcp: DomainComplianceProfile.General("test-author")),
            new Ed25519Signer(_keyPair));
        Assert.True(export.Succeeded, string.Join(
            "; ", export.Validation.Errors.Select(error => $"{error.Code}: {error.Message}")));
        return export.FileBytes!;
    }
}
