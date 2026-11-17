using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.EngineRoom;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Harborline.Api.LocalNodeHost.Tests.Telemetry;

[Collection("Harborline process environment")]
public sealed class TelemetrySentinelFenceTests
{
    private sealed record KnownLeak(string Member, TelemetrySentinelFence.Hit Hit, string Reason, string Owner);

    // Frozen, not blessed: each observed leak must match exactly until T-575 (L037) fixes it.
    private static readonly KnownLeak[] KnownLeaks =
    [
        new("Search", new("activity", "", "url.query"),
            "The framework request activity on the empty-name source retains the query string.", "T-575 / L037"),
        new("Views", new("activity", "", "url.query"),
            "The framework request activity on the empty-name source retains the filter argument.", "T-575 / L037"),
    ];

    [Fact]
    public async Task Records_command_leaves_no_sentinel_in_telemetry()
    {
        await using var host = await Host.OpenAsync();
        var sentinel = Sentinel("records");
        var position = host.Fence.Mark();
        using var response = await host.Client.PostAsJsonAsync("/api/local-node/entities", new { legalName = sentinel });
        Assert.True(response.IsSuccessStatusCode, $"Records status: {response.StatusCode}");
        await host.AssertSilenceAsync("Records", position, sentinel);
    }

    [Fact]
    public async Task Forms_command_leaves_no_sentinel_in_telemetry()
    {
        await using var host = await Host.OpenAsync();
        var sentinel = Sentinel("forms");
        var position = host.Fence.Mark();
        await SaveAndSubmitAsync(host, "sentinel-forms", sentinel);
        await host.AssertSilenceAsync("Forms", position, sentinel);
    }

    [Fact]
    public async Task Views_command_leaves_no_sentinel_in_telemetry()
    {
        await using var host = await Host.OpenAsync();
        var sentinel = Sentinel("views");
        var position = host.Fence.Mark();
        using var response = await host.Client.GetAsync("/api/local-node/asset-registry/entities?type=" + sentinel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await host.AssertSilenceAsync("Views", position, sentinel);
    }

    [Fact]
    public async Task Rules_command_leaves_no_sentinel_in_telemetry()
    {
        await using var host = await Host.OpenAsync();
        var expressionSentinel = Sentinel("rulesexpression");
        var operandSentinel = Sentinel("rulesoperand");
        var position = host.Fence.Mark();
        await SaveAndSubmitAsync(host, "sentinel-rules", operandSentinel, expressionSentinel);
        await host.AssertSilenceAsync("Rules", position, expressionSentinel, operandSentinel);
    }

    [Fact]
    public async Task Data_exchange_command_leaves_no_sentinel_in_telemetry()
    {
        await using var host = await Host.OpenAsync();
        var sentinels = Enumerable.Range(0, 6).Select(_ => Sentinel("dataexchange")).ToArray();
        var path = Path.Combine(host.ImportDirectory, "erpnext.sql");
        await File.WriteAllTextAsync(path, $$"""
            CREATE TABLE `tabAccount` (`name` varchar(140) NOT NULL, PRIMARY KEY (`name`)) ENGINE=InnoDB;
            INSERT INTO `tabAccount` VALUES ('{{sentinels[0]}}'), ('{{sentinels[1]}}');
            CREATE TABLE `tabDocType` (`name` varchar(140) NOT NULL, PRIMARY KEY (`name`)) ENGINE=InnoDB;
            INSERT INTO `tabDocType` VALUES ('{{sentinels[2]}}');
            CREATE TABLE `tabProperty` (`name` varchar(140) NOT NULL, PRIMARY KEY (`name`)) ENGINE=InnoDB;
            INSERT INTO `tabProperty` VALUES ('{{sentinels[3]}}'), ('{{sentinels[4]}}'), ('{{sentinels[5]}}');
            """);
        var position = host.Fence.Mark();
        using var response = await host.Client.PostAsJsonAsync("/api/local-node/import/erpnext/preview", new { dumpFilePath = path });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await host.AssertSilenceAsync("Data exchange", position, sentinels);
    }

    [Fact]
    public async Task Search_command_leaves_no_sentinel_in_telemetry()
    {
        await using var host = await Host.OpenAsync();
        var sentinel = Sentinel("search");
        var position = host.Fence.Mark();
        using var response = await host.Client.GetAsync("/api/local-node/kg/search?q=" + sentinel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await host.AssertSilenceAsync("Search", position, sentinel);
    }

    [Fact]
    public async Task Sentinel_fence_reports_a_sentinel_planted_in_each_signal()
    {
        await using var host = await Host.OpenAsync();
        var sentinel = Sentinel("planted");
        var position = host.Fence.Mark();
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TelemetrySentinelFence.Planted")
            .LogInformation("Fence probe {Sentinel}", sentinel.ToUpperInvariant());
        using var source = new ActivitySource(EngineRoomMetrics.ActivitySourceName);
        using (var activity = source.StartActivity("sentinel-fence-probe"))
        {
            Assert.NotNull(activity);
            activity.SetTag("sentinel", sentinel.ToUpperInvariant());
        }
        using var meter = new Meter(EngineRoomMetrics.MeterName);
        meter.CreateCounter<int>("sentinel_fence_probe").Add(1, new KeyValuePair<string, object?>("sentinel", sentinel.ToUpperInvariant()));
        await host.DrainAsync(position);

        // Literal locations from T-502 acceptance 7, independent of the production names used to emit.
        Assert.Equal(new TelemetrySentinelFence.Hit[]
        {
            new("activity", "Sunfish.EngineRoom", "sentinel"),
            new("exported metric", "OTLP", "/v1/metrics"),
            new("exported trace", "OTLP", "/v1/traces"),
            new("log", "TelemetrySentinelFence.Planted", "Fence probe {Sentinel}"),
            new("measurement", "Sunfish.EngineRoom", "sentinel"),
        }, host.Fence.Find(sentinel));
    }

    private static string Sentinel(string member) => "zq7sentinel" + member + Guid.NewGuid().ToString("N");

    private static async Task SaveAndSubmitAsync(Host host, string id, string answer, string? expressionSentinel = null)
    {
        object Text(string value) => new { defaultLocale = "en", values = new Dictionary<string, string> { ["en"] = value } };
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["name"] = new { label = Text("Tenant name"), controlHint = "text", piiSensitivity = "None" },
                    ["unit"] = new { label = Text("Unit type"), piiSensitivity = "None" },
                },
                sections = new[] { new
                {
                    id = "applicant", title = Text("Applicant"), fields = new[] { "name", "unit" },
                    layout = new { kind = "grid", direction = "row", wrap = "wrap", columns = 2, gap = 4 },
                    fieldPlacement = new Dictionary<string, object> { ["unit"] = new { colSpan = 2, grow = 0 } },
                } },
                rules = expressionSentinel is null ? Array.Empty<object>() : new object[] { new
                {
                    id = "sentinel-check", tier = "JsonLogic", scope = "Schema", scopeTarget = "",
                    expression = "{\"!=\":[{\"var\":\"name\"},\"" + expressionSentinel + "\"]}", action = "Validate",
                } },
                title = Text("Tenant intake"), description = Text("Collect applicant details."),
            },
            fieldsMeta = new Dictionary<string, object>
            {
                ["name"] = new { type = "text", required = true, options = (string[]?)null },
                ["unit"] = new { type = "select", required = false, options = new[] { "studio", "one-bed" } },
            },
        };
        using var saved = await host.Client.PutAsJsonAsync("/api/local-node/forms/definitions/" + id, body);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var submitted = await host.Client.PostAsJsonAsync($"/api/local-node/forms/{id}/submit", new { name = answer, unit = "studio" });
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "t502-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> environment = new();
        private WebApplication? collector;
        internal TelemetrySentinelFence Fence { get; } = new();
        internal IServiceProvider Services => LocalNodeHostRuntime.CurrentServices!;
        internal HttpClient Client { get; private set; } = null!;
        internal string ImportDirectory => Path.Combine(directory, "import");

        internal static async Task<Host> OpenAsync()
        {
            var host = new Host();
            try
            {
                Directory.CreateDirectory(host.ImportDirectory);
                var builder = WebApplication.CreateBuilder();
                builder.Services.AddTestDesktopOperator();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                host.collector = builder.Build();
                host.collector.MapPost("/v1/metrics", host.Fence.ReceiveAsync);
                host.collector.MapPost("/v1/traces", host.Fence.ReceiveAsync);
                await host.collector.StartAsync();

                foreach (var (key, value) in new Dictionary<string, string>
                {
                    ["DOTNET_ENVIRONMENT"] = "Production", ["ASPNETCORE_ENVIRONMENT"] = "Production",
                    ["ASPNETCORE_URLS"] = "http://127.0.0.1:0", ["LocalNode__HealthPort"] = "0",
                    ["LocalNode__RootSeedHex"] = new string('3', 64), ["LocalNode__WebClient__Enabled"] = "false",
                    ["LocalNode__MultiTeam__Enabled"] = "false", ["LocalNode__SchedulingDogfood__Enabled"] = "false",
                    ["Logging__EventLog__LogLevel__Default"] = "None",
                    ["LocalNode__Diagnostics__OtlpEndpoint"] = host.collector.Urls.Single(),
                    ["LocalNode__ErpnextImport__ImportRoot"] = host.ImportDirectory,
                })
                {
                    host.environment[key] = Environment.GetEnvironmentVariable(key);
                    Environment.SetEnvironmentVariable(key, value);
                }
                await host.StartAsync();
                var tenant = NodeTenant.Resolve(host.Services.GetRequiredService<IActiveTeamAccessor>());
                var actor = Data.Identity.NodeOperatorIdentity.From(host.Services)!.Value;
                var at = host.Services.GetRequiredService<TimeProvider>().GetUtcNow();
                await host.Services.GetRequiredService<IGrantStore>().AppendAsync(tenant,
                    new AccessGrant(GrantId.New(), tenant, actor, RoleReference.Administrator, ScopeExpression.Parse("/"),
                        GrantResidency.Cache, new GrantValidity(at), GranterKind.Person, actor, at,
                        new GrantProvenance(GrantSourceKind.Manual,
                            new GrantReason(GrantReasonCodes.Manual, "T-502 telemetry fixture"), actor), at));
                host.Client.Dispose();
                await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
                await host.StartAsync();
                return host;
            }
            catch (Exception primary)
            {
                try { await host.DisposeAsync(); }
                catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
                throw;
            }
        }

        private async Task StartAsync()
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var uri = await LocalNodeHostRuntime.StartAsync("t502-route-token", directory, startup.Token,
                finalServiceRegistration: services => services.AddSingleton(Fence.LogProvider));
            Client = new HttpClient { BaseAddress = uri };
            Client.DefaultRequestHeaders.Add("Authorization", "Bearer t502-route-token");
        }

        internal Task DrainAsync(TelemetrySentinelFence.Position position)
        {
            Services.GetRequiredService<ILoggerFactory>().CreateLogger("TelemetrySentinelFence.Drain")
                .LogInformation("Telemetry fence drain probe");
            Services.GetRequiredService<EngineRoomTelemetry>().RecordSubsystemStatus(
                EngineRoomSubsystem.MainPropulsion, SubsystemStatus.Operational);
            Assert.True(Services.GetRequiredService<MeterProvider>().ForceFlush(30_000), "Metric exporter did not drain.");
            Assert.True(Services.GetRequiredService<TracerProvider>().ForceFlush(30_000), "Trace exporter did not drain.");
            Assert.True(Fence.HasPayloadAfter("/v1/metrics", position), "No metric payload after command start.");
            Assert.True(Fence.HasPayloadAfter("/v1/traces", position), "No trace payload after command start.");
            Assert.True(Fence.HasLogAfter(position, "TelemetrySentinelFence.Drain", "Telemetry fence drain probe"),
                "No drain probe log from the current host after command start.");
            return Task.CompletedTask;
        }

        internal async Task AssertSilenceAsync(string member, TelemetrySentinelFence.Position position, params string[] sentinels)
        {
            await DrainAsync(position);
            var expected = KnownLeaks.Where(leak => leak.Member == member).Select(leak => leak.Hit)
                .OrderBy(hit => hit.Signal, StringComparer.Ordinal).ThenBy(hit => hit.Source, StringComparer.Ordinal)
                .ThenBy(hit => hit.Location, StringComparer.Ordinal).ToArray();
            var actual = Fence.Find(sentinels);
            Assert.True(expected.SequenceEqual(actual),
                $"{member} telemetry locations differ. Expected:\n{string.Join("\n", expected.Select(hit => hit.ToString()))}" +
                $"\nObserved:\n{string.Join("\n", actual.Select(hit => hit.ToString()))}");
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            try { await LocalNodeHostRuntime.StopAsync(CancellationToken.None); }
            finally
            {
                foreach (var (key, value) in environment) Environment.SetEnvironmentVariable(key, value);
                try
                {
                    if (collector is not null) await collector.DisposeAsync();
                }
                finally
                {
                    Fence.Dispose();
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }
            }
        }
    }
}
