using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.OperatorCli;

/// <summary>
/// Ticket 072 (harborline-control): reconciles the operator CLI's coverage manifest against the
/// node's sealed executable endpoint graph, so the CLI cannot silently fall behind the surface it
/// wraps (ADR 0020's "tracked, not tolerated"). The authority is the graph ASP.NET actually
/// executes, captured the same way the ticket 066 route-audience proof captures it - never a
/// hand-maintained route list.
/// </summary>
[Collection("Harborline process environment")]
public sealed class CliCoverageReconciliationTests
{

    [Fact]
    public async Task Every_Operator_Route_Is_Covered_By_A_Verb_Or_An_Exemption_And_Nothing_Is_Stale()
    {
        var manifest = LoadManifest();
        var snapshot = await CaptureRichestProfileAsync();
        var executablePairs = snapshot.Endpoints
            .SelectMany(endpoint => endpoint.MethodRouteFences.Select(method => new
            {
                Pair = $"{method.HttpMethod} {endpoint.RoutePattern}",
                method.RouteFenceKind,
            }))
            .ToArray();
        var executableSet = executablePairs.Select(item => item.Pair).ToHashSet(StringComparer.Ordinal);

        // Direction 1: EVERY pair in the sealed graph is either a CLI verb's route or an exemption.
        // Wave-1 finding opcli-1: an audience filter here is an evasion channel - 6 of the 8 verb
        // routes live in audiences the original filter never required (SelectedSessionProduct,
        // DeviceReachableProductData, PreAuthOperational), so a new operator route in those classes
        // was invisible to the gate. The whole graph is the only non-evadable rule; audience
        // reasoning belongs in each exemption's rationale, not in the filter.
        var operatorPairs = executablePairs
            .Select(item => item.Pair)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var uncovered = operatorPairs
            .Where(pair => !manifest.Verbs.ContainsKey(pair) && !manifest.Exemptions.ContainsKey(pair))
            .ToArray();
        Assert.True(
            uncovered.Length == 0,
            "Operator routes with no CLI verb and no exemption in apps/node-operator-cli/cli-coverage.json: "
                + string.Join(", ", uncovered));

        // Direction 2: the manifest cannot rot. A verb or exemption naming a pair the sealed graph
        // no longer serves is a stale claim, and stale coverage reads as "covered" forever.
        var stale = manifest.Verbs.Keys.Concat(manifest.Exemptions.Keys)
            .Where(pair => !executableSet.Contains(pair))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            stale.Length == 0,
            "Manifest entries naming routes absent from the sealed graph: " + string.Join(", ", stale));

        // An exemption is a decision, not an escape hatch: it carries a rationale of substance,
        // the same bar quality profiles set for a not-applicable disposition.
        foreach (var exemption in manifest.Exemptions)
        {
            Assert.True(
                exemption.Value.Trim().Length >= 20,
                $"Exemption for '{exemption.Key}' lacks a bounded rationale.");
        }
    }

    private static CoverageManifest LoadManifest()
    {
        var path = Path.Combine(
            LocateHostSourceRoot(), "..", "node-operator-cli", "cli-coverage.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        return new CoverageManifest(ReadMap(root, "verbs"), ReadMap(root, "exemptions"));
    }

    private static Dictionary<string, string> ReadMap(JsonElement root, string property) =>
        root.GetProperty(property).EnumerateObject().ToDictionary(
            item => item.Name,
            item => item.Value.GetString() ?? string.Empty,
            StringComparer.Ordinal);

    private sealed record CoverageManifest(
        Dictionary<string, string> Verbs,
        Dictionary<string, string> Exemptions);

    private static async Task<LocalNodeExecutableEndpointSnapshot> CaptureRichestProfileAsync()
    {
        // The all-components profile maps the largest executable graph; ticket 066's audience proof
        // walks every profile and shows the others map strict subsets of these route families.
        var profile = Assert.Single(
            LocalNodeHostedComponentCatalog.SupportedEndpointProfiles,
            candidate => candidate is { WebClientEnabled: true, LlmProxyEnabled: true, SchedulingDogfoodEnabled: true });
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"harborline-ticket-072-coverage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        using var environment = new RouteGraphProfileEnvironment(profile);
        var started = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await LocalNodeHostRuntime.StartAsync(
                    "ticket-072-coverage-token",
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
            // Best-effort cleanup must not obscure the coverage assertion.
        }
    }

    private sealed class RouteGraphProfileEnvironment : IDisposable
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

        internal RouteGraphProfileEnvironment(LocalNodeHostedComponentProfile profile)
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
            Environment.SetEnvironmentVariable("LocalNode__HealthPort", "0");
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", new string('7', 64));
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
                profile.WebClientEnabled ? "07200000-0000-0000-0000-000000000072" : null);
            Environment.SetEnvironmentVariable(
                "LocalNode__MultiTeam__TeamBootstraps__0__DisplayName",
                profile.WebClientEnabled ? "Ticket 072 coverage" : null);
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
