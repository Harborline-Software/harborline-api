using System;
using System.IO;
using System.Linq;
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

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Governance;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// End-to-end for the founder's <c>workshop:unlock</c> holding on the DESKTOP plane (ticket 205 slice 2,
/// review-1 MAJOR): the REAL <see cref="AuthorizationGate"/> and the REAL
/// <see cref="NodeWorkshopUnlockAuthority"/>, wired through <see cref="LifecycleRoutes"/> exactly as
/// <c>HostedLifecycleApiEndpoint</c> maps them, over the install seeded by the production
/// <c>AccessGrantAuthorizationSeed</c> that <c>AuthorizationSeedHostedService</c> runs at boot.
/// </summary>
/// <remarks>
/// Slice 2 moved the unlock decision from the flat <c>TeamMembership.Permissions</c> set onto the access-grant
/// closure, but left the founder's holding in the flat set. On the desktop plane the route principal is
/// <c>NodeCallerParty.OperatorParty</c> (<c>"local"</c>) — no selected-session principal is bound — and the
/// install seeded no grant with that subject, so <c>unlock.granted</c> flipped from true to false. These two
/// tests are that regression, and the fence that keeps the holding from becoming a blanket allow.
/// </remarks>
public sealed class LifecycleUnlockGrantEndToEndTests : IAsyncLifetime
{
    private static readonly TeamId FounderTeam = new(new Guid("77777777-7777-7777-7777-777777777777"));
    private static readonly TenantId FounderTenant = ActiveTeamTenantContext.ProjectTenantId(FounderTeam);
    private static readonly DateTimeOffset At = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);

    private string _dir = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "t205-unlock-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    /// <summary>An install seeded the way <c>AuthorizationSeedHostedService</c> seeds it at boot.</summary>
    private static async Task<ServiceProvider> SeededInstallAsync()
    {
        var services = new ServiceCollection();
        services.AddAccessGrantModule();
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<AccessGrantAuthorizationSeed>()
            .InstallAsync(FounderTenant, At, AuthorizationSeedProfile.Production);
        return provider;
    }

    [Fact]
    public async Task The_desktop_founder_reads_a_granted_unlock_through_the_real_gate()
    {
        await using var install = await SeededInstallAsync();
        await using var h = await Harness.StartAsync(
            _dir, install.GetRequiredService<AuthorizationGate>(), FounderTeam);

        using var doc = await h.GetLifecycleAsync();

        Assert.True(
            doc.RootElement.GetProperty("unlock").GetProperty("granted").GetBoolean(),
            "the desktop founder principal must hold workshop:unlock through the access-grant closure");
    }

    [Fact]
    public async Task A_principal_that_is_not_the_founder_is_denied_by_the_same_install()
    {
        await using var install = await SeededInstallAsync();
        var authority = new NodeWorkshopUnlockAuthority(install.GetRequiredService<AuthorizationGate>());

        var decision = await authority.AuthorizeAsync(
            new AuthorizationWriteContext(new ActorId("someone-else"), FounderTenant, At));

        Assert.IsType<WorkshopUnlockDecision.Denied>(decision);
    }

    [Fact]
    public void The_seeded_node_operator_principal_is_the_desktop_caller_party()
    {
        // The seed lives in a package that cannot reference the node host, so this equality is what keeps
        // the seeded subject and the desktop route's principal from drifting apart silently.
        Assert.Equal(ActiveTeamAuthorizationContext.LocalUserId, AccessGrantAuthorizationSeed.NodeOperatorPrincipal);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public HttpClient Client { get; }

        private Harness(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public static async Task<Harness> StartAsync(string dir, AuthorizationGate gate, TeamId activeTeam)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();

            app.Use(async (http, next) =>
            {
                // Desktop plane: no SelectedSessionRequestPrincipal, so NodeCallerParty.Resolve falls back
                // to the operator party — the principal a real desktop founder's request carries.
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
                await next(http);
            });
            LifecycleRoutes.Map(
                app.MapSelectedSessionProductGroup(),
                FileTenantGovernanceStateStore.InDirectory(dir, () => At),
                new NodeWorkshopUnlockAuthority(gate),
                new SingleTeamAccessor(activeTeam),
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

    private sealed class SingleTeamAccessor : IActiveTeamAccessor
    {
        public SingleTeamAccessor(TeamId teamId) => Active = new TeamContext(
            teamId, "Founder Team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        public TeamContext? Active { get; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
