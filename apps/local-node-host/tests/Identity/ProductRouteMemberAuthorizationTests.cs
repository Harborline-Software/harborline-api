using System.Net;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Card 3558 — verifies that a web-plane member without the route permission is refused by the
/// product data route, rather than merely authenticated through to the handler.
/// </summary>
/// <remarks>
/// <para>
/// This remains a real-listener probe with a real selected-session cookie: the only proof of a
/// route-level fence is a real request at the real route. A filter registered through
/// <c>AddEndpointFilter</c> is a factory rather than queryable endpoint metadata and cannot be seen
/// by reading source.
/// </para>
/// <para>The member holds no contacts permission, so the route must fail closed with 403.</para>
/// </remarks>
[Trait("PlanCard", "MTW-2-3558")]
public sealed class ProductRouteMemberAuthorizationTests
{
    private const string CallerToken = "product-route-authz-caller-token";
    private const string MemberHandle = "handle-member-with-at-least-256-bits-of-authz-entropy-0000";

    private static readonly TeamId OperatorTeam =
        new(Guid.Parse("35580000-0000-0000-0000-0000000000fe"));

    [Fact(DisplayName =
        "3558: a web-plane member without contacts:read receives 403")]
    public async Task Member_Request_To_A_Product_Data_Route()
    {
        await using var fixture = await Fixture.CreateAsync();

        using var asMember = await fixture.SendAsMemberAsync(
            new HttpRequestMessage(HttpMethod.Get, ContactRoutes.RouteBase));
        var memberBody = await asMember.Content.ReadAsStringAsync();

        using var asAnonymous = await fixture.SendAnonymouslyAsync(
            new HttpRequestMessage(HttpMethod.Get, ContactRoutes.RouteBase));

        using var asBearer = await fixture.SendAsBearerAsync(
            new HttpRequestMessage(HttpMethod.Get, ContactRoutes.RouteBase));

        // CONTROL 1 — caller authentication IS enforced. Without this the member result would be an
        // artifact of a wide-open listener rather than a statement about authorization.
        Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);
        Assert.Contains("authorization.permission_required", memberBody, StringComparison.Ordinal);

        // The desktop bearer has no bridged operator permission in this minimal fixture and must
        // also fail closed; neither request reaches the handler.
        Assert.Equal(HttpStatusCode.Forbidden, asBearer.StatusCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _outerProvider;
        private readonly SharedHostedWebApp _app;
        private readonly HttpClient _client;

        private Fixture(ServiceProvider outerProvider, SharedHostedWebApp app, HttpClient client)
        {
            _outerProvider = outerProvider;
            _app = app;
            _client = client;
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var activeTeam = new FixedActiveTeamAccessor(new TeamContext(
                OperatorTeam, "Operator Team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));

            // A real on-disk SQLite store, because the route reads through EF and an unresolvable
            // context would make this test fail for a setup reason rather than answer the question.
            var databasePath = Path.Combine(
                Path.GetTempPath(), $"authz-probe-{Guid.NewGuid():N}.db");

            var outer = new ServiceCollection();
            outer.AddTestKernelClock();
            outer.AddLogging(b => b.ClearProviders());
            outer.AddDbContextFactory<LocalNodeDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
            outer.AddSingleton<IActiveTeamAccessor>(activeTeam);
            outer.AddSingleton<Harborline.Api.Foundation.MultiTenancy.ITenantContext, ActiveTeamTenantContext>();
            outer.AddSingleton(new NodeCallerSessionToken(CallerToken));
            outer.AddSingleton<IWebSelectedSessionPrincipalAuthority>(new FixedSelectedSessionAuthority());
            outer.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
                Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
            outer.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
                Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
            var identityDatabasePath = Path.Combine(
                Path.GetTempPath(), $"authz-probe-identity-{Guid.NewGuid():N}.db");
            outer.AddDbContextFactory<NodeLocalInstallationIdentityDbContext>(
                o => o.UseSqlite($"Data Source={identityDatabasePath}"));
            outer.AddNodeContacts();

            var provider = outer.BuildServiceProvider();
            await using (var db = await provider
                .GetRequiredService<IDbContextFactory<LocalNodeDbContext>>()
                .CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }
            await using (var db = await provider
                .GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>()
                .CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }
            await InstallationIdentityTestBootstrap.BootstrapAsync(
                provider.GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>());

            var app = new SharedHostedWebApp(
                provider,
                Options.Create(new LocalNodeOptions { HealthPort = 0 }),
                new LocalNodeExecutableEndpointRegistry(),
                provider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
                provider.GetRequiredService<TimeProvider>());

            var endpoint = new HostedContactApiEndpoint(
                app,
                provider.GetRequiredService<NodeEfPartyRepository>(),
                provider.GetRequiredService<ContactCrdtProjection>(),
                activeTeam,
                provider.GetRequiredService<ILogger<HostedContactApiEndpoint>>(),
                provider.GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>(),
                provider.GetRequiredService<TimeProvider>());
            await endpoint.StartAsync(CancellationToken.None);
            await app.StartAsync(CancellationToken.None);

            return new Fixture(
                provider, app, new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) });
        }

        internal async Task<HttpResponseMessage> SendAsMemberAsync(HttpRequestMessage request)
        {
            using (request)
            {
                request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={MemberHandle}");
                return await _client.SendAsync(request);
            }
        }

        internal async Task<HttpResponseMessage> SendAsBearerAsync(HttpRequestMessage request)
        {
            using (request)
            {
                request.Headers.Add(NodeCallerSessionToken.HeaderName, "Bearer " + CallerToken);
                return await _client.SendAsync(request);
            }
        }

        internal async Task<HttpResponseMessage> SendAnonymouslyAsync(HttpRequestMessage request)
        {
            using (request)
            {
                return await _client.SendAsync(request);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _outerProvider.DisposeAsync();
        }
    }

    private sealed class FixedSelectedSessionAuthority : IWebSelectedSessionPrincipalAuthority
    {
        // A baseline collaborator: a real selected session, no data permission of any kind attached.
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(selectedHandle, MemberHandle, StringComparison.Ordinal)
                ? new SelectedSessionRequestPrincipal(
                    "account-product-route-authz",
                    new TenantId(OperatorTeam.Value.ToString("D")),
                    new PrincipalUserId("principal-product-route-authz"),
                    new CanonicalPartyReference("party-product-route-authz-member"),
                    "membership-product-route-authz",
                    2,
                    [new PinnedGrantOwnerVersion("grant-product-route-authz", 3)],
                    5,
                    "session-product-route-authz",
                    "coordination-product-route-authz")
                : null);
    }

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }
}
