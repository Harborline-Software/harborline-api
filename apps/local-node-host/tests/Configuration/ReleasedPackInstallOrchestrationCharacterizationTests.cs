using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

using IPackProjectionParticipant = Harborline.Api.Foundation.Definitions.IPackProjectionParticipant;
using PackProjectionTransaction = Harborline.Api.Foundation.Definitions.PackProjectionTransaction;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// Characterizes the <see cref="ReleasedPackInstaller"/> orchestration outcomes that the release atomicity tests do
/// not pin: the literal outcome of each early refusal, the installed-owner conflict message, root widening, the
/// widening-before-gate order, the foreign-only coordinate, and the exporter, install-engine and activation refusals.
/// Every case runs the real installer over <see cref="ReleasedPackAtomicFixture"/>: the real exporter, install engine,
/// durable store and audit outbox. Each expected value is a literal written here, never a production constant, and
/// each refusal case proves that nothing was exported, installed, activated or narrowed that the outcome does not
/// admit, and that the live and durable state is unchanged.
/// </summary>
public sealed class ReleasedPackInstallOrchestrationCharacterizationTests
{
    private const string FinanceForm = """{"id":"forms/invoice"}""";
    private const string ForeignCode = "configuration-release-foreign-definition";

    [Fact]
    public async Task Unoffered_digest_refuses_with_a_literal_outcome_and_runs_nothing()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        var unoffered = new string('0', 64);
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();

        var outcome = await fixture.InstallAsync(unoffered);

        Assert.False(outcome.Installed);
        Assert.False(outcome.Succeeded);
        Assert.Equal((unoffered, "", ""), (outcome.ReleasedDigest, outcome.PackKey, outcome.Version));
        var refusal = Assert.Single(outcome.Refusals);
        Assert.Equal(("configuration-release-missing", "digest", "No Released package is offered under that digest."),
            (refusal.Code, refusal.Target, refusal.Message));
        Assert.Null(outcome.Narrowed);
        Assert.Equal((0, 0, 0, 0), (fixture.Exporter.Exports, fixture.Installer.Installs,
            fixture.Installer.Activations, fixture.Installer.Narrowings));
        Assert.Equal(live, fixture.LiveState());
        Assert.Equal(durable, await fixture.DurableStateAsync());
    }

    [Fact]
    public async Task Unverifiable_signature_refuses_before_export_and_runs_nothing()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0", [ReleasedPackAtomicFixture.OwnNote]));
        // The stored bytes and digest still match; only the signed payload no longer matches its signature.
        await using (var context = fixture.Harness.Store.CreatePacksContext())
        {
            var row = context.ReleasedPackages.Single(item => item.Digest == digest);
            var signed = JsonNode.Parse(row.SignatureJson)!;
            signed["payload"]!["packageKey"] = "tenant.forged";
            row.SignatureJson = signed.ToJsonString();
            await context.SaveChangesAsync();
        }
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();

        var outcome = await fixture.InstallAsync(digest);

        Assert.False(outcome.Installed);
        Assert.Equal((digest, "", ""), (outcome.ReleasedDigest, outcome.PackKey, outcome.Version));
        var refusal = Assert.Single(outcome.Refusals);
        Assert.Equal(("configuration-release-signature-invalid", "signature",
                "The Released package's signature does not verify over the bytes it is offered with."),
            (refusal.Code, refusal.Target, refusal.Message));
        Assert.Equal((0, 0, 0, 0), (fixture.Exporter.Exports, fixture.Installer.Installs,
            fixture.Installer.Activations, fixture.Installer.Narrowings));
        Assert.Equal(live, fixture.LiveState());
        Assert.Equal(durable, await fixture.DurableStateAsync());
    }

    /// <summary>
    /// ck-2 S8: an own edit of a key another installed pack ships is refused by name before anything is exported,
    /// and the release's own unconflicted definition is not installed either.
    /// </summary>
    [Fact]
    public async Task Own_definition_already_shipped_by_another_installed_pack_refuses_before_export()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.OwnNote, ("forms/invoice", ReleasedPackAtomicFixture.ReleaseKey, "FormDefinition", FinanceForm)]));
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();

        var outcome = await fixture.InstallAsync(digest);

        Assert.False(outcome.Installed);
        Assert.Equal((digest, "tenant.release", "1.1.0"), (outcome.ReleasedDigest, outcome.PackKey, outcome.Version));
        var refusal = Assert.Single(outcome.Refusals);
        Assert.Equal((ForeignCode, "forms/invoice",
                "The released definition 'forms/invoice' belongs to package 'acme.finance', and a released package never takes over another package's definition: the edit names the release as its package, but that package already ships the key."),
            (refusal.Code, refusal.Target, refusal.Message));
        Assert.Null(outcome.Narrowed);
        Assert.Equal((0, 0, 0, 0), (fixture.Exporter.Exports, fixture.Installer.Installs,
            fixture.Installer.Activations, fixture.Installer.Narrowings));
        Assert.Equal(live, fixture.LiveState());
        Assert.Equal(durable, await fixture.DurableStateAsync());
        Assert.Empty(fixture.Audit.Successes());
    }

    /// <summary>A non-object body replaces the owner's definition wholesale, which widens it at the root.</summary>
    [Fact]
    public async Task Root_replacement_of_a_foreign_definition_is_refused_as_a_widening_at_the_root()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [("forms/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", """["replaced"]""")],
            (ReleasedPackAtomicFixture.Finance, "1.0.0")));
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();

        var outcome = await fixture.InstallAsync(digest);

        Assert.False(outcome.Installed);
        // A foreign-only release names its own package key but has no own version.
        Assert.Equal((digest, "tenant.release", ""), (outcome.ReleasedDigest, outcome.PackKey, outcome.Version));
        var refusal = Assert.Single(outcome.Refusals);
        Assert.Equal((ForeignCode, "forms/invoice",
                "The released definition 'forms/invoice' belongs to package 'acme.finance', and a released package never takes over another package's definition: the edit widens it at '/', so it is not a narrowing."),
            (refusal.Code, refusal.Target, refusal.Message));
        Assert.Equal((0, 0, 0, 0), (fixture.Exporter.Exports, fixture.Installer.Installs,
            fixture.Installer.Activations, fixture.Installer.Narrowings));
        Assert.Equal(live, fixture.LiveState());
        Assert.Equal(durable, await fixture.DurableStateAsync());
    }

    /// <summary>
    /// A widening is refused for what it is, and the edit is not then also decided by the owner's gate. The control
    /// release proves this gate refuses acme.finance, so a widening that reached it would carry a second refusal.
    /// </summary>
    [Fact]
    public async Task Widening_refusal_is_final_and_does_not_reach_the_owner_gate()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync(
            releaseGate: TestPackGate.ScopedTo(ReleasedPackAtomicFixture.ReleaseKey));
        var widening = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [("forms/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition",
                """{"id":"forms/invoice","pack":"acme.finance","extra":true}""")],
            (ReleasedPackAtomicFixture.Finance, "1.0.0")));
        var narrowing = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [("forms/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", FinanceForm)],
            (ReleasedPackAtomicFixture.Finance, "1.0.0")));
        var durable = await fixture.DurableStateAsync();

        var refused = await fixture.InstallAsync(widening);
        var control = await fixture.InstallAsync(narrowing);

        var refusal = Assert.Single(refused.Refusals);
        Assert.Equal((ForeignCode, "forms/invoice",
                "The released definition 'forms/invoice' belongs to package 'acme.finance', and a released package never takes over another package's definition: the edit widens it at '/extra', so it is not a narrowing."),
            (refusal.Code, refusal.Target, refusal.Message));
        var denied = Assert.Single(control.Refusals);
        Assert.Equal((ForeignCode, "forms/invoice",
                "The released definition 'forms/invoice' belongs to package 'acme.finance', and a released package never takes over another package's definition: the caller may not narrow that package."),
            (denied.Code, denied.Target, denied.Message));
        Assert.Equal((0, 0, 0, 0), (fixture.Exporter.Exports, fixture.Installer.Installs,
            fixture.Installer.Activations, fixture.Installer.Narrowings));
        Assert.Equal(durable, await fixture.DurableStateAsync());
    }

    /// <summary>
    /// A release with no own definition installs no pack, so its success names no own pack coordinate. The overlay and
    /// counters are pinned by the atomicity foreign-only tests; this pins only the outcome coordinate.
    /// </summary>
    [Fact]
    public async Task Foreign_only_success_reports_no_own_pack_coordinate()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [("forms/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", FinanceForm)],
            (ReleasedPackAtomicFixture.Finance, "1.0.0")));

        var outcome = await fixture.InstallAsync(digest);

        Assert.True(outcome.Succeeded, string.Join(";", outcome.Refusals.Select(refusal => refusal.Message)));
        Assert.False(outcome.Installed);
        Assert.Equal((digest, "", ""), (outcome.ReleasedDigest, outcome.PackKey, outcome.Version));
        Assert.Equal(["forms/invoice"], outcome.Narrowed);
    }

    /// <summary>
    /// The real exporter refuses to sign a definition that embeds a concrete email. Its finding is the refusal, word
    /// for word, and neither the own pack nor the release's admitted narrowing of acme.finance is written.
    /// </summary>
    [Fact]
    public async Task Real_exporter_refusal_installs_nothing_and_narrows_nothing()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [("forms/tenant-note", ReleasedPackAtomicFixture.ReleaseKey, "FormDefinition",
                """{"formId":"tenant-note","sections":[{"id":"header","fields":["ops@example.com"]}]}"""),
             ("forms/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", FinanceForm)],
            (ReleasedPackAtomicFixture.Finance, "1.0.0")));
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();

        var outcome = await fixture.InstallAsync(digest);

        Assert.False(outcome.Installed);
        Assert.Equal((digest, "tenant.release", "1.1.0"), (outcome.ReleasedDigest, outcome.PackKey, outcome.Version));
        var refusal = Assert.Single(outcome.Refusals);
        Assert.Equal(("pack.validation.content.pii.embedded_email", "forms/tenant-note:$.sections[0].fields[0]",
                "content 'forms/tenant-note' embeds a concrete email at $.sections[0].fields[0] — use a role/placeholder, never a real address."),
            (refusal.Code, refusal.Target, refusal.Message));
        Assert.Null(outcome.Narrowed);
        Assert.Equal((1, 0, 0, 0), (fixture.Exporter.Exports, fixture.Installer.Installs,
            fixture.Installer.Activations, fixture.Installer.Narrowings));
        Assert.Equal(live, fixture.LiveState());
        Assert.Equal(durable, await fixture.DurableStateAsync());
        Assert.Empty(fixture.Audit.Successes());
    }

    /// <summary>An exporter finding that names no target is reported against the released package.</summary>
    [Fact]
    public async Task Exporter_refusal_without_a_target_names_the_released_package()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        var exporter = new VerdictExporter(fixture.Exporter, _ => new PackExportOutcome(false, null, null,
            PackValidationResult.Invalid([new PackValidationError("test.export.untargeted", null, "untargeted finding")])));
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0", [ReleasedPackAtomicFixture.OwnNote]));
        var durable = await fixture.DurableStateAsync();

        var outcome = await InstallAsync(fixture, Releases(fixture, exporter: exporter), digest);

        Assert.False(outcome.Installed);
        Assert.Equal(("tenant.release", "1.1.0"), (outcome.PackKey, outcome.Version));
        var refusal = Assert.Single(outcome.Refusals);
        Assert.Equal(("test.export.untargeted", "releasedPackage", "untargeted finding"),
            (refusal.Code, refusal.Target, refusal.Message));
        Assert.Equal((1, 0, 0), (exporter.Exports, fixture.Installer.Installs, fixture.Installer.Activations));
        Assert.Equal(durable, await fixture.DurableStateAsync());
    }

    /// <summary>
    /// T-909 ck-1 through the real engine: the engine's own refusal code is reported against the released package,
    /// nothing is activated, and the refused install leaves no row.
    /// </summary>
    [Fact]
    public async Task Install_engine_refusal_reports_the_engine_code_and_activates_nothing()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [("kernel.field", ReleasedPackAtomicFixture.ReleaseKey, "RecordType", """{"sealed":true}""")]));
        var live = fixture.LiveState();
        var durable = await fixture.DurableStateAsync();

        var outcome = await fixture.InstallAsync(digest);

        Assert.False(outcome.Installed);
        Assert.Equal((digest, "tenant.release", "1.1.0"), (outcome.ReleasedDigest, outcome.PackKey, outcome.Version));
        var refusal = Assert.Single(outcome.Refusals);
        Assert.Equal(("kernel.compiled-shape-replacement", "releasedPackage", "The Released package was refused by the install engine."),
            (refusal.Code, refusal.Target, refusal.Message));
        Assert.Equal((1, 1, 0, 0), (fixture.Exporter.Exports, fixture.Installer.Installs,
            fixture.Installer.Activations, fixture.Installer.Narrowings));
        Assert.Empty(fixture.Projector.Published);
        Assert.Equal(live, fixture.LiveState());
        Assert.Equal(durable, await fixture.DurableStateAsync());
        Assert.Empty(fixture.Audit.Successes());
    }

    /// <summary>
    /// An activation refusal that states no error is reported with the installer's stated fallback text. The production
    /// engine always states one, so only an <see cref="IPackInstaller"/> double reaches this; the install it staged is
    /// still discarded.
    /// </summary>
    [Fact]
    public async Task Activation_refusal_without_an_engine_error_uses_the_stated_fallback()
    {
        await using var fixture = await ReleasedPackAtomicFixture.CreateAsync();
        var installer = new ErrorlessActivationRefusal(fixture.Installer);
        var digest = await fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0", [ReleasedPackAtomicFixture.OwnNote]));
        var durable = await fixture.DurableStateAsync();

        var outcome = await InstallAsync(fixture, Releases(fixture, installer: installer), digest);

        Assert.False(outcome.Installed);
        Assert.Equal(("tenant.release", "1.1.0"), (outcome.PackKey, outcome.Version));
        var refusal = Assert.Single(outcome.Refusals);
        Assert.Equal(("configuration-release-activation-refused", "releasedPackage", "Activation was refused."),
            (refusal.Code, refusal.Target, refusal.Message));
        Assert.Equal((1, 1, 0), (fixture.Installer.Installs, installer.Activations, fixture.Installer.Activations));
        Assert.Null(fixture.Store.GetVersion(fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey, "1.1.0"));
        Assert.Equal(durable, await fixture.DurableStateAsync());
    }

    /// <summary>The fixture's installer composition with one seam replaced.</summary>
    private static ReleasedPackInstaller Releases(ReleasedPackAtomicFixture fixture, IPackExporter? exporter = null,
        IPackInstaller? installer = null) =>
        new(fixture.Proposals, exporter ?? fixture.Exporter, installer ?? fixture.Installer, fixture.Node, new Ed25519Verifier(),
            HostedPackInstallApiEndpoint.BuildTrustStore(fixture.Node, NullLogger.Instance), PackRevocationList.Empty,
            fixture.Reader, TestPackGate.AllowAll());

    private static ValueTask<ReleasedPackInstallOutcome> InstallAsync(ReleasedPackAtomicFixture fixture,
        ReleasedPackInstaller releases, string digest) =>
        releases.InstallAsync(fixture.Tenant, digest,
            TestAuthorization.Write(fixture.Tenant, ReleasedPackAtomicFixture.Principal, ReleasedPackAtomicFixture.Frozen));

    /// <summary>Runs the real export, then replaces its outcome with <paramref name="verdict"/>'s.</summary>
    private sealed class VerdictExporter(IPackExporter inner, Func<PackExportOutcome, PackExportOutcome> verdict) : IPackExporter
    {
        private int _exports;

        internal int Exports => _exports;

        public ValueTask<PackValidationResult> ValidateAsync(PackExportRequest request, CancellationToken ct = default) =>
            inner.ValidateAsync(request, ct);

        public async ValueTask<PackExportOutcome> ExportAsync(PackExportRequest request, IOperationSigner signer,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _exports);
            return verdict(await inner.ExportAsync(request, signer, ct));
        }
    }

    /// <summary>Delegates everything to the recording installer except activation, which it refuses with no error.</summary>
    private sealed class ErrorlessActivationRefusal(RecordingInstaller inner) : IPackInstaller, IPackProjectionParticipant
    {
        private int _activations;

        internal int Activations => _activations;

        public void StageProjection(PackProjectionTransaction transaction) => inner.StageProjection(transaction);

        public PackInstallPreview Preview(ReadOnlySpan<byte> packBytes, PackInstallContext context) => inner.Preview(packBytes, context);

        public PackInstallPreview Check(ReadOnlySpan<byte> packBytes, PackInstallContext context) => inner.Check(packBytes, context);

        public Task<PackInstallOutcome> InstallAsync(ReadOnlyMemory<byte> packBytes, PackInstallContext context,
            CancellationToken cancellationToken = default) => inner.InstallAsync(packBytes, context, cancellationToken);

        public Task<PackInstallOutcome> InstallAsync(ReadOnlyMemory<byte> packBytes, PackInstallContext context,
            PackProjectionTransaction transaction, CancellationToken cancellationToken = default) =>
            inner.InstallAsync(packBytes, context, transaction, cancellationToken);

        public Task<PackActivationOutcome> ActivateAsync(PackInstallContext context, string packKey, string version,
            CancellationToken cancellationToken = default) => Refuse(packKey, version);

        public Task<PackActivationOutcome> ActivateAsync(PackInstallContext context, string packKey, string version,
            PackProjectionTransaction transaction, CancellationToken cancellationToken = default) => Refuse(packKey, version);

        public Task<PackDeactivationOutcome> DeactivateAsync(PackInstallContext context, string packKey, string version,
            CancellationToken cancellationToken = default) => inner.DeactivateAsync(context, packKey, version, cancellationToken);

        public Task<PackNarrowingOutcome> NarrowAsync(PackInstallContext context, string packKey, string contentKey,
            JsonNode overlayPatch, AuthorizationDecision decision, CancellationToken cancellationToken = default) =>
            inner.NarrowAsync(context, packKey, contentKey, overlayPatch, decision, cancellationToken);

        public Task<PackNarrowingOutcome> NarrowAsync(PackInstallContext context, string packKey, string contentKey,
            JsonNode overlayPatch, AuthorizationDecision decision, PackProjectionTransaction transaction,
            CancellationToken cancellationToken = default) =>
            inner.NarrowAsync(context, packKey, contentKey, overlayPatch, decision, transaction, cancellationToken);

        private Task<PackActivationOutcome> Refuse(string packKey, string version)
        {
            Interlocked.Increment(ref _activations);
            return Task.FromResult(new PackActivationOutcome(false, packKey, version, Error: null));
        }
    }
}
