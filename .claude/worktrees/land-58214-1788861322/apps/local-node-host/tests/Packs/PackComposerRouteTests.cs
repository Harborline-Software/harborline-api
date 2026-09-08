using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Route-level end-to-end for the Pack Composer B-1a export/verify surface: an own-roster pack
/// composed + signed via <c>POST /packs/export</c> round-trips through <c>POST /packs/verify</c> to
/// <c>Verified</c>; an instance-data pack is refused (422) at export; garbage is
/// <c>VerificationFailed</c> at verify. Mirrors <see cref="PackComposerRoutes.Map"/> exactly — no
/// test/prod wire drift.
/// </summary>
public sealed class PackComposerRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000000fc"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private KeyPair _key = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        _key = KeyPair.Generate();
        var signer = new Ed25519Signer(_key);
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
        var verifier = new PackVerifier(new Ed25519Verifier(), codec);
        var trustStore = new InMemoryPackTrustStore(new[]
        {
            new PackTrustRoot(TrustScope.OwnRoster, _key.PrincipalId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current),
        });
        var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));

        PackComposerRoutes.Map(_app, exporter, verifier, trustStore, signer, activeTeam,
            TestPackGate.AllowAll(), TimeProvider.System, NullLogger.Instance);

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

    private static object ValidExportBody() => new
    {
        key = "acme.inspections",
        version = "1.0.0",
        name = "Acme Inspections",
        description = "Inspection intake pack.",
        scopeTier = "Vertical",
        contents = new[]
        {
            new
            {
                key = "intake-form",
                kind = "FormDefinition",
                version = "1.0.0",
                content = new { title = "Intake", assignee = "role:approver" },
            },
        },
        dependencies = Array.Empty<object>(),
        capabilityRequirements = new[] { "forms.dynamic" },
    };

    [Fact(DisplayName = "export → verify round-trips to Verified")]
    public async Task Export_then_verify_is_verified()
    {
        var exportResp = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, ValidExportBody());
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
        var packBytes = await exportResp.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(packBytes);

        using var content = new ByteArrayContent(packBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var verifyResp = await _client.PostAsync(PackComposerRoutes.VerifyRoute, content);
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);

        using var doc = JsonDocument.Parse(await verifyResp.Content.ReadAsStringAsync());
        Assert.Equal("Verified", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("acme.inspections", doc.RootElement.GetProperty("manifestKey").GetString());
        Assert.Equal("OwnRoster", doc.RootElement.GetProperty("vouchingScope").GetString());
    }

    [Fact(DisplayName = "export rejects an unparseable DCP regulatoryClass (400, never a silent downgrade)")]
    public async Task Export_unparseable_regulatory_class_is_rejected()
    {
        // Fail-closed honesty (ADR 0145): a typo'd regulated class must NOT silently coerce to General and
        // slip past the export gate's class-clearance check — a supplied-but-unparseable token is a 400.
        var body = new
        {
            key = "acme.inspections",
            version = "1.0.0",
            name = "Acme Inspections",
            description = "Inspection intake pack.",
            scopeTier = "Vertical",
            contents = new[]
            {
                new
                {
                    key = "intake-form",
                    kind = "FormDefinition",
                    version = "1.0.0",
                    content = new { title = "Intake", assignee = "role:approver" },
                },
            },
            dependencies = Array.Empty<object>(),
            capabilityRequirements = new[] { "forms.dynamic" },
            dcp = new { regulatoryClass = "healthcare-typo" },
        };
        var resp = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "export refuses an instance-data pack (422)")]
    public async Task Export_instance_data_is_refused()
    {
        var body = new
        {
            key = "leaky.pack",
            version = "1.0.0",
            name = "Leaky",
            description = "d",
            scopeTier = "Vertical",
            contents = new[]
            {
                new
                {
                    key = "leaky-form",
                    kind = "FormDefinition",
                    version = "1.0.0",
                    content = new { title = "x", submittedValues = new { amount = "500" } },
                },
            },
            dependencies = Array.Empty<object>(),
            capabilityRequirements = Array.Empty<string>(),
        };

        var resp = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("pii.instance_data", text);
    }

    [Fact(DisplayName = "verify of garbage is VerificationFailed")]
    public async Task Verify_garbage_is_failed()
    {
        using var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("not a pack"));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var resp = await _client.PostAsync(PackComposerRoutes.VerifyRoute, content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("VerificationFailed", doc.RootElement.GetProperty("verdict").GetString());
    }

    private static TeamContext TeamContextFor(TeamId teamId, string name)
        => new(teamId, name, new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        public MutableActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; set; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
