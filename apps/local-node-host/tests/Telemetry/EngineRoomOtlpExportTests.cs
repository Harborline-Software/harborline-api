using Harborline.Api.Foundation.EngineRoom;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Harborline.Api.LocalNodeHost.Tests.Telemetry;

public sealed class EngineRoomOtlpExportTests
{
    [Fact]
    public async Task OtlpConsumerReceivesOneMetricAndOneSpan()
    {
        var metricReceived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spanReceived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var collectorBuilder = WebApplication.CreateBuilder();
        collectorBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var collector = collectorBuilder.Build();
        collector.MapPost("/v1/metrics", context =>
            ReceiveOtlpPayloadAsync(context, metricReceived));
        collector.MapPost("/v1/traces", context =>
            ReceiveOtlpPayloadAsync(context, spanReceived));
        await collector.StartAsync();

        var collectorUri = new Uri(collector.Urls.Single());
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("Sunfish.EngineRoom")
            .AddOtlpExporter(options => ConfigureExporter(options, collectorUri, "v1/metrics"))
            .Build();
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("Sunfish.EngineRoom")
            .AddOtlpExporter(options => ConfigureExporter(options, collectorUri, "v1/traces"))
            .Build();

        var services = new ServiceCollection();
        services.AddHarborlineEngineRoom();
        await using var provider = services.BuildServiceProvider();
        var telemetry = provider.GetRequiredService<EngineRoomTelemetry>();

        telemetry.RecordSubsystemStatus(
            EngineRoomSubsystem.MainPropulsion,
            SubsystemStatus.Operational);

        Assert.True(meterProvider.ForceFlush(5_000));
        Assert.True(tracerProvider.ForceFlush(5_000));
        var payloadLengths = await Task.WhenAll(metricReceived.Task, spanReceived.Task)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(payloadLengths, length => Assert.True(length > 0));
    }

    private static void ConfigureExporter(
        OtlpExporterOptions options,
        Uri collectorUri,
        string signalPath)
    {
        options.Endpoint = new Uri(collectorUri, signalPath);
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.ExportProcessorType = ExportProcessorType.Simple;
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
