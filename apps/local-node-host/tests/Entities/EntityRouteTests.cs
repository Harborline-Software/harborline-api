using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// ADR 0115 gap C4 — route-level tests for the node-local entity API.
/// </summary>
/// <remarks>
/// <para>
/// Hosts the SAME route handlers <see cref="HostedEntityApiEndpoint"/> registers
/// on a real in-process Kestrel listener (ephemeral loopback port), backed by a
/// temp SQLite store (plain, unencrypted — SC-1 encryption is covered separately
/// in SqlCipherFailClosedTests), driven by a real <see cref="HttpClient"/>.
/// </para>
/// <para>
/// The test invokes the production registration helper <see cref="EntityRoutes.Map"/>
/// (single source of truth, no test/prod drift).
/// </para>
/// </remarks>
public sealed class EntityRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private int _sessionRouteExecutions;
    private int _admissionRouteExecutions;
    private MutableActiveTeamAccessor _activeTeam = null!;
    private TeamContext _teamA = null!;
    private TeamContext _teamB = null!;
    private MutableAuthorizationContext _authorization = null!;
    private ToggleableEntityValidator _validator = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTestKernelClock();

        // Ticket 151: the POST is now permission-gated (records:write) and runs the registered
        // pre-commit validator. Default: allow-all + accept-all so the pre-gate tests hold.
        _authorization = new MutableAuthorizationContext();
        _validator = new ToggleableEntityValidator();
        builder.Services.AddSingleton<IAuthorizationContext>(_authorization);
        // Ticket 205 slice 4: the route guards resolve at the gate. It follows the SAME mutable holding
        // set this host already flips, so a test that narrows the caller's permissions narrows the decision.
        builder.Services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.Following(
            permission => _authorization.HasPermission(permission)));

        // Real on-disk temp SQLite file — cross-connection persistence required
        // so DbContextFactory's per-request connections share the migrated schema.
        _dir = Path.Combine(Path.GetTempPath(), "harborline-entity-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "entity-test.db")};Pooling=False";
        _teamA = TeamContextFor("7e570000-0000-0000-0000-0000000000aa", "Tenant A");
        _teamB = TeamContextFor("7e570000-0000-0000-0000-0000000000bb", "Tenant B");
        _activeTeam = new MutableActiveTeamAccessor(_teamA, _teamB);

        // Register the FinancialLedgerEntityModule so LocalNodeDbContext can
        // configure LegalEntity / ChartOfAccounts / GLAccount.
        builder.Services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite(connectionString));
        builder.Services.AddSingleton<IActiveTeamAccessor>(_activeTeam);
        builder.Services.AddScoped<Harborline.Api.Foundation.MultiTenancy.ITenantContext, Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext>();
        builder.Services.AddSingleton<ISelectedSessionPermissionResolver, FailClosedSelectedSessionPermissionResolver>();
        builder.Services.AddScoped<SelectedSessionTenantContext>();

        _app = builder.Build();

        // Migrate the schema once up front (EnsureCreated) so the route tests
        // find a fully-configured store.
        var factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // In production the listener binds this feature before the middleware. This small seam keeps
        // the real route test on the same per-request selected-session identity path.
        _app.Use(async (context, next) =>
        {
            context.Features.Set(DesktopPlaneRequestFeature.Instance);
            var principalId = context.Request.Headers["X-Test-Principal"].ToString();
            if (!string.IsNullOrWhiteSpace(principalId))
            {
                var principal = Principal(principalId);
                context.Features.Set(principal);
                context.RequestServices.GetRequiredService<SelectedSessionTenantContext>().Bind(principal);
            }

            await next(context);
        });

        // Map the SAME production routes (mirrors HostedEntityApiEndpoint wiring).
        NodeMutationIdempotency.UseOnce(_app, TimeProvider.System);
        EntityRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            factory,
            _activeTeam,
            new Data.Entities.NodeEntityWriter(factory, _validator, Authorization.TestAuthorization.AllowGate()),
            TimeProvider.System);
        _app.MapPost("/api/session/credential-mint", () =>
        {
            _sessionRouteExecutions++;
            return Results.Ok(new { executions = _sessionRouteExecutions });
        });
        _app.MapPost("/api/local-node/admission", () =>
        {
            _admissionRouteExecutions++;
            return Results.Ok(new { executions = _admissionRouteExecutions });
        });

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
        await _teamA.DisposeAsync();
        await _teamB.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private const string Route = "/api/local-node/entities";

    [Fact(DisplayName = "Entity route: list is empty on fresh store")]
    public async Task List_Empty_OnFreshStore()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(0, doc.GetProperty("entities").GetArrayLength());
    }

    [Fact(DisplayName = "Entity route: create returns 201 + id + legalName")]
    public async Task Create_Returns201_WithIdAndLegalName()
    {
        var resp = await _client.PostAsJsonAsync(Route, new
        {
            legalName = "Acme Holdings LLC",
            kind = "Llc",
            taxClassification = "DisregardedEntity",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(doc.GetProperty("id").GetString()));
        Assert.Equal("Acme Holdings LLC", doc.GetProperty("legalName").GetString());
    }

    [Fact(DisplayName = "Entity route: same Idempotency-Key replays one instance with the same response shape")]
    public async Task Create_SameIdempotencyKey_ReplaysOneInstance()
    {
        using var first = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new { legalName = "Idempotent LLC" }),
        };
        first.Headers.Add(NodeMutationIdempotency.HeaderName, "entity-replay-1");
        using var second = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new { legalName = "Idempotent LLC" }),
        };
        second.Headers.Add(NodeMutationIdempotency.HeaderName, "entity-replay-1");

        using var firstResponse = await _client.SendAsync(first);
        using var secondResponse = await _client.SendAsync(second);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);

        Assert.Equal(
            await firstResponse.Content.ReadAsStringAsync(),
            await secondResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            1,
            (await _client.GetFromJsonAsync<JsonElement>(Route))
                .GetProperty("entities").GetArrayLength());
    }

    [Fact(DisplayName = "Entity route: same key with a different body is a 409 and does not execute")]
    public async Task Create_SameIdempotencyKey_DifferentBody_IsConflict()
    {
        using var first = Request("payload-conflict", "First LLC");
        using var second = Request("payload-conflict", "Second LLC");

        using var firstResponse = await _client.SendAsync(first);
        using var secondResponse = await _client.SendAsync(second);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
        Assert.Equal(1, (await _client.GetFromJsonAsync<JsonElement>(Route))
            .GetProperty("entities").GetArrayLength());
    }

    [Fact(DisplayName = "Entity route: canonical path case and query ordering still replay")]
    public async Task Create_CanonicalPathAndQuery_Replays()
    {
        using var first = Request("canonical-route", "Canonical LLC", "/api/local-node/entities?z=2&a=1");
        using var second = Request("canonical-route", "Canonical LLC", "/api/LOCAL-NODE/Entities?a=1&z=2");

        using var firstResponse = await _client.SendAsync(first);
        using var secondResponse = await _client.SendAsync(second);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        Assert.Equal(await firstResponse.Content.ReadAsStringAsync(), await secondResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, (await _client.GetFromJsonAsync<JsonElement>(Route))
            .GetProperty("entities").GetArrayLength());
    }

    [Fact(DisplayName = "Entity route: whitespace-only Idempotency-Key is a 400")]
    public async Task Create_WhitespaceOnlyIdempotencyKey_IsBadRequest()
    {
        using var request = Request(" ", "Whitespace LLC");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact(DisplayName = "Entity route: multiple Idempotency-Key headers are a 400")]
    public async Task Create_MultipleIdempotencyKeys_AreBadRequest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = JsonContent.Create(new { legalName = "Multiple LLC" }),
        };
        request.Headers.Add(NodeMutationIdempotency.HeaderName, "one");
        request.Headers.Add(NodeMutationIdempotency.HeaderName, "two");

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact(DisplayName = "Entity route: different selected principals do not share a replay")]
    public async Task Create_SameKey_DifferentPrincipals_ExecutesTwice()
    {
        using var first = Request("principal-isolation", "Principal One LLC");
        first.Headers.Add("X-Test-Principal", "principal-one");
        using var second = Request("principal-isolation", "Principal Two LLC");
        second.Headers.Add("X-Test-Principal", "principal-two");

        using var firstResponse = await _client.SendAsync(first);
        using var secondResponse = await _client.SendAsync(second);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        Assert.NotEqual(await firstResponse.Content.ReadAsStringAsync(), await secondResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, (await _client.GetFromJsonAsync<JsonElement>(Route))
            .GetProperty("entities").GetArrayLength());
    }

    [Fact(DisplayName = "Entity route: bootstrap replay scope changes with the active team")]
    public async Task Create_SameKey_AfterActiveTeamSwitch_ExecutesInNewTenant()
    {
        using var first = Request("bootstrap-team-switch", "Tenant A LLC");
        using var firstResponse = await _client.SendAsync(first);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        await _activeTeam.SetActiveAsync(_teamB.TeamId, CancellationToken.None);

        using var second = Request("bootstrap-team-switch", "Tenant B LLC");
        using var secondResponse = await _client.SendAsync(second);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        var secondDocument = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Tenant B LLC", secondDocument.GetProperty("legalName").GetString());
        Assert.Equal(_teamB.TeamId, _activeTeam.Active!.TeamId);
        Assert.NotEqual(
            Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(_teamA.TeamId),
            Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(_teamB.TeamId));

        await using var context = await _app.Services
            .GetRequiredService<IDbContextFactory<LocalNodeDbContext>>()
            .CreateDbContextAsync();
        var rows = await context.Set<Harborline.Api.Blocks.FinancialLedger.Models.LegalEntity>()
            .AsNoTracking()
            .ToListAsync();
        Assert.Contains(rows, row =>
            row.TenantId == Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(_teamA.TeamId) &&
            row.LegalName == "Tenant A LLC");
        Assert.Contains(rows, row =>
            row.TenantId == Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(_teamB.TeamId) &&
            row.LegalName == "Tenant B LLC");
    }

    [Fact(DisplayName = "Session routes are explicitly excluded from idempotency caching")]
    public async Task SessionRoute_IsNeverCached()
    {
        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/session/credential-mint");
        first.Headers.Add(NodeMutationIdempotency.HeaderName, "session-key");
        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/session/credential-mint");
        second.Headers.Add(NodeMutationIdempotency.HeaderName, "session-key");

        using var firstResponse = await _client.SendAsync(first);
        using var secondResponse = await _client.SendAsync(second);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(2, _sessionRouteExecutions);
    }

    [Fact(DisplayName = "Admission routes are explicitly excluded from idempotency caching")]
    public async Task AdmissionRoute_IsNeverCached()
    {
        using var first = new HttpRequestMessage(HttpMethod.Post, "/api/local-node/admission");
        first.Headers.Add(NodeMutationIdempotency.HeaderName, "admission-key");
        using var second = new HttpRequestMessage(HttpMethod.Post, "/api/local-node/admission");
        second.Headers.Add(NodeMutationIdempotency.HeaderName, "admission-key");

        using var firstResponse = await _client.SendAsync(first);
        using var secondResponse = await _client.SendAsync(second);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(2, _admissionRouteExecutions);
    }

    private static HttpRequestMessage Request(string key, string legalName, string? route = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route ?? Route)
        {
            Content = JsonContent.Create(new { legalName }),
        };
        request.Headers.Add(NodeMutationIdempotency.HeaderName, key);
        return request;
    }

    private static SelectedSessionRequestPrincipal Principal(string id) => new(
        accountId: $"account-{id}",
        tenantId: new TenantId("tenant-entity-tests"),
        principalUserId: new PrincipalUserId(id),
        canonicalParty: new CanonicalPartyReference($"party-{id}"),
        membershipId: $"membership-{id}",
        membershipOwnerVersion: 1,
        pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion($"grant-{id}", 1)],
        authorizationEpoch: 1,
        sessionCorrelationId: $"session-{id}",
        coordinationCorrelationId: $"coordination-{id}");

    private static TeamContext TeamContextFor(string id, string displayName) =>
        new(
            new TeamId(Guid.Parse(id)),
            displayName,
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        private readonly TeamContext _initial;
        private readonly TeamContext _other;

        internal MutableActiveTeamAccessor(TeamContext initial, TeamContext other)
        {
            _initial = initial;
            _other = other;
            Active = initial;
        }

        public TeamContext? Active { get; private set; }

        public Task SetActiveAsync(TeamId teamId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var target = teamId.Equals(_initial.TeamId) ? _initial :
                teamId.Equals(_other.TeamId) ? _other :
                throw new InvalidOperationException($"Unknown test team {teamId}.");
            var previous = Active;
            Active = target;
            if (!ReferenceEquals(previous, target))
            {
                ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(previous, target));
            }

            return Task.CompletedTask;
        }

        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
    }

    [Fact(DisplayName = "Entity route: created entity appears in list")]
    public async Task Create_ThenList_ShowsEntity()
    {
        await _client.PostAsJsonAsync(Route, new
        {
            legalName = "Sunrise Rentals LLC",
            kind = "Llc",
            taxClassification = "DisregardedEntity",
        });

        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        var entities = doc.GetProperty("entities");
        Assert.Equal(1, entities.GetArrayLength());
        Assert.Equal("Sunrise Rentals LLC", entities[0].GetProperty("legalName").GetString());
        Assert.Equal("Llc", entities[0].GetProperty("kind").GetString());
        Assert.Equal("DisregardedEntity", entities[0].GetProperty("taxClassification").GetString());
    }

    [Fact(DisplayName = "Entity route: create trims whitespace from legalName")]
    public async Task Create_TrimsLegalName()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { legalName = "  Padded Name LLC  " });
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Padded Name LLC", doc.GetProperty("legalName").GetString());
    }

    [Fact(DisplayName = "Entity route: create defaults kind to Llc and taxClassification to DisregardedEntity")]
    public async Task Create_DefaultKindAndTaxClassification()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { legalName = "Minimal Co" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var list = await _client.GetFromJsonAsync<JsonElement>(Route);
        var entity = list.GetProperty("entities").EnumerateArray()
            .Single(e => e.GetProperty("legalName").GetString() == "Minimal Co");
        Assert.Equal("Llc", entity.GetProperty("kind").GetString());
        Assert.Equal("DisregardedEntity", entity.GetProperty("taxClassification").GetString());
    }

    [Fact(DisplayName = "Entity route: create rejects missing legalName (400)")]
    public async Task Create_RejectsMissingLegalName()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { kind = "Llc" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Entity route: create rejects whitespace-only legalName (400)")]
    public async Task Create_RejectsWhitespaceLegalName()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { legalName = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Entity route: create rejects invalid kind (400)")]
    public async Task Create_RejectsInvalidKind()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { legalName = "x", kind = "SuperCorp" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Entity route: create rejects invalid taxClassification (400)")]
    public async Task Create_RejectsInvalidTaxClassification()
    {
        var resp = await _client.PostAsJsonAsync(Route, new
        {
            legalName = "x",
            kind = "Llc",
            taxClassification = "NotAClass",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Entity route: multiple entities accumulate in list")]
    public async Task Create_MultipleEntities_AllAppearInList()
    {
        await _client.PostAsJsonAsync(Route, new { legalName = "Entity A" });
        await _client.PostAsJsonAsync(Route, new { legalName = "Entity B" });

        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        var entities = doc.GetProperty("entities");
        Assert.Equal(2, entities.GetArrayLength());
    }

    // ── Ticket 151: stage-one authorization gate + pre-commit validation ─────────

    [Fact(DisplayName = "Entity route: create without records:write is refused (403), nothing persists")]
    public async Task Create_WithoutRecordsWrite_IsRefused()
    {
        _authorization.Allow(Harborline.Api.Foundation.IdentityAtlas.TeamRolePermissions.RecordsRead);

        var resp = await _client.PostAsJsonAsync(Route, new { legalName = "Unauthorized LLC" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());

        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(0, doc.GetProperty("entities").GetArrayLength());
    }

    [Fact(DisplayName = "Entity route: authorization refusal precedes an invalid body")]
    public async Task Create_WithoutRecordsWrite_RefusesBeforeValidation()
    {
        _authorization.Allow(Harborline.Api.Foundation.IdentityAtlas.TeamRolePermissions.RecordsRead);
        _validator.RejectWith("body is invalid");

        var resp = await _client.PostAsJsonAsync(Route, new { legalName = "Unauthorized invalid LLC" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        Assert.Empty(_validator.SeenTopLevelKeys);
    }

    [Fact(DisplayName = "Entity route: create runs the registered validator and refuses on failure (422)")]
    public async Task Create_WhenValidatorRejects_IsRefused()
    {
        _validator.RejectWith("legal entity body refused by authority validation");

        var resp = await _client.PostAsJsonAsync(Route, new { legalName = "Invalid LLC" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", body.GetProperty("error").GetString());
        Assert.Equal("entity.validation.body_invalid", body.GetProperty("code").GetString());
        Assert.Equal(0, body.GetProperty("pointers").GetArrayLength());

        // The validator sees WIRE-SHAPED (camelCase) keys — the JSON as the client sent it, not the
        // C# DTO's PascalCase (a real schema validator would silently miss every property otherwise).
        Assert.Contains("legalName", _validator.SeenTopLevelKeys);
        Assert.DoesNotContain("LegalName", _validator.SeenTopLevelKeys);

        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(0, doc.GetProperty("entities").GetArrayLength());
    }

    /// <summary>Accept-all by default; <see cref="RejectWith"/> flips it to refuse every body.
    /// Captures the top-level property names of every body it sees, so tests can pin the wire shape.</summary>
    private sealed class ToggleableEntityValidator : Harborline.Api.Foundation.Assets.Entities.IEntityValidator
    {
        private string? _rejectMessage;

        public List<string> SeenTopLevelKeys { get; } = new();

        public void RejectWith(string message) => _rejectMessage = message;

        public Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default)
        {
            SeenTopLevelKeys.AddRange(
                body.RootElement.EnumerateObject().Select(p => p.Name));
            return _rejectMessage is null
                ? Task.CompletedTask
                : Task.FromException(new Harborline.Api.Foundation.Assets.Entities.EntityValidationException(_rejectMessage));
        }
    }
}
