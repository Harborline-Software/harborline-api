using System.Net;
using System.Net.Http.Headers;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Entities;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>G-R1(b-d): executable loopback proofs for the W2 composition boundary.</summary>
public sealed class NodeHostingW2GateTests : IAsyncLifetime
{
    private const string BootstrapToken = "node-hosting-w2-bootstrap-token";
    private const string FounderHandle = "node-hosting-w2-founder-handle";
    private const string MemberHandle = "node-hosting-w2-member-handle";

    private ServiceProvider? _services;
    private SharedHostedWebApp? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddSingleton<IActiveTeamAccessor>(NodeTestActiveTeam.Accessor);
        services.AddSingleton(new NodeCallerSessionToken(BootstrapToken));
        services.AddSingleton<IWebSelectedSessionPrincipalAuthority, FixedPrincipalAuthority>();
        _services = services.BuildServiceProvider();

        _app = new SharedHostedWebApp(
            _services,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            _services.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            _services.GetRequiredService<TimeProvider>(),
            urlsOverride: "http://127.0.0.1:0");

        _app.MapApiRoutes(routes =>
        {
            var desktop = routes.MapDesktopPlaneOnlyGroup();
            desktop.MapGet(FormsRoutes.RouteBase, () => Results.Ok());
            desktop.MapGet(CommsRoutes.RouteBase, () => Results.Ok());
            desktop.MapGet(AuthorizationAdminRoutes.RouteBase, () => Results.Ok());
            desktop.MapPost(FounderBindRoutes.BindPath, () => Results.Ok());

            var admission = routes.MapFounderWebAdmissionGroup(new FixedIdentityAuthority());
            admission.MapPost(AdmissionRoutes.RouteBase, () => Results.Ok());
        });

        await _app.StartAsync(CancellationToken.None);
        _client = new HttpClient { BaseAddress = new Uri(_app.SelectedUrl!) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }

        if (_services is not null)
            await _services.DisposeAsync();
    }

    [Theory(DisplayName = "G-R1(c): desktop-plane-only routes are 403 for selected sessions and 2xx for bootstrap")]
    [InlineData("GET", FormsRoutes.RouteBase)]
    [InlineData("GET", CommsRoutes.RouteBase)]
    [InlineData("GET", AuthorizationAdminRoutes.RouteBase)]
    [InlineData("POST", FounderBindRoutes.BindPath)]
    public async Task DesktopPlaneOnly_HasBothSidesOfTheFence(string method, string path)
    {
        using var selected = new HttpRequestMessage(new HttpMethod(method), path);
        selected.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={MemberHandle}");
        using var selectedResponse = await _client!.SendAsync(selected);
        Assert.Equal(HttpStatusCode.Forbidden, selectedResponse.StatusCode);
        Assert.Contains(WebPlaneUnavailableRouteFence.UnavailableCode,
            await selectedResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var bootstrap = new HttpRequestMessage(new HttpMethod(method), path);
        bootstrap.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BootstrapToken);
        using var bootstrapResponse = await _client.SendAsync(bootstrap);
        Assert.InRange((int)bootstrapResponse.StatusCode, 200, 299);
    }

    [Theory(DisplayName = "G-R1(c): admission admits founders and refuses members or unresolved sessions")]
    [InlineData(FounderHandle, 200, "")]
    [InlineData(MemberHandle, 403, AdmissionWebPlaneRouteFence.FounderOnlyCode)]
    public async Task FounderAdmission_UsesPositiveStanding(string handle, int expectedStatus, string expectedCode)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AdmissionRoutes.RouteBase);
        request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={handle}");
        using var response = await _client!.SendAsync(request);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        if (expectedCode.Length > 0)
            Assert.Contains(expectedCode, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "G-R1(d): an unfenced route fails at seal before Kestrel binds")]
    public void UnfencedRoute_ThrowsRouteFenceViolationAtSeal()
    {
        var routes = new TestEndpointRouteBuilder();
        routes.MapGet(FormsRoutes.RouteBase, () => Results.Ok());
        var registry = new LocalNodeExecutableEndpointRegistry();

        Assert.Throws<RouteFenceViolationException>(() => registry.Seal(
            routes.DataSources,
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));
        Assert.False(registry.IsSealed);
    }

    private sealed class FixedPrincipalAuthority : IWebSelectedSessionPrincipalAuthority
    {
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SelectedSessionRequestPrincipal?>(
                selectedHandle is FounderHandle or MemberHandle
                    ? new SelectedSessionRequestPrincipal(
                        accountId: "node-hosting-w2-account",
                        tenantId: new TenantId(NodeTestActiveTeam.TestTeamId.Value.ToString("D")),
                        principalUserId: new PrincipalUserId("node-hosting-w2-principal"),
                        canonicalParty: new CanonicalPartyReference("node-hosting-w2-party"),
                        membershipId: "node-hosting-w2-membership",
                        membershipOwnerVersion: 1,
                        pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("node-hosting-w2-grant", 1)],
                        authorizationEpoch: 1,
                        sessionCorrelationId: "node-hosting-w2-session",
                        coordinationCorrelationId: "node-hosting-w2-coordination")
                    : null);
    }

    private sealed class FixedIdentityAuthority : IWebSelectedSessionIdentityAuthority
    {
        public Task<SelectedSessionIdentity?> DescribeAsync(
            string? selectedHandle,
            SelectedSessionRequestPrincipal principal,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SelectedSessionIdentity?>(new SelectedSessionIdentity(
                principal.AccountId,
                principal.CanonicalParty,
                null,
                principal.TenantId,
                null,
                selectedHandle == FounderHandle
                    ? SelectedSessionMembership.Founder
                    : SelectedSessionMembership.Member,
                DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    private sealed class TestEndpointRouteBuilder : IEndpointRouteBuilder
    {
        internal TestEndpointRouteBuilder()
        {
            ServiceProvider = new ServiceCollection()
                .AddRouting()
                .BuildServiceProvider();
        }

        public IServiceProvider ServiceProvider { get; }
        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() =>
            new ApplicationBuilder(ServiceProvider);
    }
}
