using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

public sealed class SharedHostedWebAppSealTests
{
    [Fact(DisplayName = "Route-fence violation survives seal degradation catch and refuses listener start")]
    public async Task RouteFenceViolation_Refuses_Shared_Listener_Start()
    {
        var outer = new ServiceCollection();
        outer.AddTestKernelClock();
        outer.AddLogging();
        outer.AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor());
        outer.AddSingleton(new NodeCallerSessionToken("test-session-token"));
        var outerProvider = outer.BuildServiceProvider();

        var registry = new LocalNodeExecutableEndpointRegistry();
        var app = new SharedHostedWebApp(
            outerProvider,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            registry,
            outerProvider.GetRequiredService<
                Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
            outerProvider.GetRequiredService<TimeProvider>());
        var violatingPath = $"{FormsRoutes.RouteBase}/outside-required-group";
        app.MapApiRoutes(routes =>
        {
            var endpoint = new RouteEndpointBuilder(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse(violatingPath),
                order: 0).Build();
            ((IEndpointRouteBuilder)routes).DataSources.Add(new DefaultEndpointDataSource(endpoint));
        });

        // This directly drives SharedHostedWebApp.StartAsync, including its sealing catch, and proves
        // the shared listener refuses before Kestrel binds. It does not drive the outer IHost's
        // hosted-service orchestration or Program.cs's full production endpoint composition.
        var exception = await Assert.ThrowsAsync<RouteFenceViolationException>(
            () => app.StartAsync(new CancellationToken(canceled: true)));

        Assert.Contains(FormsRoutes.RouteBase, exception.Message, StringComparison.Ordinal);
        Assert.Contains("DesktopPlaneOnly", exception.Message, StringComparison.Ordinal);
        Assert.Null(app.SelectedUrl);
        Assert.False(registry.IsSealed);
    }

    private sealed class NoTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active => null;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }
}
