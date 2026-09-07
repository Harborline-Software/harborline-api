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
/// Ticket 151 — the kernel install path carries a PRINCIPAL. <see cref="IPackInstaller.Install"/> is the
/// commit seam, so the DOMAIN layer requires <see cref="PackInstallContext.Principal"/>: a compiled caller
/// that skips the host route (and with it the <c>packages:operate</c> check) cannot commit a seed layer
/// anonymously. Preview stays principal-free (it never mutates).
/// </summary>
public sealed class PackInstallPrincipalTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000151");
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "ticket 199: install with no principal records an ordinary pre-decision refusal")]
    public async Task Install_Without_Principal_Is_Refused()
    {
        using var fixture = await CreateFixtureAsync();

        var anonymous = fixture.Context; // no Principal
        Assert.Throws<ArgumentNullException>(() => fixture.Installer.Install(fixture.PackBytes, anonymous));
        Assert.Empty(fixture.Store.ListInstalled(Tenant));
        var refusal = Assert.Single(fixture.Audit.Query(Tenant));
        Assert.True(refusal.PreDecision);
        Assert.Equal(PackInstallCodes.RefusedNoPrincipal, refusal.Detail);
    }

    [Fact(DisplayName = "ticket 151: the same install WITH a principal commits (control)")]
    public async Task Install_With_Principal_Commits()
    {
        using var fixture = await CreateFixtureAsync();

        var outcome = fixture.Installer.Install(
            fixture.PackBytes, fixture.Context with { Principal = "test-operator" });

        Assert.True(outcome.Installed);
        Assert.Single(fixture.Store.ListInstalled(Tenant));
    }

    [Fact(DisplayName = "review: install audit rows record WHO — success and refusal both carry the acting principal")]
    public async Task Install_Audit_Rows_Carry_The_Acting_Principal()
    {
        using var fixture = await CreateFixtureAsync();
        var principled = fixture.Context with { Principal = "test-operator" };

        Assert.True(fixture.Installer.Install(fixture.PackBytes, principled).Installed);
        // A tampered copy: verification refuses (S-7) — the refusal row must still record WHO tried.
        var tampered = (byte[])fixture.PackBytes.Clone();
        tampered[^1] ^= 0xFF;
        Assert.False(fixture.Installer.Install(tampered, principled).Installed);

        var entries = fixture.Audit.Query(Tenant);
        var installed = Assert.Single(entries, e => e.Action == PackInstallAuditAction.Installed);
        Assert.Equal("test-operator", installed.ActingPrincipal);
        var refused = Assert.Single(entries, e => e.Action == PackInstallAuditAction.Refused);
        Assert.Equal("test-operator", refused.ActingPrincipal);
    }

    [Fact(DisplayName = "ticket 199: ACTIVATE audits missing authority as a pre-decision refusal and attributes admitted writes")]
    public async Task Activate_Requires_And_Records_The_Acting_Principal()
    {
        using var fixture = await CreateFixtureAsync();
        var outcome = fixture.Installer.Install(
            fixture.PackBytes, fixture.Context with { Principal = "test-operator" });
        Assert.True(outcome.Installed);

        Assert.Throws<ArgumentNullException>(() =>
            fixture.Installer.Activate(Tenant, outcome.PackKey, outcome.Version, Now));
        var refusal = Assert.Single(fixture.Audit.Query(Tenant), e => e.PreDecision);
        Assert.Equal(PackInstallCodes.ActivateRefusedNoPrincipal, refusal.Detail);

        // With the principal: the flip proceeds and the audit row records WHO.
        var activated = fixture.Installer.Activate(
            Tenant, outcome.PackKey, outcome.Version, Now, "test-operator");
        Assert.True(activated.Activated, activated.Error);
        var activatedRow = Assert.Single(
            fixture.Audit.Query(Tenant), e => e.Action == PackInstallAuditAction.Activated);
        Assert.Equal("test-operator", activatedRow.ActingPrincipal);
    }

    [Fact(DisplayName = "ticket 199: DEACTIVATE audits missing authority as a pre-decision refusal and attributes admitted writes")]
    public async Task Deactivate_Requires_And_Records_The_Acting_Principal()
    {
        using var fixture = await CreateFixtureAsync();
        var outcome = fixture.Installer.Install(
            fixture.PackBytes, fixture.Context with { Principal = "test-operator" });
        Assert.True(outcome.Installed);
        Assert.True(fixture.Installer.Activate(
            Tenant, outcome.PackKey, outcome.Version, Now, "test-operator").Activated);

        Assert.Throws<ArgumentNullException>(() =>
            fixture.Installer.Deactivate(Tenant, outcome.PackKey, outcome.Version, Now));
        var refusal = Assert.Single(fixture.Audit.Query(Tenant), e => e.PreDecision);
        Assert.Equal(PackInstallCodes.DeactivateRefusedNoPrincipal, refusal.Detail);

        var deactivated = fixture.Installer.Deactivate(
            Tenant, outcome.PackKey, outcome.Version, Now, "test-operator");
        Assert.True(deactivated.Deactivated, deactivated.Error);
        var deactivatedRow = Assert.Single(
            fixture.Audit.Query(Tenant), e => e.Action == PackInstallAuditAction.Deactivated);
        Assert.Equal("test-operator", deactivatedRow.ActingPrincipal);
    }

    [Fact(DisplayName = "ticket 151: preview stays principal-free (it never mutates)")]
    public async Task Preview_Without_Principal_Still_Plans()
    {
        using var fixture = await CreateFixtureAsync();

        var preview = fixture.Installer.Preview(fixture.PackBytes, fixture.Context);

        Assert.Equal(PackInstallVerdict.WouldInstall, preview.Verdict);
        Assert.Empty(fixture.Store.ListInstalled(Tenant));
    }

    [Theory]
    [InlineData(true, false, PackInstallCodes.RefusedBlankPackKey)]
    [InlineData(false, true, PackInstallCodes.RefusedBlankVersion)]
    public async Task Activate_BlankCoordinate_RecordsPreDecisionRefusal(
        bool blankKey, bool blankVersion, string expectedCode)
    {
        using var fixture = await CreateFixtureAsync();
        Assert.ThrowsAny<ArgumentException>(() => fixture.Installer.Activate(
            Tenant,
            blankKey ? " " : "pack-a",
            blankVersion ? " " : "1.0.0",
            Now,
            "test-operator"));
        var refusal = Assert.Single(fixture.Audit.Query(Tenant));
        Assert.True(refusal.PreDecision);
        Assert.Equal(expectedCode, refusal.Detail);
    }

    [Fact]
    public async Task DeniedGate_RecordsPreDecisionRefusal()
    {
        using var fixture = await CreateFixtureAsync(allowed: false);
        Assert.Throws<Harborline.Api.Foundation.Authorization.AuthorizationDeniedException>(() =>
            fixture.Installer.Install(fixture.PackBytes, fixture.Context with { Principal = "test-operator" }));
        var refusal = Assert.Single(fixture.Audit.Query(Tenant));
        Assert.True(refusal.PreDecision);
        Assert.Equal(PackInstallCodes.RefusedAuthorizationDenied, refusal.Detail);
        Assert.Empty(fixture.Store.ListInstalled(Tenant));
    }

    private static async Task<Fixture> CreateFixtureAsync(bool allowed = true)
    {
        var keyPair = KeyPair.Generate();
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec, timeProvider: TimeProvider.System);
        var export = await exporter.ExportAsync(
            new PackExportRequest(
                Key: "test.install-principal",
                Version: "1.0.0",
                Name: "Install principal test pack",
                Description: "Exercises the ticket 151 principal requirement.",
                ScopeTier: PackScopeTier.Horizontal,
                Contents:
                [
                    new PackContentSource(
                        "principal-test-form",
                        PackContentKind.FormDefinition,
                        "1.0.0",
                        new JsonObject { ["title"] = "principal test" }),
                ],
                Dependencies: Array.Empty<PackDependencyRef>(),
                CapabilityRequirements: Array.Empty<string>(),
                Epoch: 1,
                Dcp: DomainComplianceProfile.General("test-author")),
            new Ed25519Signer(keyPair));
        Assert.True(export.Succeeded, string.Join(
            "; ", export.Validation.Errors.Select(error => $"{error.Code}: {error.Message}")));

        var trustStore = new InMemoryPackTrustStore(
        [
            new PackTrustRoot(TrustScope.OwnRoster, keyPair.PrincipalId, 1, TrustRootStatus.Current),
        ]);
        var store = new InMemoryPackInstallStore();
        var audit = new InMemoryPackInstallAudit();
        var installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), codec),
            store,
            new WorkflowRefusingPackContentAdmission(),
            audit,
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.Gate(allowed));
        var context = new PackInstallContext(
            Tenant,
            trustStore,
            PackRevocationList.Empty,
            Now,
            TimeSpan.FromDays(30));

        return new Fixture(installer, context, store, audit, export.FileBytes!, keyPair);
    }

    private sealed record Fixture(
        PackInstaller Installer,
        PackInstallContext Context,
        InMemoryPackInstallStore Store,
        InMemoryPackInstallAudit Audit,
        byte[] PackBytes,
        KeyPair KeyPair) : IDisposable
    {
        public void Dispose() => KeyPair.Dispose();
    }
}
