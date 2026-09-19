using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>T-642: real listener pipeline, workflow authoring route, and durable store composition.</summary>
public sealed class KernelRefusalRouteTests : IAsyncLifetime
{
    private const string Key = "windowed-workflow";
    private const string Path = WorkflowDefinitionRoutes.RouteBase + "/" + Key;
    private const string Token = "t642-test-caller-token";
    private static readonly DateTimeOffset OpensAt = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ClosesAt = OpensAt.AddHours(1);
    private readonly ManualClock _clock = new(OpensAt);
    private WebApplication _app = null!;
    private SharedHostedWebApp _listener = null!;
    private HttpClient _client = null!;
    private int _dispatchedWrites;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddFrozenKernelClock(_clock);
        builder.Services.AddSingleton(new NodeCallerSessionToken(Token));
        var activeTeam = new ActiveTeam(new TeamContext(
            new TeamId(Guid.Parse("aaaa0642-0000-0000-0000-000000000001")),
            "T-642", new ServiceCollection().BuildServiceProvider(), _clock));
        builder.Services.AddSingleton<IActiveTeamAccessor>(activeTeam);
        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), _clock);
        builder.Services.AddSingleton<IEntityStore>(new InMemoryEntityStoreReader(entities));
        builder.Services.AddSingleton<ICapabilityAuthorityRegistry>(CapabilityAuthorityRegistry.Canonical);
        builder.Services.AddSingleton<IWorkflowAdmissionValidator, WorkflowAdmissionValidator>();
        builder.Services.AddTestAuthorizationGate();
        builder.Services.AddEntityStoreWorkflowDefinitionStore(_ => entities);
        _app = builder.Build();
        _listener = new SharedHostedWebApp(
            _app, Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            _app.Services.GetRequiredService<ILogger<SharedHostedWebApp>>(), _clock);
        _listener.MapApiRoutes(routes =>
        {
            routes.Use(async (http, next) =>
            {
                if (HttpMethods.IsPut(http.Request.Method))
                    Interlocked.Increment(ref _dispatchedWrites);
                await next(http);
            });
            WorkflowDefinitionRoutes.Map(
                routes.MapDeviceReachableProductDataGroup(),
                _app.Services.GetRequiredService<AuthorizedWorkflowDefinitionLifecycle>(), activeTeam, _clock);
        });
        await _listener.StartAsync(CancellationToken.None);
        await _app.StartAsync();
        _listener.CaptureSelectedUrl();
        _client = new HttpClient { BaseAddress = new Uri(_listener.SelectedUrl!) };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _listener.StopAsync(CancellationToken.None);
        await _app.StopAsync();
        await _listener.DisposeAsync();
        await _app.DisposeAsync();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Multi_command_body_returns_400_before_dispatch(int count)
    {
        using var response = await _client.PutAsJsonAsync(Path,
            new { commands = Enumerable.Range(0, count).Select(_ => Body()).ToArray() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var refusal = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("kernel.multi-command-batch", refusal.GetProperty("code").GetString());
        Assert.Equal(count, refusal.GetProperty("commandCount").GetInt32());
        Assert.Equal(0, _dispatchedWrites);
        using var stored = await _client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.NotFound, stored.StatusCode);
    }

    [Fact]
    public async Task Single_command_body_dispatches_once_and_persists()
    {
        using var response = await _client.PutAsJsonAsync(Path, new { commands = new[] { Body() } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _dispatchedWrites);
        using var stored = await _client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
    }

    [Fact]
    public async Task Empty_command_body_does_not_dispatch()
    {
        using var response = await _client.PutAsJsonAsync(Path, new { commands = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, _dispatchedWrites);
    }

    [Fact]
    public async Task Caller_authentication_precedes_command_admission()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        using var response = await _client.PutAsJsonAsync(Path, new { commands = new[] { Body(), Body() } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _dispatchedWrites);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(60, false)]
    [InlineData(61, false)]
    public async Task Definition_write_observes_declared_window(int minutesAfterOpening, bool admitted)
    {
        _clock.UtcNow = OpensAt.AddMinutes(minutesAfterOpening);
        using var response = await _client.PutAsJsonAsync(Path, Body(withWindow: true));

        Assert.Equal(admitted ? HttpStatusCode.OK : HttpStatusCode.UnprocessableEntity, response.StatusCode);
        if (!admitted)
            await AssertWindowRefusalAsync(response);
        using var stored = await _client.GetAsync(Path);
        Assert.Equal(admitted ? HttpStatusCode.OK : HttpStatusCode.NotFound, stored.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stored_window_cannot_be_bypassed_by_replacement(bool widen)
    {
        using var first = await _client.PutAsJsonAsync(Path, Body(withWindow: true));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        _clock.UtcNow = ClosesAt;
        var replacement = Body(withWindow: widen);
        if (widen)
            replacement["contractWindow"]!["closesAt"] = ClosesAt.AddDays(1);

        using var response = await _client.PutAsJsonAsync(Path, replacement);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertWindowRefusalAsync(response);
        var stored = await _client.GetFromJsonAsync<JsonElement>(Path);
        Assert.Equal("1.0.0", stored.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Closed_window_refuses_before_member_interpretation()
    {
        _clock.UtcNow = OpensAt.AddMinutes(-1);
        var body = Body(withWindow: true);
        body["states"] = "not a state collection";
        using var response = await _client.PutAsJsonAsync(Path, body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertWindowRefusalAsync(response);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"opensAt\":\"invalid\",\"closesAt\":\"invalid\"}")]
    [InlineData("{\"opensAt\":\"2026-09-19T13:00:00Z\",\"closesAt\":\"2026-09-19T12:00:00Z\"}")]
    public async Task Malformed_window_returns_400_without_persisting(string window)
    {
        var body = Body();
        body["contractWindow"] = JsonNode.Parse(window);
        using var response = await _client.PutAsJsonAsync(Path, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var stored = await _client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.NotFound, stored.StatusCode);
    }

    private async Task AssertWindowRefusalAsync(HttpResponseMessage response)
    {
        var refusal = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("kernel.definition-contract-window", refusal.GetProperty("code").GetString());
        Assert.Equal(Key, refusal.GetProperty("definitionId").GetString());
        Assert.Equal(OpensAt, refusal.GetProperty("opensAt").GetDateTimeOffset());
        Assert.Equal(ClosesAt, refusal.GetProperty("closesAt").GetDateTimeOffset());
        Assert.Equal(_clock.UtcNow, refusal.GetProperty("observedAt").GetDateTimeOffset());
    }

    private static JsonObject Body(bool withWindow = false)
    {
        var body = JsonNode.Parse("""
            { "title": { "defaultLocale": "en", "values": { "en": "Windowed workflow" } },
              "mutability": "Locked", "initialState": "Draft",
              "states": [{ "id": "Draft", "kind": "Normal" }, { "id": "Done", "kind": "Terminal" }],
              "triggers": [{ "id": "finish", "kind": "HumanAction", "task": "finish" }],
              "transitions": [{ "id": "finish", "from": "Draft", "on": "finish", "to": "Done" }],
              "actions": [], "guards": [] }
            """)!.AsObject();
        if (withWindow)
            body["contractWindow"] = new JsonObject { ["opensAt"] = OpensAt, ["closesAt"] = ClosesAt };
        return body;
    }

    private sealed class ManualClock(DateTimeOffset utcNow) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class ActiveTeam(TeamContext team) : IActiveTeamAccessor
    {
        public TeamContext? Active => team;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }
}
