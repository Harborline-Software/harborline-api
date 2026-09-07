using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Governance;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// Route-level tests for the node-local instance-lifecycle surface (<see cref="LifecycleRoutes"/>, slice
/// B5) — hosts the SAME route handler <see cref="HostedLifecycleApiEndpoint"/> registers, on a real
/// in-process Kestrel listener, driven with a real <see cref="HttpClient"/> to pin the wire contract FED's
/// <c>useLifecyclePhase.ts</c> consumes.
/// </summary>
public sealed class LifecycleRouteTests : IAsyncLifetime
{
    private static readonly TeamId TestTeamId = new(new Guid("55555555-5555-5555-5555-555555555555"));

    private string _dir = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "b5-lifecycle-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Fresh_node_reads_setup_phase_and_a_granted_unlock()
    {
        await using var h = await Harness.StartAsync(_dir, activeTeam: TestTeamId, unlockGranted: true);

        using var doc = await h.GetLifecycleAsync();
        var root = doc.RootElement;

        Assert.Equal("setup", root.GetProperty("phase").GetString());
        Assert.True(root.GetProperty("unlock").GetProperty("granted").GetBoolean());
    }

    [Fact]
    public async Task Finish_setup_flips_to_operating_and_the_next_read_reflects_it()
    {
        await using var h = await Harness.StartAsync(_dir, activeTeam: TestTeamId, unlockGranted: true);

        var post = await h.Client.PostAsync(LifecycleRoutes.FinishSetupRoute, content: null);
        Assert.True(post.IsSuccessStatusCode, $"expected 2xx, got {(int)post.StatusCode}");
        using (var postDoc = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            Assert.Equal("operating", postDoc.RootElement.GetProperty("phase").GetString());
        }

        using var getDoc = await h.GetLifecycleAsync();
        Assert.Equal("operating", getDoc.RootElement.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task A_denied_unlock_carries_the_accessible_reason_and_remediation_keys()
    {
        await using var h = await Harness.StartAsync(_dir, activeTeam: TestTeamId, unlockGranted: false);

        using var doc = await h.GetLifecycleAsync();
        var unlock = doc.RootElement.GetProperty("unlock");

        Assert.False(unlock.GetProperty("granted").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(unlock.GetProperty("reasonCode").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(unlock.GetProperty("reasonKey").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(unlock.GetProperty("remediationKey").GetString()));
    }

    [Fact]
    public async Task No_active_team_reads_a_calm_setup_default_without_persisting()
    {
        await using var h = await Harness.StartAsync(_dir, activeTeam: null, unlockGranted: true);

        using var doc = await h.GetLifecycleAsync();
        var root = doc.RootElement;

        // Pre-genesis boot window: calm setup default, unlock not yet resolvable.
        Assert.Equal("setup", root.GetProperty("phase").GetString());
        Assert.False(root.GetProperty("unlock").GetProperty("granted").GetBoolean());
        Assert.Equal("no_active_tenant", root.GetProperty("unlock").GetProperty("reasonCode").GetString());

        // Nothing was persisted (no tenant to key on).
        Assert.False(File.Exists(Path.Combine(_dir, FileTenantGovernanceStateStore.FileName)));
    }

    [Fact]
    public async Task Finish_setup_with_no_active_team_fails_loud()
    {
        await using var h = await Harness.StartAsync(_dir, activeTeam: null, unlockGranted: true);

        var post = await h.Client.PostAsync(LifecycleRoutes.FinishSetupRoute, content: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
    }

    // ── Test harness ─────────────────────────────────────────────────────────────────────────────────

    private sealed class Harness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public HttpClient Client { get; }

        private Harness(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public static async Task<Harness> StartAsync(string dir, TeamId? activeTeam, bool unlockGranted)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();

            var store = FileTenantGovernanceStateStore.InDirectory(dir, TimeProvider.System.GetUtcNow);
            var authority = new StubUnlockAuthority(unlockGranted);
            var accessor = new FakeActiveTeamAccessor(
                activeTeam is { } id ? new TeamContext(id, "Test Team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System) : null);

            app.Use(async (http, next) =>
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
                await next(http);
            });
            LifecycleRoutes.Map(
                app.MapSelectedSessionProductGroup(),
                store,
                authority,
                accessor,
                TimeProvider.System,
                new NodeCallerSessionToken(null));

            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
            return new Harness(app, client);
        }

        public async Task<JsonDocument> GetLifecycleAsync()
        {
            var res = await Client.GetAsync(LifecycleRoutes.RouteBase);
            Assert.True(res.IsSuccessStatusCode, $"expected 2xx, got {(int)res.StatusCode}");
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class StubUnlockAuthority : INodeWorkshopUnlockAuthority
    {
        private readonly bool _granted;
        public StubUnlockAuthority(bool granted) => _granted = granted;
        public ValueTask<WorkshopUnlockDecision> AuthorizeAsync(AuthorizationWriteContext authority, CancellationToken ct = default) =>
            new(_granted ? WorkshopUnlockDecision.Granted.Instance : WorkshopUnlockDecision.MissingUnlockPermission);
    }

    private sealed class FakeActiveTeamAccessor : IActiveTeamAccessor
    {
        public FakeActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
