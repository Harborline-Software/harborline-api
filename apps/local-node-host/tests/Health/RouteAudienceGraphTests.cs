using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

[Collection("Harborline process environment")]
public sealed class RouteAudienceGraphTests
{
    [Fact]
    public void Compatibility_Baseline_Apparatus_Is_Absent()
    {
        const string resourceName =
            "Harborline.Api.LocalNodeHost.Health.legacy-route-audience-baseline.tsv";
        var hostAssembly = typeof(UnclassifiedRouteAudienceGuard).Assembly;
        var hostSourceRoot = LocateHostSourceRoot();

        Assert.DoesNotContain(resourceName, hostAssembly.GetManifestResourceNames());
        Assert.Empty(Directory.EnumerateFiles(
            hostSourceRoot,
            "legacy-route-audience-baseline.tsv",
            SearchOption.AllDirectories));
        var hostSourceFiles = Directory
            .EnumerateFiles(hostSourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(sourcePath =>
            {
                var firstSegment = Path.GetRelativePath(hostSourceRoot, sourcePath)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                return firstSegment is not ("bin" or "obj" or "tests" or "tools");
            });
        Assert.DoesNotContain(hostSourceFiles, sourcePath => File.ReadAllText(sourcePath).Contains(
                "LegacyRouteAudienceBaseline",
                StringComparison.Ordinal));
        Assert.Null(hostAssembly.GetType(
            "Harborline.Api.LocalNodeHost.Health.LegacyRouteAudienceBaseline",
            throwOnError: false));
    }

    [Fact]
    public async Task Every_Supported_Profile_Graph_Is_Fully_Classified()
    {
        var profiles = LocalNodeHostedComponentCatalog.SupportedEndpointProfiles.ToArray();
        // +5 in every profile since ticket 213 slice 2: the consent-record routes (one read, one
        // request, three transitions), all DesktopPlaneOnly.
        // Ticket 329 adds the holders read to the existing desktop administration group in all six.
        // Ticket 331 adds one desktop-only authorized trace read in each profile.
        // Ticket 362 adds the selected-session narrow-member route (POST, beside revoke) in the four
        // web-enabled profiles, and slice 2 retires the permissions route from the same four.
        int[] expectedClassifiedCounts = [219, 236, 237, 230, 247, 248];

        Assert.Equal(6, profiles.Length);
        for (var index = 0; index < profiles.Length; index++)
        {
            var snapshot = await CaptureProfileAsync(profiles[index]);
            var pairs = snapshot.Endpoints
                .SelectMany(endpoint => endpoint.MethodRouteFences.Select(method => new
                {
                    endpoint.RoutePattern,
                    method.HttpMethod,
                    method.RouteFenceKind,
                }))
                .ToArray();
            var holders = Assert.Single(pairs, pair =>
                pair.HttpMethod == "GET" && pair.RoutePattern == AccessHoldersRead.Route);
            Assert.Equal(RouteFenceKind.DesktopPlaneOnly, holders.RouteFenceKind);
            var trace = Assert.Single(pairs, pair => pair.HttpMethod == "GET"
                && pair.RoutePattern == AuthorizationAdminRoutes.RouteBase + "/traces/{auditId:guid}");
            Assert.Equal(RouteFenceKind.DesktopPlaneOnly, trace.RouteFenceKind);
            var classified = pairs.Count(pair => pair.RouteFenceKind is not null);
            var unclassified = pairs
                .Where(pair => pair.RouteFenceKind is null)
                .Select(pair => $"{pair.HttpMethod} {pair.RoutePattern}")
                .ToArray();
            var report =
                $"profile web={profiles[index].WebClientEnabled}, " +
                $"llm={profiles[index].LlmProxyEnabled}, " +
                $"scheduling={profiles[index].SchedulingDogfoodEnabled}: " +
                $"classified={classified}, unclassified={unclassified.Length}";

            Assert.True(
                unclassified.Length == 0,
                report + $"; offending pairs: {string.Join(", ", unclassified)}");
            Assert.True(
                classified == expectedClassifiedCounts[index],
                report);
            Assert.Equal(pairs.Length, classified);
        }
    }

    private static async Task<LocalNodeExecutableEndpointSnapshot> CaptureProfileAsync(
        LocalNodeHostedComponentProfile profile)
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ticket-066-graph-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        using var environment = new ProfileEnvironment(profile);
        var started = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await LocalNodeHostRuntime.StartAsync(
                    "ticket-066-graph-token",
                    dataDirectory,
                    timeout.Token)
                .WaitAsync(timeout.Token);
            started = true;

            var services = Assert.IsAssignableFrom<IServiceProvider>(
                LocalNodeHostRuntime.CurrentServices);
            var registry = services.GetRequiredService<LocalNodeExecutableEndpointRegistry>();
            Assert.True(registry.IsSealed);
            return registry.Current;
        }
        finally
        {
            if (started)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await LocalNodeHostRuntime.StopAsync(timeout.Token);
            }
            TryDelete(dataDirectory);
        }
    }

    private static string LocateHostSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Harborline.LocalNodeHost.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the local-node host source root.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup must not obscure the graph assertion.
        }
    }

    private sealed class ProfileEnvironment : IDisposable
    {
        private static readonly string[] Keys =
        [
            "DOTNET_ENVIRONMENT",
            "ASPNETCORE_ENVIRONMENT",
            "ASPNETCORE_URLS",
            "LocalNode__HealthPort",
            "LocalNode__RootSeedHex",
            "LocalNode__WebClient__Enabled",
            "LocalNode__WebClient__LlmUpstreamBase",
            "LocalNode__SchedulingDogfood__Enabled",
            "LocalNode__MultiTeam__Enabled",
            "LocalNode__MultiTeam__TeamBootstraps__0__TeamId",
            "LocalNode__MultiTeam__TeamBootstraps__0__DisplayName",
        ];

        private readonly Dictionary<string, string?> _previous = Keys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        internal ProfileEnvironment(LocalNodeHostedComponentProfile profile)
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
            Environment.SetEnvironmentVariable("LocalNode__HealthPort", "0");
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", new string('6', 64));
            Environment.SetEnvironmentVariable(
                "LocalNode__WebClient__Enabled",
                profile.WebClientEnabled ? "true" : "false");
            Environment.SetEnvironmentVariable(
                "LocalNode__WebClient__LlmUpstreamBase",
                profile.LlmProxyEnabled ? "http://127.0.0.1:11434" : null);
            Environment.SetEnvironmentVariable(
                "LocalNode__SchedulingDogfood__Enabled",
                profile.SchedulingDogfoodEnabled ? "true" : "false");
            Environment.SetEnvironmentVariable(
                "LocalNode__MultiTeam__Enabled",
                profile.WebClientEnabled ? "true" : "false");
            Environment.SetEnvironmentVariable(
                "LocalNode__MultiTeam__TeamBootstraps__0__TeamId",
                profile.WebClientEnabled ? "06600000-0000-0000-0000-000000000066" : null);
            Environment.SetEnvironmentVariable(
                "LocalNode__MultiTeam__TeamBootstraps__0__DisplayName",
                profile.WebClientEnabled ? "Ticket 066 graph" : null);
        }

        public void Dispose()
        {
            foreach (var pair in _previous)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}
