using System.Reflection;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.LocalNodeHost;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// Pins the selected-session family in two stages: the production composition root must map
/// <see cref="HostedWebSessionApiEndpoint"/>, and that real registrar must map the exact HTTP method
/// and path set when Microsoft DI resolves it. Route-level fixtures call the individual route
/// families directly and cannot detect either removal.
/// </summary>
/// <remarks>
/// This is the card's two-stage fallback. It does not boot the full <c>Program.cs</c> service graph,
/// construct <see cref="SharedHostedWebApp"/>, prove hosted-service start ordering, start Kestrel, or
/// prove the executable registry seals. The existing composed-host smoke tests own full-host boot;
/// this test owns production registration plus the registrar's real HTTP method and path output
/// without the multi-minute composition path.
/// </remarks>
public sealed class SelectedSessionProductionMountTests
{
    private const string ProductionMapping =
        "Add<WebSession.HostedWebSessionApiEndpoint>(mappers, services, listener);";

    private static readonly (string Method, string Path)[] ExpectedRoutes =
    [
        ("POST", "/api/session/account-challenge"),
        ("POST", "/api/session/account-setup-accept"),
        ("GET", "/api/session/admin/invitations"),
        ("POST", "/api/session/admin/invitations"),
        ("GET", "/api/session/admin/members"),
        ("POST", "/api/session/admin/members/permissions"),
        // Ticket 362 slice 1 - an administrator narrows a member's conferred grant (revoke-and-reissue).
        ("POST", "/api/session/admin/members/narrow"),
        ("POST", "/api/session/admin/members/revoke"),
        ("GET", "/api/session/antiforgery"),
        ("POST", "/api/session/connect-device"),
        ("POST", "/api/session/founder-bind"),
        ("POST", "/api/session/login"),
        ("POST", "/api/session/logout"),
        ("GET", "/api/session/me"),
        ("POST", "/api/session/recovery-accept"),
        ("POST", "/api/session/select"),
        ("POST", "/api/session/switch"),
        ("GET", "/api/session/whoami"),
    ];

    [Fact]
    public void Program_Registration_And_Registrar_Pin_The_Selected_Session_Route_Set()
    {
        var program = File.ReadAllText(Path.Combine(LocateHostRoot(), "Health", "LocalNodeEndpointMapping.cs"));
        Assert.True(
            program.Contains(ProductionMapping, StringComparison.Ordinal),
            "LocalNodeEndpointMapping does not map HostedWebSessionApiEndpoint; the production " +
            "selected-session route family is not mounted.");

        var provider = BuildRouteMappingProvider();
        var endpoint = provider.GetServices<IHostedService>()
            .OfType<HostedWebSessionApiEndpoint>()
            .Single();
        using var routeServices = new ServiceCollection()
            .AddLogging()
            .AddRouting()
            .BuildServiceProvider();
        var routes = new TestEndpointRouteBuilder(routeServices);

        endpoint.MapRoutes(routes);

        var actualRoutes = routes.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => (
                Path: endpoint.RoutePattern.RawText,
                Methods: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []))
            .Where(route =>
                route.Path?.StartsWith("/api/session/", StringComparison.Ordinal) is true)
            .SelectMany(route => route.Methods.Select(method => (
                Method: method,
                Path: route.Path!)))
            .OrderBy(route => route.Path, StringComparer.Ordinal)
            .ThenBy(route => route.Method, StringComparer.Ordinal)
            .ToArray();
        var missing = ExpectedRoutes.Except(actualRoutes).ToArray();
        var unexpected = actualRoutes.Except(ExpectedRoutes).ToArray();
        Assert.True(
            missing.Length == 0 &&
            unexpected.Length == 0 &&
            actualRoutes.Length == ExpectedRoutes.Length,
            $"Selected-session HTTP method and path set differs. Missing: {Format(missing)}; " +
            $"unexpected: {Format(unexpected)}; expected count: {ExpectedRoutes.Length}; " +
            $"actual count: {actualRoutes.Length}.");
    }

    private static ServiceProvider BuildRouteMappingProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestKernelClock();
        services.AddSingleton(Uninitialized<SharedHostedWebApp>());
        services.AddSingleton(new WebLoginRateLimiter(
            Options.Create(new NodeWebClientOptions()),
            TimeProvider.System,
            NullLogger<WebLoginRateLimiter>.Instance));

        services.AddSingleton(Inert<INodeWebSessionAuthority>());
        services.AddSingleton(Inert<IWebAccountAccessChallengeIssuer>());
        services.AddSingleton(Inert<IWebTenantSelectionAuthority>());
        services.AddSingleton(Inert<IWebTenantSwitchAuthority>());
        services.AddSingleton(Inert<IWebAntiforgeryPolicy>());
        services.AddSingleton(Inert<IWebSelectedSessionLogoutAuthority>());
        services.AddSingleton(Inert<IWebFounderBindAuthority>());
        services.AddSingleton(Inert<IAccountSetupAcceptanceAuthority>());
        services.AddSingleton(new PairingRedeemRateLimiter(clock: TimeProvider.System));
        services.AddSingleton(Inert<IWebChosenCredentialFactory>());
        services.AddSingleton(Inert<IAccountRecoveryAuthority>());
        services.AddSingleton(Inert<IAdminTeamAccessAuthority>());
        services.AddSingleton(Inert<IWebSelectedSessionIdentityAuthority>());
        services.AddSingleton(Uninitialized<WebAdmittedMemberPairingTokenMint>());
        services.AddSingleton(Uninitialized<NodeTeamRoster>());

        services.AddHostedService<HostedWebSessionApiEndpoint>();
        return services.BuildServiceProvider();
    }

    private static T Inert<T>() where T : class =>
        DispatchProxy.Create<T, InertDispatchProxy>();

    private static T Uninitialized<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static string Format(IEnumerable<(string Method, string Path)> values)
    {
        var joined = string.Join(", ", values.Select(route => $"{route.Method} {route.Path}"));
        return joined.Length == 0 ? "(none)" : joined;
    }

    private static string LocateHostRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine("apps", "local-node-host"),
                         "local-node-host",
                     })
            {
                var path = Path.Combine(directory.FullName, candidate);
                if (File.Exists(Path.Combine(path, "Program.cs")))
                {
                    return path;
                }
            }

            if (File.Exists(Path.Combine(directory.FullName, "Program.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Program.cs not found walking up from {AppContext.BaseDirectory}.");
    }

    private class InertDispatchProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                $"The route-map test invoked inert collaborator '{targetMethod?.Name}'.");
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider serviceProvider) :
        IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() =>
            new ApplicationBuilder(ServiceProvider);
    }
}
