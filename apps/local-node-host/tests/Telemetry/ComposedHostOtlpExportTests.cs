using System.Net;

using Harborline.Api.Foundation.EngineRoom;
using Harborline.Api.LocalNodeHost;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Telemetry;

[Collection("Harborline process environment")]
public sealed class ComposedHostOtlpExportTests
{
    private const string RootSeedHex =
        "3463463463463463463463463463463463463463463463463463463463463463";

    [Fact]
    public async Task Configured_endpoint_exports_one_engine_room_metric_and_span()
    {
        var dataDirectory = CreateDataDirectory();
        var previousRootSeedHex = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        var previousOtlpEndpoint = Environment.GetEnvironmentVariable("LocalNode__Diagnostics__OtlpEndpoint");
        var metricReceived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spanReceived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var collectorBuilder = WebApplication.CreateBuilder();
        collectorBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var collector = collectorBuilder.Build();
        collector.MapPost("/v1/metrics", context => ReceiveOtlpPayloadAsync(context, metricReceived));
        collector.MapPost("/v1/traces", context => ReceiveOtlpPayloadAsync(context, spanReceived));
        await collector.StartAsync();

        try
        {
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", RootSeedHex);
            Environment.SetEnvironmentVariable(
                "LocalNode__Diagnostics__OtlpEndpoint",
                collector.Urls.Single());

            var baseAddress = await LocalNodeHostRuntime.StartAsync(
                "t448-composed-host-otlp",
                dataDirectory,
                CancellationToken.None);
            using var client = new HttpClient { BaseAddress = baseAddress };
            using var health = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            var services = Assert.IsAssignableFrom<IServiceProvider>(LocalNodeHostRuntime.CurrentServices);
            services.GetRequiredService<EngineRoomTelemetry>().RecordSubsystemStatus(
                EngineRoomSubsystem.MainPropulsion,
                SubsystemStatus.Operational);

            var payloadLengths = await Task.WhenAll(metricReceived.Task, spanReceived.Task)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.All(payloadLengths, length => Assert.True(length > 0));
        }
        finally
        {
            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", previousRootSeedHex);
            Environment.SetEnvironmentVariable("LocalNode__Diagnostics__OtlpEndpoint", previousOtlpEndpoint);
            DeleteDataDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Default_boot_starts_without_exporting_telemetry()
    {
        var dataDirectory = CreateDataDirectory();
        var previousRootSeedHex = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        var previousOtlpEndpoint = Environment.GetEnvironmentVariable("LocalNode__Diagnostics__OtlpEndpoint");
        var metricReceived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spanReceived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var collectorBuilder = WebApplication.CreateBuilder();
        collectorBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var collector = collectorBuilder.Build();
        collector.MapPost("/v1/metrics", context => ReceiveOtlpPayloadAsync(context, metricReceived));
        collector.MapPost("/v1/traces", context => ReceiveOtlpPayloadAsync(context, spanReceived));
        await collector.StartAsync();

        try
        {
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", RootSeedHex);
            Environment.SetEnvironmentVariable("LocalNode__Diagnostics__OtlpEndpoint", null);

            var baseAddress = await LocalNodeHostRuntime.StartAsync(
                "t448-composed-host-default",
                dataDirectory,
                CancellationToken.None);
            using var client = new HttpClient { BaseAddress = baseAddress };
            using var health = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(metricReceived.Task.IsCompleted);
            Assert.False(spanReceived.Task.IsCompleted);
        }
        finally
        {
            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", previousRootSeedHex);
            Environment.SetEnvironmentVariable("LocalNode__Diagnostics__OtlpEndpoint", previousOtlpEndpoint);
            DeleteDataDirectory(dataDirectory);
        }
    }

    private static string CreateDataDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"t448-otlp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDataDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static async Task ReceiveOtlpPayloadAsync(
        HttpContext context,
        TaskCompletionSource<int> payloadReceived)
    {
        using var payload = new MemoryStream();
        await context.Request.Body.CopyToAsync(payload);
        payloadReceived.TrySetResult(checked((int)payload.Length));
        context.Response.ContentType = "application/x-protobuf";
    }
}
