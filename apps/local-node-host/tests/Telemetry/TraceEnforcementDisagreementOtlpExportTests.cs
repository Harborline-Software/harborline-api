using System.Net;
using System.Text;
using Harborline.Api.LocalNodeHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Harborline.Api.LocalNodeHost.Tests.Telemetry;

[Collection("Harborline process environment")]
public sealed class TraceEnforcementDisagreementOtlpExportTests
{
    [Fact]
    public async Task Configured_endpoint_exports_the_disagreement_counter_at_zero()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"t575-otlp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var seed = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
        var endpoint = Environment.GetEnvironmentVariable("LocalNode__Diagnostics__OtlpEndpoint");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var collector = builder.Build();
        collector.MapPost("/v1/metrics", async context =>
        {
            using var payload = new MemoryStream();
            await context.Request.Body.CopyToAsync(payload);
            if (payload.ToArray().AsSpan().IndexOf(Encoding.UTF8.GetBytes("authorization.trace.disagreements")) >= 0)
                received.TrySetResult();
            context.Response.ContentType = "application/x-protobuf";
        });
        await collector.StartAsync();
        try
        {
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", "3463463463463463463463463463463463463463463463463463463463463463");
            Environment.SetEnvironmentVariable("LocalNode__Diagnostics__OtlpEndpoint", collector.Urls.Single());
            var address = await LocalNodeHostRuntime.StartAsync("t575-composed-host-otlp", directory, CancellationToken.None);
            using var client = new HttpClient { BaseAddress = address };
            using var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", seed);
            Environment.SetEnvironmentVariable("LocalNode__Diagnostics__OtlpEndpoint", endpoint);
            Directory.Delete(directory, recursive: true);
        }
    }
}
