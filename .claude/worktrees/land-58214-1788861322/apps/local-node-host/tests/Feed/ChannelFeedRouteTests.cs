using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Foundation.UpdateFeed;
using Harborline.Api.Foundation.UpdateFeed.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Feed;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Feed;

/// <summary>
/// Update-feed U2 — the ROUTE-level LIVE end-to-end against U1's ACTUAL published dogfood feed (the golden
/// fixtures copied byte-for-byte from harborline-www). <c>POST /channels/harborline-dogfood/check</c> drives
/// the whole path: the node fetches the feed over real HTTP → the shared verifier verifies it against the
/// binary-pinned dogfood root → the verified artifact is staged and handed to the EXISTING install-preview
/// engine (no new install path) → the response shows the update as installable against the (empty) installed
/// state. Also proves the sideload path through the SAME route, tamper fail-closed (422), the channel-table
/// list surface (F6 config + pinned?), and the <c>packages:operate</c> gate.
/// </summary>
public sealed class ChannelFeedRouteTests
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000000fe"));
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero); // before validUntil (2026-08-06)

    /// <summary>The tenant the route harness resolves TeamA's active team to (mirrors
    /// <c>NodeTenant.Resolve</c> — <c>ActiveTeamTenantContext.ProjectTenantId</c>), so a test can seed the
    /// SAME tenant's installed state the routes will read.</summary>
    private static readonly TenantId TeamTenant = Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(TeamA);

    // ── the headline gate: live end-to-end against U1's published dogfood feed over HTTP ──────────────
    [Fact(DisplayName = "U2 KEYSTONE: check the dogfood channel over HTTP → verify → stage → the EXISTING preview shows the pack installable against empty installed state")]
    public async Task Check_dogfood_feed_over_http_previews_the_update()
    {
        await using var feedServer = await FeedTestServer.StartAsync(GoldenDogfoodFeed.LoadFiles());
        await using var harness = await RouteHarness.CreateAsync(
            OnlineDogfood(feedServer.FeedBaseUrl), feedServer.HttpClientFactory);

        var resp = await harness.Client.PostAsync(
            $"/api/local-node/channels/{GoldenDogfoodFeed.ChannelId}/check", EmptyBody());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("Verified", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(1L, root.GetProperty("channel").GetProperty("sequence").GetInt64());

        var pack = Assert.Single(root.GetProperty("packs").EnumerateArray().ToList());
        Assert.Equal(GoldenDogfoodFeed.PackKey, pack.GetProperty("packKey").GetString());
        Assert.Equal(GoldenDogfoodFeed.Version, pack.GetProperty("latest").GetString());

        // The staged artifact went through the EXISTING install-preview engine (no new install path):
        var preview = pack.GetProperty("preview");
        Assert.Equal("WouldInstall", preview.GetProperty("verdict").GetString());        // installable
        Assert.False(preview.GetProperty("isUpgrade").GetBoolean());                     // nothing installed yet
        Assert.Equal(GoldenDogfoodFeed.PackKey, preview.GetProperty("packKey").GetString());
    }

    // ── the SAME route, sideload shape: a local feed tree verifies through the same path ──────────────
    [Fact(DisplayName = "U2: the same check route verifies U1's dogfood feed as a SIDELOAD (local tree) — same verify + preview path")]
    public async Task Check_dogfood_feed_as_sideload_previews_the_update()
    {
        var registry = new BinaryPinnedChannelRegistry(
            new[]
            {
                new NodeChannel(GoldenDogfoodFeed.ChannelId, "Dogfood (USB)", GoldenDogfoodFeed.Directory,
                    ChannelKind.Sideload, FeedKeyId.Format(GoldenDogfoodFeed.PinnedRoot().KeyId), true),
            },
            new[] { new ChannelRootPin(GoldenDogfoodFeed.ChannelId, GoldenDogfoodFeed.PinnedRoot(), 1, "dogfood-dev") });

        await using var harness = await RouteHarness.CreateAsync(registry, NewFactory());
        var resp = await harness.Client.PostAsync(
            $"/api/local-node/channels/{GoldenDogfoodFeed.ChannelId}/check", EmptyBody());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Verified", doc.RootElement.GetProperty("status").GetString());
        var pack = Assert.Single(doc.RootElement.GetProperty("packs").EnumerateArray().ToList());
        Assert.Equal("WouldInstall", pack.GetProperty("preview").GetProperty("verdict").GetString());
    }

    // ── tamper at the route: a mutated published artifact is refused with 422, nothing previewed ──────
    [Fact(DisplayName = "U2: a tampered dogfood artifact served over HTTP is refused (422) — no pack staged, no preview")]
    public async Task Check_tampered_dogfood_feed_is_refused()
    {
        var files = GoldenDogfoodFeed.LoadFiles();
        var artifactKey = files.Keys.Single(k => k.Contains("/artifact.", StringComparison.Ordinal));
        files[artifactKey][^1] ^= 0xFF; // corrupt one byte of the published blob

        await using var feedServer = await FeedTestServer.StartAsync(files);
        await using var harness = await RouteHarness.CreateAsync(
            OnlineDogfood(feedServer.FeedBaseUrl), feedServer.HttpClientFactory);

        var resp = await harness.Client.PostAsync(
            $"/api/local-node/channels/{GoldenDogfoodFeed.ChannelId}/check", EmptyBody());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("VerificationFailed", doc.RootElement.GetProperty("status").GetString());
        Assert.Empty(doc.RootElement.GetProperty("packs").EnumerateArray().ToList());
        Assert.Contains(
            doc.RootElement.GetProperty("failures").EnumerateArray(),
            f => f.GetProperty("code").GetString() == FeedVerifyCodes.ArtifactCidMismatch);
    }

    // ── U3: the §5.3 "update available" state, wired end to end through the SAME check route ──────────

    [Fact(DisplayName = "U3: nothing installed yet → the check surfaces updateState=NotInstalled (feed-only, offered as a fresh install)")]
    public async Task Check_surfaces_not_installed_state()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 1);
        await using var feedServer = await FeedTestServer.StartAsync(feed.Files);
        await using var harness = await RouteHarness.CreateAsync(
            OnlineFeedTestChannel(feedServer.FeedBaseUrl, feed.Root), feedServer.HttpClientFactory,
            previewRoot: feed.Root);

        var resp = await harness.Client.PostAsync(
            $"/api/local-node/channels/{FeedTestBuilder.ChannelId}/check", EmptyBody());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var pack = Assert.Single(doc.RootElement.GetProperty("packs").EnumerateArray().ToList());
        Assert.Equal("NotInstalled", pack.GetProperty("updateState").GetString());
        Assert.False(pack.TryGetProperty("installedVersion", out var iv) && iv.ValueKind != JsonValueKind.Null);
    }

    [Fact(DisplayName = "U3: the SAME version already installed → re-checking surfaces updateState=UpToDate (no affordance)")]
    public async Task Check_surfaces_up_to_date_after_installing()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 1);
        await using var feedServer = await FeedTestServer.StartAsync(feed.Files);
        var registry = OnlineFeedTestChannel(feedServer.FeedBaseUrl, feed.Root);
        await using var harness = await RouteHarness.CreateAsync(
            registry, feedServer.HttpClientFactory, previewRoot: feed.Root);

        // Seed the "already installed" baseline: install + activate the SAME artifact the feed carries.
        var artifactBytes = feed.Files[FeedTestBuilder.ArtifactPath(feed.Files)];
        var trustStore = new InMemoryPackTrustStore(new[]
        {
            new PackTrustRoot(TrustScope.HarborlineChannel, feed.Root.KeyId, feed.Root.PublisherEpoch, TrustRootStatus.Current),
        });
        var ctx = new PackInstallContext(TeamTenant, trustStore, PackRevocationList.Empty, Now, TimeSpan.FromDays(30), Principal: "test-operator");
        var installed = harness.Installer.Install(artifactBytes, ctx);
        Assert.True(installed.Installed);
        var activated = harness.Installer.Activate(TeamTenant, FeedTestBuilder.PackKey, "0.1.0", Now, "test-operator");
        Assert.True(activated.Activated);

        var resp = await harness.Client.PostAsync(
            $"/api/local-node/channels/{FeedTestBuilder.ChannelId}/check", EmptyBody());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var pack = Assert.Single(doc.RootElement.GetProperty("packs").EnumerateArray().ToList());
        Assert.Equal("UpToDate", pack.GetProperty("updateState").GetString());
        Assert.Equal("0.1.0", pack.GetProperty("installedVersion").GetString());
    }

    // ── U3 epoch gate (UF-4) — BOTH directions, wired through the SAME check route ─────────────────────

    [Fact(DisplayName = "U3 epoch gate (at-or-below): minNodeEpoch == NodeKernelEpoch.Current does NOT block")]
    public async Task Check_epoch_at_current_does_not_block()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 1, minNodeEpoch: NodeKernelEpoch.Current);
        await using var feedServer = await FeedTestServer.StartAsync(feed.Files);
        await using var harness = await RouteHarness.CreateAsync(
            OnlineFeedTestChannel(feedServer.FeedBaseUrl, feed.Root), feedServer.HttpClientFactory,
            previewRoot: feed.Root);

        var resp = await harness.Client.PostAsync(
            $"/api/local-node/channels/{FeedTestBuilder.ChannelId}/check", EmptyBody());

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var pack = Assert.Single(doc.RootElement.GetProperty("packs").EnumerateArray().ToList());
        Assert.NotEqual("NeedsNewerNode", pack.GetProperty("updateState").GetString());
    }

    [Fact(DisplayName = "U3 epoch gate (above): minNodeEpoch > NodeKernelEpoch.Current → updateState=NeedsNewerNode, overrides an otherwise-fresh install")]
    public async Task Check_epoch_above_current_blocks()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 1, minNodeEpoch: NodeKernelEpoch.Current + 1);
        await using var feedServer = await FeedTestServer.StartAsync(feed.Files);
        await using var harness = await RouteHarness.CreateAsync(
            OnlineFeedTestChannel(feedServer.FeedBaseUrl, feed.Root), feedServer.HttpClientFactory,
            previewRoot: feed.Root);

        var resp = await harness.Client.PostAsync(
            $"/api/local-node/channels/{FeedTestBuilder.ChannelId}/check", EmptyBody());

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var pack = Assert.Single(doc.RootElement.GetProperty("packs").EnumerateArray().ToList());
        Assert.Equal("NeedsNewerNode", pack.GetProperty("updateState").GetString());
    }

    // ── the channel table list surface (F6 config + pinned?) ──────────────────────────────────────────
    [Fact(DisplayName = "U2: GET /channels lists the channel table — config row + IsPinned true + dogfood grade (F6 config-vs-pin legibility)")]
    public async Task List_channels_shows_config_and_pin()
    {
        await using var feedServer = await FeedTestServer.StartAsync(GoldenDogfoodFeed.LoadFiles());
        await using var harness = await RouteHarness.CreateAsync(
            OnlineDogfood(feedServer.FeedBaseUrl), feedServer.HttpClientFactory);

        var resp = await harness.Client.GetAsync("/api/local-node/channels");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var row = Assert.Single(doc.RootElement.EnumerateArray().ToList());
        Assert.Equal(GoldenDogfoodFeed.ChannelId, row.GetProperty("channelId").GetString());
        Assert.True(row.GetProperty("isPinned").GetBoolean());
        Assert.Equal("dogfood-dev", row.GetProperty("grade").GetString());
    }

    // ── the packages:operate gate ─────────────────────────────────────────────────────────────────────
    [Fact(DisplayName = "U2: the check route is gated on packages:operate — a caller without it is denied")]
    public async Task Check_requires_packages_operate()
    {
        await using var feedServer = await FeedTestServer.StartAsync(GoldenDogfoodFeed.LoadFiles());
        await using var harness = await RouteHarness.CreateAsync(
            OnlineDogfood(feedServer.FeedBaseUrl), feedServer.HttpClientFactory, Harborline.Api.LocalNodeHost.Tests.Packs.TestPackGate.Denying());

        var resp = await harness.Client.PostAsync(
            $"/api/local-node/channels/{GoldenDogfoodFeed.ChannelId}/check", EmptyBody());
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────

    private static System.Net.Http.HttpContent EmptyBody()
        => new System.Net.Http.ByteArrayContent(Array.Empty<byte>());

    private static System.Net.Http.IHttpClientFactory NewFactory()
        => new ServiceCollection().AddHttpClient().BuildServiceProvider()
            .GetRequiredService<System.Net.Http.IHttpClientFactory>();

    private static BinaryPinnedChannelRegistry OnlineDogfood(string feedBase)
        => new(
            new[]
            {
                new NodeChannel(GoldenDogfoodFeed.ChannelId, "Dogfood (online)", feedBase, ChannelKind.Online,
                    FeedKeyId.Format(GoldenDogfoodFeed.PinnedRoot().KeyId), true),
            },
            new[] { new ChannelRootPin(GoldenDogfoodFeed.ChannelId, GoldenDogfoodFeed.PinnedRoot(), 1, "dogfood-dev") });

    /// <summary>A pinned online channel over a freshly-<see cref="FeedTestBuilder.BuildAsync"/>-generated
    /// feed (U3 state-derivation tests — the golden dogfood fixture's version/epoch are fixed, so these
    /// tests generate their own one-off feeds to vary <c>minNodeEpoch</c> and to install-then-recheck).</summary>
    private static BinaryPinnedChannelRegistry OnlineFeedTestChannel(string feedBase, FeedPinnedRoot root)
        => new(
            new[]
            {
                new NodeChannel(FeedTestBuilder.ChannelId, "Node test (online)", feedBase, ChannelKind.Online,
                    FeedKeyId.Format(root.KeyId), true),
            },
            new[] { new ChannelRootPin(FeedTestBuilder.ChannelId, root, 1, "dogfood-dev") });

    /// <summary>A started in-process node host that maps the channel-feed routes over the REAL install-preview
    /// engine + the pinned dogfood preview trust store.</summary>
    private sealed class RouteHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly PacksTestStore _db;
        public System.Net.Http.HttpClient Client { get; private init; } = null!;

        private RouteHarness(WebApplication app, PacksTestStore db) { _app = app; _db = db; }

        /// <summary>The SAME store the mapped routes read for the §5.3 "installed" side of the derivation —
        /// tests pre-seed it (via <see cref="Installer"/>) to exercise UpToDate / UpdateAvailable.</summary>
        public IPackInstallStore Store { get; private init; } = null!;

        /// <summary>The SAME install engine the routes preview through — tests call
        /// <c>Installer.Install(...)</c> to seed an "already installed" baseline before re-checking.</summary>
        public IPackInstaller Installer { get; private init; } = null!;

        public static async Task<RouteHarness> CreateAsync(
            IChannelRegistry registry,
            System.Net.Http.IHttpClientFactory factory,
            Harborline.Api.Foundation.Authorization.AuthorizationGate? authz = null,
            FeedPinnedRoot? previewRoot = null,
            IPackInstallStore? store = null)
        {
            var db = await PacksTestStore.CreateAsync();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();

            var root = previewRoot ?? GoldenDogfoodFeed.PinnedRoot();
            var codec = new PackFileCodec();
            var sharedStore = store ?? new InMemoryPackInstallStore();
            var installer = new PackInstaller(
                new PackVerifier(new Harborline.Api.Foundation.Crypto.Ed25519Verifier(), codec),
                sharedStore,
                new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator()),
                new InMemoryPackInstallAudit(),
                Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
            var previewTrust = new InMemoryPackTrustStore(new[]
            {
                new PackTrustRoot(TrustScope.HarborlineChannel, root.KeyId, root.PublisherEpoch, TrustRootStatus.Current),
            });

            var client = new ChannelFeedClient(
                registry, new HttpFeedFetcher(factory),
                new DurableChannelSequenceStore(db.Factory), new FixedTimeProvider(Now));

            var activeTeam = new MutableActiveTeamAccessor(
                new TeamContext(TeamA, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));

            app.Use(async (http, next) =>
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
                await next(http);
            });
            ChannelFeedRoutes.Map(
                app.MapSelectedSessionProductGroup(),
                client, registry, installer, sharedStore, previewTrust, PackRevocationList.Empty,
                activeTeam, authz ?? Harborline.Api.LocalNodeHost.Tests.Packs.TestPackGate.AllowAll(), new FixedTimeProvider(Now),
                NullLogger.Instance);

            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new RouteHarness(app, db)
            {
                Client = new System.Net.Http.HttpClient { BaseAddress = new Uri(address) },
                Store = sharedStore,
                Installer = installer,
            };
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            await _db.DisposeAsync();
        }
    }

    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        public MutableActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; set; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
