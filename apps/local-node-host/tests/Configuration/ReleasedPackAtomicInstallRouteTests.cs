using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// The release-wide atomic install through <c>POST /configuration/releases/{digest}/install</c>, over the same
/// production composition as <see cref="ReleasedPackAtomicInstallTests"/>: the route's status, the overlays and the
/// absent releasing-pack rows are read back from a restart of the encrypted database.
/// </summary>
public sealed class ReleasedPackAtomicInstallRouteTests : IAsyncLifetime
{
    private ReleasedPackAtomicFixture _fixture = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _fixture = await ReleasedPackAtomicFixture.CreateAsync();
        var builder = WebApplication.CreateBuilder();
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Use(async (http, next) =>
        {
            if (http.Request.Headers.ContainsKey("X-Test-Desktop")) http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        ConfigurationProposalRoutes.Map(_app, _fixture.Proposals, _fixture.ActiveTeam, TestPackGate.AllowAll(),
            new FrozenClock(ReleasedPackAtomicFixture.Frozen), NullLogger.Instance, _fixture.Releases);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
        _client.DefaultRequestHeaders.Add("X-Test-Desktop", "1");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _fixture.DisposeAsync();
    }

    /// <summary>
    /// Regression foreign-only-success: two legitimate narrowings of acme.finance and nothing of the release's own.
    /// The route answers 200 "narrowed" naming both, each overlay is the literal RFC 7396 removal, and no releasing
    /// pack seed, version, watermark or projection admission exists.
    /// </summary>
    [Fact]
    public async Task Foreign_only_release_through_the_route_is_narrowed_with_both_literal_overlays()
    {
        var digest = await _fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [("forms/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", """{"id":"forms/invoice"}"""),
             ("records/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", """{"id":"records/invoice"}""")],
            (ReleasedPackAtomicFixture.Finance, "1.0.0")));
        var outbox = await _fixture.OutboxCountAsync();

        using var response = await _client.PostAsJsonAsync(InstallRoute(digest), new { });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body.ToString());
        Assert.Equal("narrowed", body.GetProperty("status").GetString());
        Assert.Equal(string.Empty, body.GetProperty("packKey").GetString());
        Assert.Empty(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal(new[] { "forms/invoice", "records/invoice" },
            body.GetProperty("narrowed").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal((0, 0, 2), (_fixture.Exporter.Exports, _fixture.Installer.Installs, _fixture.Installer.Narrowings));

        await using var restart = SearchTestStore.Reopen(_fixture.Harness.Store);
        var reopened = new DurablePackInstallStore(restart.PacksFactory);
        Assert.Equal(
            new[] { ("forms/invoice", """{"pack":null}"""), ("records/invoice", """{"pack":null}""") },
            reopened.GetOverrides(_fixture.Tenant, ReleasedPackAtomicFixture.Finance)
                .Select(row => (row.ContentKey, row.OverlayPatch.ToJsonString())).ToArray());
        Assert.DoesNotContain(reopened.ListInstalled(_fixture.Tenant), pack => pack.PackKey == ReleasedPackAtomicFixture.ReleaseKey);
        Assert.Null(reopened.GetWatermark(_fixture.Tenant, ReleasedPackAtomicFixture.ReleaseKey));
        Assert.Empty(((IPackProjectionAdmissionStore)reopened).ListIncompleteProjectionAdmissions());
        Assert.Empty(_fixture.Projector.Published);
        Assert.Equal(outbox + 2, await _fixture.OutboxCountAsync());
        Assert.Equal(2, _fixture.Audit.Successes().Count(entry => entry.Action == PackInstallAuditAction.Narrowed
            && entry.PackKey == ReleasedPackAtomicFixture.Finance));
    }

    /// <summary>
    /// Regression foreign-only-success, unchanged half: edits equal to the owner's Active definitions take nothing
    /// over and narrow nothing, so the release is a 200 "narrowed" no-op that runs no kernel write at all.
    /// </summary>
    [Fact]
    public async Task Unchanged_foreign_only_release_through_the_route_is_a_no_op()
    {
        var digest = await _fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [("forms/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", """{"id":"forms/invoice","pack":"acme.finance"}"""),
             ("records/invoice", ReleasedPackAtomicFixture.Finance, "FormDefinition", """{"id":"records/invoice","pack":"acme.finance"}""")],
            (ReleasedPackAtomicFixture.Finance, "1.0.0")));
        var live = _fixture.LiveState();
        var durable = await _fixture.DurableStateAsync();

        using var response = await _client.PostAsJsonAsync(InstallRoute(digest), new { });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body.ToString());
        Assert.Equal("narrowed", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("narrowed").EnumerateArray());
        Assert.Equal((0, 0, 0, 0), (_fixture.Exporter.Exports, _fixture.Installer.Installs,
            _fixture.Installer.Activations, _fixture.Installer.Narrowings));
        Assert.Equal(live, _fixture.LiveState());
        Assert.Equal(durable, await _fixture.DurableStateAsync());
        Assert.Empty(_fixture.Audit.Successes());
    }

    /// <summary>
    /// Regression mixed-refusal through the route: the own upgrade and the first overlay stage, the later overlay is
    /// refused by the composed cascade admission, the route answers 422, and a restart reads the pre-call rows.
    /// </summary>
    [Fact]
    public async Task Mixed_release_refused_through_the_route_writes_nothing()
    {
        _fixture.SeedOwnPrior();
        var digest = await _fixture.OfferAsync(ReleasedPackAtomicFixture.Document("1.1.0",
            [ReleasedPackAtomicFixture.OwnNote, ReleasedPackAtomicFixture.WithoutContactTitle, ReleasedPackAtomicFixture.WithoutPackageCeiling],
            (ReleasedPackAtomicFixture.Defaults, "1.0.0")));
        var live = _fixture.LiveState();
        var durable = await _fixture.DurableStateAsync();
        var outbox = await _fixture.OutboxCountAsync();

        using var response = await _client.PostAsJsonAsync(InstallRoute(digest), new { });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, body.ToString());
        Assert.Equal("refused", body.GetProperty("status").GetString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("configuration-release-foreign-definition", refusal.GetProperty("code").GetString());
        Assert.Equal(ReleasedPackAtomicFixture.PackageDefaults, refusal.GetProperty("target").GetString());
        Assert.EndsWith("its narrowing overlay was refused (pack.defaults.relax_forbidden).",
            refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(body.GetProperty("narrowed").EnumerateArray());
        Assert.Equal((1, 1, 2), (_fixture.Installer.Installs, _fixture.Installer.Activations, _fixture.Installer.Narrowings));
        Assert.Equal(live, _fixture.LiveState());
        Assert.Equal(durable, await _fixture.DurableStateAsync());
        Assert.Equal(outbox, await _fixture.OutboxCountAsync());
        Assert.Empty(_fixture.Audit.Successes());
    }

    private static string InstallRoute(string digest) => $"/api/local-node/configuration/releases/{digest}/install";

    private sealed class FrozenClock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }
}
