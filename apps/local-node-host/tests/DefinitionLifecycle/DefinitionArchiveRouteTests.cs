using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Definitions;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.DefinitionLifecycle;

public sealed class DefinitionArchiveRouteTests
{
    [Theory]
    [InlineData("forms", "formId", "Forms", "forms:author")]
    [InlineData("workflows", "key", "Workflows", "scheduling:author")]
    public async Task LiveRoutes_ArchiveUnarchiveAndFilter_KeepPayloadAndHistory(
        string family, string idProperty, string kind, string operation)
    {
        await using var host = await ArchiveRouteHost.CreateAsync();
        await host.Definitions.SeedAsync("same", ArchiveDefinitions.TenantA);
        await host.Definitions.SeedAsync("same", ArchiveDefinitions.TenantB);
        var route = $"/api/local-node/{family}/definitions";
        var before = await host.Client.GetStringAsync(route + "/same");
        var formHistory = await host.Client.GetStringAsync("/api/local-node/forms/definitions/same/versions");
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PostAsync(route + "/same/unarchive", null)).StatusCode);
        Assert.Empty(await host.Database.OutboxRowsAsync());
        for (var repeat = 0; repeat < 2; repeat++)
            Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PostAsJsonAsync(route + "/same/archive?tenant=forged", new
            {
                tenant = "forged", actor = "forged", kind = "Reports",
            })).StatusCode);
        Assert.Empty((await host.Client.GetFromJsonAsync<JsonElement>(route)).EnumerateArray());
        foreach (var optIn in new[] { "1", "true", "TRUE" })
        {
            var row = Assert.Single((await host.Client.GetFromJsonAsync<JsonElement>(route + "?includeArchived=" + optIn)).EnumerateArray());
            Assert.Equal("same", row.GetProperty(idProperty).GetString());
            Assert.Equal("1.0.0", row.GetProperty("version").GetString());
        }
        Assert.Equal(before, await host.Client.GetStringAsync(route + "/same"));
        Assert.Equal(formHistory, await host.Client.GetStringAsync("/api/local-node/forms/definitions/same/versions"));
        Assert.Single((await host.Client.GetFromJsonAsync<JsonElement>(family == "forms"
            ? "/api/local-node/workflows/definitions" : "/api/local-node/forms/definitions")).EnumerateArray());
        host.Active.Use(ArchiveDefinitions.TenantB);
        Assert.Single((await host.Client.GetFromJsonAsync<JsonElement>(route)).EnumerateArray());
        host.Active.Use(ArchiveDefinitions.TenantA);
        var audit = Assert.Single(await host.Database.OutboxRowsAsync());
        Assert.Equal("DefinitionArchived", audit.EventType);
        Assert.Equal("desktop-operator-under-test", audit.Actor);
        Assert.Equal("aaaaaaaa-0305-0000-0000-000000000001", audit.TenantId);
        Assert.Contains(operation, audit.Act);
        Assert.Equal(kind, JsonDocument.Parse(audit.BodyJson).RootElement.GetProperty("kind").GetString());
        for (var repeat = 0; repeat < 2; repeat++)
            Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PostAsync(route + "/same/unarchive", null)).StatusCode);
        Assert.Single((await host.Client.GetFromJsonAsync<JsonElement>(route)).EnumerateArray());
        Assert.Equal(2, (await host.Database.OutboxRowsAsync()).Count);
        Assert.Equal(before, await host.Client.GetStringAsync(route + "/same"));
        Assert.Equal(formHistory, await host.Client.GetStringAsync("/api/local-node/forms/definitions/same/versions"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Workflow_SelectedBWithAuthorityInBothTenants_MutatesAndAuditsOnlyB(bool archive)
    {
        var requests = new List<AuthorizationGateRequest>();
        var source = new TestRouteGate.ScopedGrantSource(request =>
        {
            requests.Add(request);
            return request.Principal.Value == "selected-holder"
                && request.Act.Operation.Value == "scheduling:author"
                && request.Tenant.Value is "aaaaaaaa-0305-0000-0000-000000000001"
                    or "bbbbbbbb-0305-0000-0000-000000000002";
        }, []);
        var gate = new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
        await using var host = await ArchiveRouteHost.CreateAsync(gate);
        await host.Definitions.SeedAsync("same", ArchiveDefinitions.TenantA);
        await host.Definitions.SeedAsync("same", ArchiveDefinitions.TenantB);
        if (!archive)
        {
            await using var seed = host.Database.Main.CreateDbContext();
            foreach (var tenant in new[] { "aaaaaaaa-0305-0000-0000-000000000001", "bbbbbbbb-0305-0000-0000-000000000002" })
                seed.Add(new DefinitionArchiveMark { TenantId = tenant, Kind = DefinitionKind.Workflows, DefinitionKey = "same" });
            await seed.SaveChangesAsync();
        }
        host.SelectB();
        Assert.Equal(Guid.Parse("aaaaaaaa-0305-0000-0000-000000000001"), host.Active.Active!.TeamId.Value);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/local-node/workflows/definitions/same/" + (archive ? "archive" : "unarchive"));
        request.Headers.Add("X-Correlation-ID", "03050000-0000-4000-8000-000000000001");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var decision = Assert.Single(requests);
        Assert.Equal("bbbbbbbb-0305-0000-0000-000000000002", decision.Tenant.Value);
        Assert.Equal("selected-holder", decision.Principal.Value);
        Assert.Equal("scheduling:author", decision.Act.Operation.Value);
        Assert.Equal(Guid.Parse("03050000-0000-4000-8000-000000000001"), decision.CorrelationId);
        await using var persisted = host.Database.Main.CreateDbContext();
        var mark = Assert.Single(await persisted.Set<DefinitionArchiveMark>().AsNoTracking().ToListAsync());
        Assert.Equal(archive ? "bbbbbbbb-0305-0000-0000-000000000002"
            : "aaaaaaaa-0305-0000-0000-000000000001", mark.TenantId);
        Assert.Equal(DefinitionKind.Workflows, mark.Kind);
        Assert.Equal("same", mark.DefinitionKey);
        var audit = Assert.Single(await host.Database.OutboxRowsAsync());
        Assert.Equal("bbbbbbbb-0305-0000-0000-000000000002", audit.TenantId);
        Assert.Equal("selected-holder", audit.Actor);
        Assert.Equal(archive ? "DefinitionArchived" : "DefinitionUnarchived", audit.EventType);
        Assert.Equal("same", audit.TargetId);
        using var auditBody = JsonDocument.Parse(audit.BodyJson);
        Assert.Equal("03050000-0000-4000-8000-000000000001", auditBody.RootElement.GetProperty("correlation_id").GetString());
    }

    [Theory]
    [InlineData("cookie", HttpStatusCode.Unauthorized)]
    [InlineData("bound-web", HttpStatusCode.Unauthorized)]
    [InlineData("invalid-correlation", HttpStatusCode.BadRequest)]
    [InlineData("empty-correlation", HttpStatusCode.BadRequest)]
    [InlineData("multiple-correlations", HttpStatusCode.BadRequest)]
    public async Task UnresolvedIdentityOrInvalidCorrelation_RefusesBeforeAnyDecisionOrRead(string scenario, HttpStatusCode expected)
    {
        var requests = new List<AuthorizationGateRequest>();
        var source = new TestRouteGate.ScopedGrantSource(request => { requests.Add(request); return true; }, []);
        await using var host = await ArchiveRouteHost.CreateAsync(
            new AuthorizationGate(source, new EmptyRecordStandingResolver(), source), forbidDefinitionReads: true);
        host.Reads.Forbid = true;
        host.BoundWebPrincipal = scenario == "bound-web";
        if (scenario.Contains("correlation", StringComparison.Ordinal)) host.SelectB();
        foreach (var family in new[] { "forms", "workflows" })
        foreach (var command in new[] { "archive", "unarchive" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/local-node/{family}/definitions/secret/{command}");
            if (scenario == "cookie") request.Headers.Add("Cookie", "__Host-hl-selected=unresolved");
            if (scenario == "invalid-correlation") request.Headers.Add("X-Correlation-ID", "not-a-guid");
            if (scenario == "empty-correlation") request.Headers.Add("X-Correlation-ID", "00000000-0000-0000-0000-000000000000");
            if (scenario == "multiple-correlations")
                Assert.True(request.Headers.TryAddWithoutValidation("X-Correlation-ID", new[]
                {
                    "03050000-0000-4000-8000-000000000001", "03050000-0000-4000-8000-000000000002",
                }));
            using var response = await host.Client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.BadRequest)
                Assert.Equal("request.correlation_invalid", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Empty(requests);
        Assert.Empty(host.Stages);
        Assert.Empty(host.Definitions.Reader!.ReceivedCalls());
        Assert.Equal(0, host.Reads.Count);
        Assert.Empty(await host.Database.OutboxRowsAsync());
    }

    [Fact]
    public async Task Forms_DraftOnlyAndNewerDraft_KeepExistingProjectionAndPublication()
    {
        await using var host = await ArchiveRouteHost.CreateAsync();
        await host.Definitions.SeedAsync("published", ArchiveDefinitions.TenantA, newerDraft: true);
        await host.Definitions.SeedAsync("draft-only", ArchiveDefinitions.TenantA, draftOnly: true);
        const string route = "/api/local-node/forms/definitions";
        var before = await host.Client.GetStringAsync(route + "?includeDrafts=1");
        foreach (var id in new[] { "published", "draft-only" })
            Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PostAsync(route + "/" + id + "/archive", null)).StatusCode);
        Assert.Empty((await host.Client.GetFromJsonAsync<JsonElement>(route + "?includeDrafts=1")).EnumerateArray());
        Assert.Equal(before, await host.Client.GetStringAsync(route + "?includeDrafts=1&includeArchived=1"));
        var published = Assert.Single((await host.Client.GetFromJsonAsync<JsonElement>(route + "?includeArchived=true")).EnumerateArray());
        Assert.Equal("published", published.GetProperty("formId").GetString());
        Assert.Equal("Published", published.GetProperty("status").GetString());
        Assert.Equal("1.0.0", published.GetProperty("version").GetString());
        Assert.Equal("1.0.1", published.GetProperty("latestDraftVersion").GetString());
        var draft = await host.Client.GetFromJsonAsync<JsonElement>(route + "/draft-only");
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Equal("1.0.0", draft.GetProperty("version").GetString());
        Assert.NotNull(await host.Definitions.Forms.GetCurrentPublishedAsync(new(ArchiveDefinitions.TenantA, "published")));
        Assert.Null(await host.Definitions.Forms.GetCurrentPublishedAsync(new(ArchiveDefinitions.TenantA, "draft-only")));
    }

    [Theory]
    [InlineData("forms", "form_definition.not_found")]
    [InlineData("workflows", "No published workflow definition 'missing' for this tenant.")]
    public async Task MissingAndOtherTenant_StopAfterBind_WithoutStateOrAudit(string family, string expected)
    {
        await using var host = await ArchiveRouteHost.CreateAsync();
        await host.Definitions.SeedAsync("missing", ArchiveDefinitions.TenantB);
        foreach (var command in new[] { "archive", "unarchive" })
        {
            host.Stages.Clear();
            var response = await host.Client.PostAsync($"/api/local-node/{family}/definitions/missing/{command}", null);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(expected, error.GetProperty(family == "forms" ? "code" : "error").GetString());
            Assert.Equal(new[] { "Authorize", "Bind" }, host.Stages.Select(stage => stage.ToString()));
        }
        Assert.Equal(0, host.Reads.Count);
        Assert.Empty(await host.Database.OutboxRowsAsync());
    }

    [Theory]
    [InlineData("forms", "forms:author")]
    [InlineData("workflows", "scheduling:author")]
    public async Task DeniedCommands_DoNotReadDefinitionsOrArchive_AndUseCanonicalRefusal(string family, string permission)
    {
        var gate = TestRouteGate.Following(operation => operation != permission);
        await using var host = await ArchiveRouteHost.CreateAsync(gate, forbidDefinitionReads: true);
        host.Reads.Forbid = true;
        foreach (var command in new[] { "archive", "unarchive" })
        {
            host.Stages.Clear();
            var response = await host.Client.PostAsync($"/api/local-node/{family}/definitions/secret/{command}", null);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains(permission, await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "Authorize" }, host.Stages.Select(stage => stage.ToString()));
        }
        Assert.Empty(host.Definitions.Reader!.ReceivedCalls());
        Assert.Equal(0, host.Reads.Count);
        Assert.Empty(await host.Database.OutboxRowsAsync());
    }

    [Theory]
    [InlineData("forms", "forms", "forms:author")]
    [InlineData("workflows", "scheduling", "scheduling:author")]
    public async Task RouteTargets_EncodeOnce_AndRetainServerActorAndTenant(string family, string recordKind, string permission)
    {
        var requests = new List<AuthorizationGateRequest>();
        var source = new TestRouteGate.ScopedGrantSource(request => { requests.Add(request); return true; }, []);
        var gate = new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
        await using var host = await ArchiveRouteHost.CreateAsync(gate);
        await host.Definitions.SeedAsync("orders % café", ArchiveDefinitions.TenantA);
        var response = await host.Client.PostAsync($"/api/local-node/{family}/definitions/orders%20%25%20caf%C3%A9/archive", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var request = Assert.Single(requests);
        Assert.Equal(permission, request.Act.Operation.Value);
        Assert.Equal(recordKind, request.Target.RecordKind);
        Assert.Equal("orders%20%25%20caf%C3%A9", request.Target.RecordId);
        Assert.Equal("aaaaaaaa-0305-0000-0000-000000000001", request.Tenant.Value);
        Assert.Equal("desktop-operator-under-test", request.Principal.Value);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), request.At);
        Assert.Equal(new[] { "Authorize", "Bind", "Mutate", "Validate", "Commit", "React" },
            host.Stages.Select(stage => stage.ToString()));
        var row = Assert.Single(await host.Database.OutboxRowsAsync());
        Assert.Equal("orders%20%25%20caf%C3%A9", row.TargetId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WrongTenantKindOrIdentity_CannotUseAScopedGrant_AndSlashIsEncodedOnce(bool form)
    {
        using var database = new DefinitionArchiveDatabase();
        using var definitions = new ArchiveDefinitions();
        const string id = "orders/a % café";
        await definitions.SeedAsync(id, ArchiveDefinitions.TenantA);
        await definitions.SeedAsync(id, ArchiveDefinitions.TenantB);
        var source = new TestRouteGate.ScopedGrantSource(request =>
            request.Tenant.Value == "aaaaaaaa-0305-0000-0000-000000000001"
            && request.Act.Operation.Value == (form ? "forms:author" : "scheduling:author")
            && request.Target.RecordKind == (form ? "forms" : "scheduling"),
            [ScopeExpression.Parse("/records/orders%2Fa%20%25%20caf%C3%A9")]);
        var gate = new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
        var reads = new ArchiveReadCounter(database.Main) { Forbid = true };
        using var writer = new NodeDefinitionArchiveWriter(reads, gate, database.Outbox);
        foreach (var (tenant, key, useForm) in new[]
        {
            (ArchiveDefinitions.TenantB, id, form), (ArchiveDefinitions.TenantA, "other", form),
            (ArchiveDefinitions.TenantA, id, !form),
        })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            {
                if (useForm) await writer.SetFormArchivedAsync(definitions.Forms, key, true, ArchiveDefinitions.Authority(tenant));
                else await writer.SetWorkflowArchivedAsync(definitions.Workflows, key, true, ArchiveDefinitions.Authority(tenant));
            });
        Assert.Equal(0, reads.Count);
        reads.Forbid = false;
        if (form) await writer.SetFormArchivedAsync(definitions.Forms, id, true, ArchiveDefinitions.Authority());
        else await writer.SetWorkflowArchivedAsync(definitions.Workflows, id, true, ArchiveDefinitions.Authority());
        Assert.Equal("orders%2Fa%20%25%20caf%C3%A9", Assert.Single(await database.OutboxRowsAsync()).TargetId);
    }

    [Fact]
    public async Task ListPolicy_IsUnchanged_AndIncludeArchivedSkipsArchiveReads()
    {
        await using var host = await ArchiveRouteHost.CreateAsync(TestRouteGate.Denying());
        await host.Definitions.SeedAsync("same", ArchiveDefinitions.TenantA);
        foreach (var family in new[] { "forms", "workflows" })
        {
            Assert.Single((await host.Client.GetFromJsonAsync<JsonElement>($"/api/local-node/{family}/definitions")).EnumerateArray());
            host.Reads.Forbid = true;
            Assert.Single((await host.Client.GetFromJsonAsync<JsonElement>($"/api/local-node/{family}/definitions?includeArchived=1")).EnumerateArray());
            host.Reads.Forbid = false;
        }
    }
}

internal sealed class ArchiveDefinitions : IDisposable
{
    internal static readonly TenantId TenantA = new("aaaaaaaa-0305-0000-0000-000000000001");
    internal static readonly TenantId TenantB = new("bbbbbbbb-0305-0000-0000-000000000002");
    internal static readonly DateTimeOffset At = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    internal AuthorizedFormDefinitionLifecycle Forms { get; }
    internal AuthorizedWorkflowDefinitionLifecycle Workflows { get; }
    internal IEntityStore? Reader { get; }

    internal ArchiveDefinitions(bool forbidReads = false)
    {
        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        Reader = forbidReads ? Substitute.For<IEntityStore>() : null;
        var reader = Reader ?? entities;
        var gate = TestRouteGate.AllowAll();
        Forms = new AuthorizedFormDefinitionLifecycle(new EntityStoreFormDefinitionStore(reader, entities, TimeProvider.System),
            entities, TimeProvider.System, gate, TestAuthorization.RoleGate(), legalHold: null);
        var admission = new WorkflowAdmissionValidator();
        Workflows = new AuthorizedWorkflowDefinitionLifecycle(new EntityStoreWorkflowDefinitionStore(reader, entities, admission, TimeProvider.System),
            entities, admission, TimeProvider.System, gate, TestAuthorization.RoleGate());
    }

    internal static AuthorizationWriteContext Authority(TenantId? tenant = null) =>
        new(new ActorId("archive-actor"), tenant ?? TenantA, AdmittedInstant.FromRecordedAct(At));

    internal async Task SeedAsync(string id, TenantId tenant, bool draftOnly = false, bool newerDraft = false)
    {
        FormDefinition Form(int patch) => new(
            new DefinitionEnvelope<FormDefinitionId, SemanticVersion, TenantId, FormDefinitionProvenance>(
                new FormDefinitionId(id), new SemanticVersion(1, 0, patch), tenant, CascadeLayer.Tenant,
                new FormDefinitionProvenance(IdentityRef.System, null), [], Contract: null),
            FormDefinitionStatus.Draft, new SchemaId("schema"), new HarborlineOverlay(new Dictionary<string, FieldOverlay>(), [], []), At, At);
        await Forms.RegisterAsync(Form(0), Authority(tenant));
        if (!draftOnly) await Forms.PublishAsync(new(tenant, id, "1.0.0"), Authority(tenant));
        if (newerDraft) await Forms.RegisterAsync(Form(1), Authority(tenant));
        var authored = JsonSerializer.SerializeToElement(new
        {
            key = id, tenant = tenant.Value, version = "1.0.0", initialState = "start",
            states = new[] { new { id = "start", kind = "Normal" }, new { id = "done", kind = "Terminal" } },
            triggers = new[] { new { id = "submit", kind = "HumanAction", task = "submit" } },
            transitions = new[] { new { id = "submit", from = "start", on = "submit", to = "done" } },
            actions = Array.Empty<object>(), provenance = "Tenant", owner = new { scheme = "actor", value = "archive-actor" },
        });
        await Workflows.RegisterAndPublishAsync(authored, await Workflows.DecideAsync(id, Authority(tenant)));
    }

    public void Dispose() => Forms.Dispose();
}

internal sealed class ArchiveReadCounter(IDbContextFactory<LocalNodeDbContext> inner) : IDbContextFactory<LocalNodeDbContext>
{
    internal int Count { get; private set; }
    internal bool Forbid { get; set; }
    public LocalNodeDbContext CreateDbContext()
    {
        Count++;
        if (Forbid) throw new InvalidOperationException("Archive storage was read before admission or during includeArchived.");
        return inner.CreateDbContext();
    }
}

internal sealed class ArchiveRouteHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    internal HttpClient Client { get; }
    internal DefinitionArchiveDatabase Database { get; }
    internal ArchiveDefinitions Definitions { get; }
    internal ArchiveActiveTeam Active { get; }
    internal ArchiveReadCounter Reads { get; }
    internal List<WritePipelineStage> Stages { get; } = [];
    private NodeDefinitionArchiveWriter _writer = null!;
    private SelectedSessionRequestPrincipal? _selected;
    internal bool BoundWebPrincipal { get; set; }

    internal void SelectB() => _selected = new SelectedSessionRequestPrincipal("selected-account", ArchiveDefinitions.TenantB,
        new PrincipalUserId("selected-holder"), new CanonicalPartyReference("selected-party"),
        "membership", 1, [new PinnedGrantOwnerVersion("fixture", 1)], 1, "session", "coordination");

    private ArchiveRouteHost(WebApplication app, DefinitionArchiveDatabase database, ArchiveDefinitions definitions, ArchiveActiveTeam active)
    {
        _app = app; Database = database; Definitions = definitions; Active = active;
        Reads = new ArchiveReadCounter(database.Main);
        Client = new HttpClient();
    }

    internal static async Task<ArchiveRouteHost> CreateAsync(AuthorizationGate? gate = null, bool forbidDefinitionReads = false)
    {
        gate ??= TestRouteGate.AllowAll();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTestDesktopOperator();
        var clock = new ArchiveClock();
        builder.Services.AddFrozenKernelClock(clock);
        builder.Services.AddSingleton(gate);
        var app = builder.Build();
        var host = new ArchiveRouteHost(app, new DefinitionArchiveDatabase(), new ArchiveDefinitions(forbidDefinitionReads), new ArchiveActiveTeam());
        host._writer = new NodeDefinitionArchiveWriter(host.Reads, gate, host.Database.Outbox, new StageObserver(host.Stages));
        app.Use(async (http, next) =>
        {
            if (host._selected is not null) http.Features.Set(host._selected);
            else http.Features.Set(DesktopPlaneRequestFeature.Instance);
            using IDisposable? caller = host.BoundWebPrincipal
                ? NodeCallerAttributionScope.Enter(new NodeCallerAttribution("bound-party", "membership", 1, "session", "coordination", 1))
                : host._selected is not null ? NodeCallerAttributionScope.Enter(NodeCallerAttribution.From(host._selected)) : null;
            await next(http);
        });
        FormDefinitionRoutes.Map(app, host.Definitions.Forms, new InMemorySchemaRegistry(clock), host.Active, clock, host._writer);
        WorkflowDefinitionRoutes.Map(app.MapDeviceReachableProductDataGroup(), host.Definitions.Workflows, host.Active, clock, host._writer);
        await app.StartAsync();
        host.Client.BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _writer.Dispose();
        Definitions.Dispose();
        Active.Dispose();
        Database.Dispose();
    }

    private sealed class StageObserver(List<WritePipelineStage> stages) : IWritePipelineObserver
    {
        public void OnStage(WritePipelineStage stage) => stages.Add(stage);
    }

    private sealed class ArchiveClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ArchiveDefinitions.At;
    }
}

internal sealed class ArchiveActiveTeam : IActiveTeamAccessor, IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection().BuildServiceProvider();
    public TeamContext? Active { get; private set; }
    public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
    internal ArchiveActiveTeam() => Use(ArchiveDefinitions.TenantA);
    internal void Use(TenantId tenant)
    {
        Active = new TeamContext(new TeamId(Guid.Parse(tenant.Value)), "Archive test", _provider, TimeProvider.System);
        ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, Active));
    }
    public Task SetActiveAsync(TeamId teamId, CancellationToken ct) { Use(new TenantId(teamId.Value.ToString())); return Task.CompletedTask; }
    public void Dispose() => _provider.Dispose();
}
