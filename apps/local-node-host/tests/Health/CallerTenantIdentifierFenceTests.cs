using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

public sealed class CallerTenantIdentifierFenceTests
{
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task Every_registered_route_refuses_caller_supplied_tenant_ids()
    {
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddLogging();
        services.AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor());
        services.AddSingleton(new NodeCallerSessionToken(null));
        await using var provider = services.BuildServiceProvider();
        var registry = new LocalNodeExecutableEndpointRegistry();
        await using var app = new SharedHostedWebApp(
            provider,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            registry,
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
            provider.GetRequiredService<TimeProvider>());
        app.MapApiRoutes(routes =>
        {
            routes.MapGet("/api/local-node/new-reader", () => Results.Ok());
            routes.MapPost("/api/local-node/new-writer", () => Results.Ok());
            routes.MapGet("/api/local-node/by-tenant/1/{tenantId}", () => Results.Ok());
            routes.MapGet("/api/local-node/by-tenant/2/{tenant_id}", () => Results.Ok());
            routes.MapGet("/api/local-node/by-tenant/3/{tenant-id}", () => Results.Ok());
            routes.MapGet("/api/local-node/by-tenant/4/{TENANTID}", () => Results.Ok());
            routes.MapPost("/api/session/select", () => Results.Ok());
            routes.MapPost("/api/session/switch", () => Results.Ok());
            routes.MapDeviceReachableProductDataGroup()
                .MapGet("/api/local-node/journal-entries", async (HttpRequest request) =>
                {
                    using var reader = new StreamReader(request.Body);
                    var body = await reader.ReadToEndAsync();
                    return Results.Text(body.Length == 0 ? "reached" : body);
                });
        });
        await app.StartAsync(CancellationToken.None);
        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        try
        {
            Assert.Equal("reached",
                await client.GetStringAsync("/api/local-node/journal-entries"));
            var pairs = registry.Current.Endpoints
                .SelectMany(endpoint => endpoint.HttpMethods.Select(method =>
                    (method, path: Regex.Replace(endpoint.RoutePattern, @"\{[^}]+\}", "other-team"))))
                .ToArray();
            Assert.True(pairs.Length >= 8);
            foreach (var (method, path) in pairs)
            {
                foreach (var spelling in new[]
                    { "tenantId", "tenant_id", "tenant-id", "TENANTID", "tenant", "tenantIdentifier", "X-Tenant", "X-Tenant-Id", "X-Tenant-Identifier" })
                {
                    using var query = new HttpRequestMessage(new HttpMethod(method), $"{path}?{spelling}=other-team");
                    await AssertBadTenantAsync(client, query);
                    using var header = new HttpRequestMessage(new HttpMethod(method), path);
                    header.Headers.TryAddWithoutValidation(spelling, "other-team");
                    await AssertBadTenantAsync(client, header);
                    if (spelling.Equals("tenantId", StringComparison.OrdinalIgnoreCase)
                        && path is "/api/session/select" or "/api/session/switch")
                        continue;
                    using var body = new HttpRequestMessage(new HttpMethod(method), path)
                    {
                        Content = JsonContent.Create(new Dictionary<string, string> { [spelling] = "other-team" }),
                    };
                    await AssertBadTenantAsync(client, body);
                }
            }
            foreach (var index in Enumerable.Range(1, 4))
            {
                using var route = new HttpRequestMessage(HttpMethod.Get, $"/api/local-node/by-tenant/{index}/other-team");
                await AssertBadTenantAsync(client, route);
            }
            foreach (var path in new[] { "/api/session/select", "/api/session/switch" })
            {
                using var choice = new HttpRequestMessage(HttpMethod.Post, path)
                    { Content = JsonContent.Create(new { tenantId = "authorized-choice" }) };
                using var response = await client.SendAsync(choice);
                Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
                using var nestedChoice = new HttpRequestMessage(HttpMethod.Post, path)
                    { Content = JsonContent.Create(new { tenantId = "authorized-choice", payload = new { tenantId = "injected" } }) };
                await AssertBadTenantAsync(client, nestedChoice);
            }

            using (var nested = new HttpRequestMessage(HttpMethod.Get, "/api/local-node/journal-entries")
                { Content = JsonContent.Create(new { ordinary = "ok", values = new object[] { new { ordinary = "ok" }, new { tenantId = "other-team" } } }) })
                await AssertBadTenantAsync(client, nested);
            using (var form = new HttpRequestMessage(HttpMethod.Get, "/api/local-node/journal-entries")
                { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["ordinary"] = "ok", ["tenant_id"] = "other-team" }) })
                await AssertBadTenantAsync(client, form);
            using (var multipart = new MultipartFormDataContent())
            {
                multipart.Add(new ByteArrayContent([1]), "tenantId", "upload.txt");
                using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/local-node/new-writer")
                    { Content = multipart };
                await AssertBadTenantAsync(client, upload);
            }
            using (var getChoice = new HttpRequestMessage(HttpMethod.Get, "/api/session/select")
                { Content = JsonContent.Create(new { tenantId = "authorized-choice" }) })
                await AssertBadTenantAsync(client, getChoice);

            using (var cleanForm = new HttpRequestMessage(HttpMethod.Get, "/api/local-node/journal-entries")
                { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["ordinary"] = "ok" }) })
                Assert.Equal("ordinary=ok", await (await client.SendAsync(cleanForm)).Content.ReadAsStringAsync());
            using (var clean = new HttpRequestMessage(HttpMethod.Get, "/api/local-node/journal-entries")
                { Content = JsonContent.Create(new { ordinary = "ok" }) })
                Assert.Contains("ordinary", await (await client.SendAsync(clean)).Content.ReadAsStringAsync());
            using (var scalar = new HttpRequestMessage(HttpMethod.Get, "/api/local-node/journal-entries")
                { Content = JsonContent.Create("hello") })
                Assert.Equal("\"hello\"", await (await client.SendAsync(scalar)).Content.ReadAsStringAsync());
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("multipart/form-data", "x")]
    [InlineData("application/json", "{")]
    public async Task Malformed_body_is_400_before_route_L985(string mediaType, string body)
    {
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddLogging();
        services.AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor());
        services.AddSingleton(new NodeCallerSessionToken(null));
        await using var provider = services.BuildServiceProvider();
        await using var app = new SharedHostedWebApp(
            provider,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
            provider.GetRequiredService<TimeProvider>());
        app.MapApiRoutes(routes => routes.MapDeviceReachableProductDataGroup()
            .MapGet("/api/local-node/journal-entries", () => Results.Ok()));
        await app.StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/local-node/journal-entries")
                { Content = new StringContent(body, Encoding.UTF8, mediaType) };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static async Task AssertBadTenantAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("request.tenant-id-not-accepted", await response.Content.ReadAsStringAsync());
    }

    private sealed class NoTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active => null;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }
}
