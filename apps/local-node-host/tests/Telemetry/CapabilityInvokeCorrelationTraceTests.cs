using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Harborline.Api.Protocol;
using Harborline.Api.Foundation.EngineRoom;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Health;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Harborline.Api.LocalNodeHost.Tests.Telemetry;

public sealed class CapabilityInvokeCorrelationTraceTests
{
    private const string InteropTestPath = "src/membrane/dotnet-correlation.interop.test.ts";

    [Fact]
    public async Task ProductionNodeIngressContinuesCapabilityCorrelationTrace()
    {
        const string traceIdAtMembrane = "4bf92f3577b34da6a3ce929d0e0e4736";
        var spans = new List<Activity>();
        using var listener = ListenForEngineRoomSpans(spans);
        await using var outer = new ServiceCollection()
            .AddTestKernelClock()
            .AddLogging()
            .AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor())
            .AddSingleton(new NodeCallerSessionToken(null))
            .BuildServiceProvider();
        await using var app = new SharedHostedWebApp(
            outer,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            outer.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
            outer.GetRequiredService<TimeProvider>());
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/membrane/invoke")
        {
            Content = JsonContent.Create(new
            {
                capabilityId = "image",
                core = new
                {
                    prompt = "trace bridge",
                    size = new { w = 64, h = 64 },
                    seed = 20,
                    count = 1,
                    format = "png",
                    timeout = 5_000,
                },
                providerInputs = new { },
                attachments = Array.Empty<object>(),
                correlationId = "body-correlation-id-must-not-win",
                idempotencyKey = "idem-trace-1",
                transport = "sync",
            }),
        };
        request.Headers.Add("x-capability-correlation-id", traceIdAtMembrane);

        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var span = Assert.Single(spans, activity => activity.OperationName == "capability.invoke");
        Assert.Equal(traceIdAtMembrane, span.TraceId.ToString());

        await app.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BridgeEmitsCapabilityInvokeSpanOnEngineRoomSource()
    {
        const string traceIdAtMembrane = "0af7651916cd43dd8448eb211c80319c";
        var spans = new List<Activity>();
        // Ticket 254b: ActivitySource.AddActivityListener registers PROCESS-WIDE, so a listener that only
        // filters on the source name collects every "Sunfish.EngineRoom" activity any other test class emits
        // while this one runs - EngineRoomTelemetryTests and EngineRoomOtlpExportTests both call
        // RecordSubsystemStatus on that source, and xunit runs the three Telemetry classes in parallel.
        // Assert.Single(spans) then sees 2+ items and the row goes red only when the machine is busy enough
        // to overlap them (four reds in loaded landing gates, green alone). Trace id is this test's own
        // isolation key, so admit only the spans belonging to this request.
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Sunfish.EngineRoom",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId.ToString() == traceIdAtMembrane) spans.Add(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHarborlineEngineRoom();

        await using var app = builder.Build();
        app.MapPost("/membrane/invoke", (CapabilityInvokeRequest _) => Results.Ok())
            .WithCapabilityCorrelationTracing();
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/membrane/invoke")
        {
            Content = JsonContent.Create(new
            {
                capability = "test.echo",
                core = JsonDocument.Parse("{}").RootElement,
                correlationId = traceIdAtMembrane,
                idempotencyKey = "idem-trace-2",
            }),
        };
        request.Headers.Add("x-capability-correlation-id", traceIdAtMembrane);

        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var span = Assert.Single(spans);
        Assert.Equal("capability.invoke", span.OperationName);
        Assert.Equal(traceIdAtMembrane, span.TraceId.ToString());
    }

    [Fact]
    public async Task BridgeRequiresEngineRoomTelemetryRegistration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        await using var app = builder.Build();
        app.MapPost("/membrane/invoke", (CapabilityInvokeRequest _) => Results.Ok())
            .WithCapabilityCorrelationTracing();
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsJsonAsync("/membrane/invoke", new
        {
            capability = "test.echo",
            core = JsonDocument.Parse("{}").RootElement,
            correlationId = "body-correlation-id",
            idempotencyKey = "idem-missing-engine-room",
        });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [SkippableFact(DisplayName =
        "TypescriptLoopbackTransportCorrelationIdAppearsInProductionNodeSpan: needs the " +
        "apps/capability-host node toolchain (npm ci)")]
    public async Task TypescriptLoopbackTransportCorrelationIdAppearsInProductionNodeSpan()
    {
        // Ticket 096: this row shells out to apps/capability-host/node_modules/.bin/vitest. Without
        // an `npm ci` there it used to die with a Win32Exception ("No such file or directory") and
        // read as a real regression. CI installs the toolchain (see the build-and-test job), so on
        // CI this runs; a machine that never installed it skips loudly by name instead of failing.
        var vitest = VitestBinaryPath();
        Skip.IfNot(
            File.Exists(vitest),
            $"apps/capability-host node toolchain absent: {vitest} does not exist. " +
            "Run `npm ci` in apps/capability-host to execute this test.");

        var spans = new List<Activity>();
        using var listener = ListenForEngineRoomSpans(spans);
        await using var outer = new ServiceCollection()
            .AddTestKernelClock()
            .AddLogging()
            .AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor())
            .AddSingleton(new NodeCallerSessionToken(null))
            .BuildServiceProvider();
        await using var app = new SharedHostedWebApp(
            outer,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            outer.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
            outer.GetRequiredService<TimeProvider>());
        await app.StartAsync(CancellationToken.None);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = vitest,
                WorkingDirectory = LocateCapabilityRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add(InteropTestPath);
        process.StartInfo.ArgumentList.Add("--reporter=verbose");
        process.StartInfo.Environment["HARBORLINE_DOTNET_RUNTIME_URL"] = app.SelectedUrl!;

        Assert.True(process.Start());
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.True(process.ExitCode == 0, $"TypeScript interop fixture failed.{Environment.NewLine}{stdout}{stderr}");
        const string marker = "CAPABILITY_HOST_MEMBRANE_TRACE_ID=";
        var traceIdAtMembrane = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Single(line => line.StartsWith(marker, StringComparison.Ordinal))[marker.Length..];
        var span = Assert.Single(spans, activity => activity.OperationName == "capability.invoke");
        Assert.Equal(traceIdAtMembrane, span.TraceId.ToString());

        await app.StopAsync(CancellationToken.None);
    }

    private static ActivityListener ListenForEngineRoomSpans(ICollection<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EngineRoomMetrics.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity => spans.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static string VitestBinaryPath() => Path.Combine(
        LocateCapabilityRoot(),
        "node_modules",
        ".bin",
        OperatingSystem.IsWindows() ? "vitest.cmd" : "vitest");

    private static string LocateCapabilityRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "capability-host");
            if (File.Exists(Path.Combine(candidate, "package.json")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"apps/capability-host not found walking up from {AppContext.BaseDirectory}.");
    }

    private sealed class NoTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active => null;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }
}
