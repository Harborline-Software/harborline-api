using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// WF-KEY (W-7 / G5) — route-level end-to-end tests for the workflow-DEFINITION authoring surface.
/// Hosts the SAME production route handlers <see cref="HostedWorkflowDefinitionApiEndpoint"/> registers
/// (<see cref="WorkflowDefinitionRoutes.Map"/> — the single source of truth, no test/prod wire drift)
/// over a real in-process Kestrel listener, driven with a real <see cref="HttpClient"/>. Mirrors
/// <c>FormDefinitionRouteTests</c>.
/// </summary>
/// <remarks>
/// Proves the AUTHOR → ADMIT → PERSIST → RELOAD round-trip the Harborline App workflow builder rides on: a PUT
/// of an admissible definition persists + publishes it (tenant-scoped, server-side); a GET reloads it
/// intact (including state labels + title); the list surfaces it; a re-save mints the next version;
/// cross-tenant isolation holds. And the fail-closed admission gate fires ON THE WIRED PUT PATH — an
/// unclassified action, and a CP action reachable from an autonomous trigger, are each REJECTED with 422
/// carrying the stable <see cref="WorkflowAdmissionCodes"/> code, and nothing is published. Uses the
/// in-memory entity store the route contract is agnostic to.
/// </remarks>
public sealed class WorkflowDefinitionRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000ff001"));
    private static readonly TeamId TeamB = new(Guid.Parse("bbbb0000-0000-0000-0000-0000000ff002"));

    private const string Base = "/api/local-node/workflows/definitions";
    private const string Key = "invoice-approval.v1";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddTestKernelClock();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // The substrate the route + store compose: an in-memory entity store + the canonical admission
        // validator (registry-derived classification, ADR 0143), then the durable definition store.
        var entityStore = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        builder.Services.AddSingleton(entityStore);
        builder.Services.AddSingleton<IEntityStore>(new InMemoryEntityStoreReader(entityStore));
        builder.Services.AddSingleton<ICapabilityAuthorityRegistry>(CapabilityAuthorityRegistry.Canonical);
        builder.Services.AddSingleton<IWorkflowAdmissionValidator, WorkflowAdmissionValidator>();
        builder.Services.AddTestAuthorizationGate();
        builder.Services.AddEntityStoreWorkflowDefinitionStore(_ => entityStore);

        _app = builder.Build();
        _activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        WorkflowDefinitionRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            _app.Services.GetRequiredService<AuthorizedWorkflowDefinitionLifecycle>(),
            _activeTeam,
            TimeProvider.System);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // ── Bodies (mirror the Harborline App `toWorkflowDefinition` output) ─────────────────

    private static object Text(string en) =>
        new { defaultLocale = "en", values = new Dictionary<string, string> { ["en"] = en } };

    private static object[] States() => new object[]
    {
        new { id = "Draft", label = Text("Draft"), kind = "Normal" },
        new { id = "PendingApproval", label = Text("Pending approval"), kind = "Normal", viewHint = "flow" },
        new { id = "Posted", label = Text("Posted"), kind = "Terminal" },
        new { id = "Rejected", label = Text("Rejected"), kind = "Terminal" },
    };

    private static object[] Triggers() => new object[]
    {
        new { id = "issued", kind = "Event", eventType = "Issued" },
        new { id = "approve", kind = "HumanAction", task = "invoice-approval" },
        new { id = "reject", kind = "HumanAction", task = "invoice-approval" },
    };

    private static object[] Transitions() => new object[]
    {
        new { id = "t-issue", from = "Draft", on = "issued", to = "PendingApproval" },
        new { id = "t-approve", from = "PendingApproval", on = "approve", to = "Posted" },
        new { id = "t-reject", from = "PendingApproval", on = "reject", to = "Rejected" },
    };

    /// <summary>The admissible invoice-approval definition: a CP post-JE action on the HUMAN approve
    /// transition (the client version + tenant are ignored — the server owns both).</summary>
    private static object AdmissibleBody(string key = Key) => new
    {
        key,
        version = "0.0.1",
        status = "Draft",
        tenant = "client-should-be-ignored",
        title = Text("Invoice approval"),
        mutability = "Locked",
        initialState = "Draft",
        states = States(),
        triggers = Triggers(),
        transitions = Transitions(),
        actions = new object[]
        {
            new
            {
                id = "a-post-je",
                on = new { transition = "t-approve" },
                kind = "CreateRecord",
                capabilityRef = "ledger.post-journal-entry",
                classification = "CP",
            },
        },
        guards = Array.Empty<object>(),
    };

    [Fact(DisplayName = "author→admit→persist→reload: an admissible definition round-trips intact through the node")]
    public async Task Save_Then_Load_RoundTrips()
    {
        var save = await _client.PutAsJsonAsync($"{Base}/{Key}", AdmissibleBody());
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var saved = await save.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Key, saved.GetProperty("key").GetString());
        Assert.Equal("1.0.0", saved.GetProperty("version").GetString());

        // Reload from the STORE (not memory) — the round-trip the Harborline App route proves.
        var loaded = await _client.GetFromJsonAsync<JsonElement>($"{Base}/{Key}");
        Assert.Equal(Key, loaded.GetProperty("key").GetString());
        Assert.Equal("1.0.0", loaded.GetProperty("version").GetString());
        Assert.Equal("Published", loaded.GetProperty("status").GetString());
        Assert.Equal("Draft", loaded.GetProperty("initialState").GetString());

        // Structure survives: the CP action on the human-approve transition.
        var action = loaded.GetProperty("actions")[0];
        Assert.Equal("a-post-je", action.GetProperty("id").GetString());
        Assert.Equal("CP", action.GetProperty("classification").GetString());
        Assert.Equal("t-approve", action.GetProperty("on").GetProperty("transition").GetString());

        // Display chrome the lean admission model drops survives via the verbatim authored JSON.
        Assert.Equal("Invoice approval", loaded.GetProperty("title").GetProperty("values").GetProperty("en").GetString());
        var draftState = loaded.GetProperty("states")[0];
        Assert.Equal("Draft", draftState.GetProperty("label").GetProperty("values").GetProperty("en").GetString());
    }

    [Fact(DisplayName = "list: a saved definition appears in the tenant's definition list")]
    public async Task List_Surfaces_Saved()
    {
        await _client.PutAsJsonAsync($"{Base}/{Key}", AdmissibleBody());

        var list = await _client.GetFromJsonAsync<JsonElement>(Base);
        var entries = list.EnumerateArray().ToList();
        Assert.Contains(entries, e => e.GetProperty("key").GetString() == Key);
        var entry = entries.First(e => e.GetProperty("key").GetString() == Key);
        Assert.Equal("Invoice approval", entry.GetProperty("name").GetString());
    }

    [Fact(DisplayName = "re-save: saving an existing key mints the next version")]
    public async Task ReSave_Mints_NextVersion()
    {
        var first = await _client.PutAsJsonAsync($"{Base}/{Key}", AdmissibleBody());
        Assert.Equal("1.0.0", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString());

        var second = await _client.PutAsJsonAsync($"{Base}/{Key}", AdmissibleBody());
        Assert.Equal("1.0.1", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString());
    }

    [Fact(DisplayName = "tenant: a definition saved under team A is invisible to team B (server-side tenant)")]
    public async Task CrossTenant_Isolated()
    {
        await _client.PutAsJsonAsync($"{Base}/{Key}", AdmissibleBody());

        _activeTeam.Active = TeamContextFor(TeamB, "Team B");
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Base}/{Key}")).StatusCode);

        _activeTeam.Active = TeamContextFor(TeamA, "Team A");
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"{Base}/{Key}")).StatusCode);
    }

    [Fact(DisplayName = "load: an unsaved key is a 404 (the builder seeds a fresh workflow)")]
    public async Task Load_Unsaved_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Base}/never.saved.v1")).StatusCode);
    }

    [Fact(DisplayName = "admission: an UNCLASSIFIED action is REJECTED at the wired PUT with the stable code")]
    public async Task Unclassified_Action_Rejected_At_Node()
    {
        // The admissible graph but the action declares no classification ⇒ Unspecified ⇒ refused.
        var body = new
        {
            key = "bad-unclassified.v1",
            title = Text("Bad"),
            mutability = "Locked",
            initialState = "Draft",
            states = States(),
            triggers = Triggers(),
            transitions = Transitions(),
            actions = new object[]
            {
                new { id = "a-post-je", on = new { transition = "t-approve" }, kind = "CreateRecord", capabilityRef = "ledger.post-journal-entry" },
            },
            guards = Array.Empty<object>(),
        };

        var resp = await _client.PutAsJsonAsync($"{Base}/bad-unclassified.v1", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(WorkflowAdmissionCodes.ActionUnclassified, problem.GetProperty("code").GetString());

        // Nothing was published — the rejected definition is not loadable.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Base}/bad-unclassified.v1")).StatusCode);
    }

    [Fact(DisplayName = "admission: a CP action reachable from an autonomous trigger is REJECTED at the wired PUT")]
    public async Task Cp_From_Autonomous_Rejected_At_Node()
    {
        // The CP post-JE action fires on t-issue, whose trigger 'issued' is an Event (autonomous) —
        // no human-task gates the firing ⇒ refused (cp_reachable_without_human_task).
        var body = new
        {
            key = "bad-cp-autonomous.v1",
            title = Text("Bad"),
            mutability = "Locked",
            initialState = "Draft",
            states = States(),
            triggers = Triggers(),
            transitions = Transitions(),
            actions = new object[]
            {
                new { id = "a-post-je", on = new { transition = "t-issue" }, kind = "CreateRecord", capabilityRef = "ledger.post-journal-entry", classification = "CP" },
            },
            guards = Array.Empty<object>(),
        };

        var resp = await _client.PutAsJsonAsync($"{Base}/bad-cp-autonomous.v1", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(WorkflowAdmissionCodes.CpReachableWithoutHumanTask, problem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Base}/bad-cp-autonomous.v1")).StatusCode);
    }

    private static TeamContext TeamContextFor(TeamId teamId, string name)
        => new(teamId, name, new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    /// <summary>A mutable active-team accessor so a test can switch the active team mid-flight.</summary>
    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        public MutableActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; set; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
