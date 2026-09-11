using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Ticket 380 slice 1 — the pack install command makes TWO gate decisions (the route's, on the preview's
/// key; the installer's, on the coordinates CLAIMED in the artifact), and when they disagree the installer's
/// <see cref="AuthorizationDeniedException"/> used to escape to Kestrel as an empty HTTP 500.
/// </summary>
/// <remarks>
/// <para>
/// The disagreement is reachable with a signed, verifiable pack: the pre-gate coordinate reader scans a
/// bounded 64 KiB manifest prefix (<c>PackManifestCoordinateReader</c>), so a pack whose manifest is LARGER
/// than that prefix claims <c>(unverified)</c> coordinates while the route's own no-effect preview reports
/// the verified <c>manifest.Key</c>. The route then decides about the real pack (allowed) and the installer
/// about a record the caller holds nothing on (denied).
/// </para>
/// <para>
/// The gate here is the production <see cref="AuthorizationGate"/> over a grant at a NAMED record scope —
/// no install-wide grant, no <c>TestPackGate</c> — and the ROUTES are the production map, with the ONE gate
/// shared by route and installer exactly as <c>Program.cs</c> composes them. A record-scoped holding is
/// what makes the mismatch observable at all: the genesis operator seed grants <c>packages:operate</c> at
/// the install root (<c>/</c>), which covers every record id including <c>(unverified)</c>, so a first-boot
/// node cannot produce this denial.
/// </para>
/// </remarks>
public sealed class PackInstallCoordinateMismatchTests : IAsyncLifetime
{
    private static readonly TeamId Team = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000003b0"));

    /// <summary>The pack whose manifest exceeds the pre-gate coordinate prefix.</summary>
    private const string OversizeManifestPackKey = "ticket380.oversize-manifest";

    /// <summary>The control pack — same grant, same route, a manifest inside the prefix.</summary>
    private const string SmallManifestPackKey = "ticket380.small-manifest";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private KeyPair _key = null!;
    private TenantId _tenant;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTestKernelClock();

        // ONE real gate, granting every pack operation ONLY inside the two packs' record scopes. The
        // refusal below is the gate's own scope containment refusing /records/(unverified).
        var gate = TestRouteGate.ScopedTo(OversizeManifestPackKey, SmallManifestPackKey);
        builder.Services.AddSingleton(gate);

        // The shipped refusal-audit composition — the same registrations Program.cs makes, so the rendered
        // refusal carries its audit receipt.
        builder.Services.AddSingleton(new NodePrincipalSigner(RandomNumberGenerator.GetBytes(32)));
        builder.Services.AddSingleton<IOperationSigner>(
            sp => sp.GetRequiredService<NodePrincipalSigner>().Signer);
        builder.Services.AddEnrollmentCompensatingControlAudit();
        builder.Services.AddSingleton<IAuditTrail>(sp => sp.GetRequiredService<InMemoryAuditTrail>());
        builder.Services.AddAuthorizationRefusalAudit();

        _app = builder.Build();

        _key = KeyPair.Generate();
        var signer = new Ed25519Signer(_key);
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
        var verifier = new PackVerifier(new Ed25519Verifier(), codec);
        var trustStore = new InMemoryPackTrustStore(
        [
            new PackTrustRoot(
                TrustScope.OwnRoster, _key.PrincipalId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current),
        ]);
        var activeTeam = new StubActiveTeamAccessor(
            new TeamContext(Team, "Team 380", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
        _tenant = NodeTenant.Resolve(activeTeam);
        var store = new InMemoryPackInstallStore();
        var installer = new PackInstaller(
            verifier,
            store,
            new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator()),
            new InMemoryPackInstallAudit(),
            // The PRODUCTION composition: the installer re-decides on the SAME gate the route asked.
            gate,
            new Harborline.Api.Foundation.Packs.Install.Compatibility.PackPlatformCompatibility(
                "2.0.0", PackSeedProjector.RegisteredCases));
        var registryServices = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var projector = new PackSeedProjector(
            store, registryServices.GetRequiredService<IEntityTypeRegistry>(),
            NullLogger<PackSeedProjector>.Instance, time: TimeProvider.System);

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        // The production registration, in the production order: the translation wraps the route handlers.
        AuthorizationDenialTranslation.Use(_app);
        PackComposerRoutes.Map(
            _app, exporter, verifier, trustStore, signer, activeTeam, gate, TimeProvider.System, NullLogger.Instance);
        PackInstallRoutes.Map(
            _app, installer, store, trustStore, PackRevocationList.Empty, activeTeam, gate, TimeProvider.System,
            NullLogger.Instance, authorizingPrincipal: _key.PrincipalId.ToBase64Url(), projector: projector);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _key?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact(DisplayName = "holds 380.A1: a route/installer coordinate mismatch answers the typed 403, not a 500")]
    public async Task An_installer_decision_that_disagrees_with_the_route_answers_the_rendered_refusal()
    {
        // The control: the SAME grant, the SAME route, a manifest the pre-gate reader can read. Both
        // decisions name the pack, so the install proceeds — this is what makes the refusal below a
        // DISAGREEMENT and not simply a caller who holds nothing.
        Assert.Equal(HttpStatusCode.OK, (await InstallAsync(SmallManifestPackKey, contentItems: 1)).StatusCode);

        var response = await InstallAsync(OversizeManifestPackKey, contentItems: 600);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var refusal = JsonDocument.Parse(body);
        Assert.Equal(
            AuthorizationRefusalRenderer.PermissionRequiredCode,
            refusal.RootElement.GetProperty("code").GetString());
        Assert.Equal(Permission.PackagesOperate, refusal.RootElement.GetProperty("permission").GetString());
        // RW-4: the refusal carries its audit receipt, and the exception's own sentence never reaches the wire.
        Assert.NotEqual(Guid.Empty, refusal.RootElement.GetProperty("auditId").GetGuid());
        Assert.DoesNotContain("The authorization gate denied", body, StringComparison.Ordinal);
        Assert.DoesNotContain("(unverified)", body, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "holds 380.A1: the refused mismatch records the INSTALLER's decision, not a pre-decision")]
    public async Task The_refusal_audit_carries_the_installers_own_decision()
    {
        var response = await InstallAsync(OversizeManifestPackKey, contentItems: 600);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var refusal = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var rows = new List<AuditRecord>();
        await foreach (var record in _app.Services.GetRequiredService<IAuditTrail>()
                           .QueryAsync(new AuditQuery(_tenant)))
        {
            if (record.EventType.Equals(AuthorizationRefusalAudit.AuthorizationRefusedEventType))
                rows.Add(record);
        }

        var row = Assert.Single(rows);
        Assert.Equal(row.AuditId, refusal.RootElement.GetProperty("auditId").GetGuid());
        // A DECIDED refusal: the installer's own decision, with the gate's four-stage trace, not a
        // pre-decision placeholder invented by the translation.
        Assert.Equal(false, row.Payload.Payload.Body["preDecision"]);
        Assert.Equal("pack", row.Target!.Value.RecordKind);
        Assert.NotNull(row.AuthoritySnapshot);
        Assert.Equal(4, row.AuthoritySnapshot.Trace!.Count);
        // The record the INSTALLER decided about is the claimed one — the disagreement itself, on the row
        // where the classified reading belongs and nowhere on the wire.
        Assert.Equal("(unverified)", row.Target!.Value.RecordId);
    }

    private async Task<HttpResponseMessage> InstallAsync(string packKey, int contentItems)
    {
        using var export = await _client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute, ExportBody(packKey, contentItems));
        var packBytes = await export.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);

        using var content = new ByteArrayContent(packBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await _client.PostAsync(PackInstallRoutes.InstallRoute, content);
    }

    /// <summary>
    /// An export request whose manifest grows with <paramref name="contentItems"/>: each content ref carries
    /// a padded key plus its content address, so 600 of them put the manifest's closing brace beyond the
    /// pre-gate reader's 64 KiB prefix while one of them stays well inside it.
    /// </summary>
    private static object ExportBody(string packKey, int contentItems) => new
    {
        key = packKey,
        version = "1.0.0",
        name = "Ticket 380 manifest size",
        description = "route/installer coordinate disagreement",
        scopeTier = "Vertical",
        contents = Enumerable.Range(0, contentItems).Select(index => new
        {
            key = $"intake-{index:D4}-{new string('k', 40)}",
            kind = "FormDefinition",
            version = "1.0.0",
            content = new { title = $"Intake {index}", assignee = "role:approver" },
        }).ToArray(),
        dependencies = Array.Empty<object>(),
        capabilityRequirements = new[] { "forms.dynamic" },
    };

    private sealed class StubActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active => active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
