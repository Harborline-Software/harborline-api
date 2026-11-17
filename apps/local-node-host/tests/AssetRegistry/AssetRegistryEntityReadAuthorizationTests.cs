using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

/// <summary>
/// Ticket 358 slice 1 — ADR 0060: authority travels with the READ. The two asset-registry entity reads
/// (<c>GET /entities[?type=]</c> and <c>GET /entities/{id}</c>) used to be fenced by audience alone, so a
/// principal holding no <c>records:read</c> grant could enumerate the tenant's typed entities. These tests
/// ride the production route map over a real listener with a gate that grants nothing, then with a gate that
/// grants <c>records:read</c> at the install root, and prove the refusal is the node's one rendered 403,
/// recorded with the decision that refused it and addressable by its audit id.
/// </summary>
public sealed class AssetRegistryEntityReadAuthorizationTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000003580a1"));
    private static readonly TeamId TeamB = new(Guid.Parse("bbbb0000-0000-0000-0000-0000003580b1"));

    private const string AssetBase = "/api/local-node/asset-registry";
    private const string UnitType = "residential-unit";

    /// <summary>What the acting principal holds. The gate is real; this is the grant set behind it.</summary>
    private bool _holdsRecordsRead = true;

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;
    private TenantId _tenant;
    private SelectedSessionRequestPrincipal? _selected;
    /// <summary>When set, the request runs inside a bound LAN-device principal scope (no selected session).</summary>
    private bool _lanDevice;
    private ReadTrackingRepository _repository = null!;
    private ReadTrackingEdges _edges = null!;
    private ReadTrackingConditions _conditions = null!;
    private ReadTrackingSubmissions _submissions = null!;
    private readonly List<AuthorizationGateRequest> _authorizationRequests = [];
    private readonly List<ScopeExpression> _grantScopes = [];
    private AuthorizationGate _gate = null!;
    private bool _pinSelectedTenant;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });
        // Ticket 294 slice 3b: the desktop actor (no compile-time operator id).
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();

        builder.Services.AddTestAuthorizationGate().AddTestNodeForms();
        builder.Services.AddNodeAssetRegistry();

        // The ONE difference from AssetRegistryRouteTests: a REAL gate deciding from what the principal
        // holds, so the same fixture answers both verdicts for one act. Registered last, so it is the
        // AuthorizationGate the request container resolves.
        var grants = new TestRouteGate.ScopedGrantSource(request =>
        {
            _authorizationRequests.Add(request);
            return (_holdsRecordsRead || request.Act.Operation.Value != TeamRolePermissions.RecordsRead)
                && (!_pinSelectedTenant || request.Tenant.Value == "aaaa0000-0000-0000-0000-0000003580a1");
        }, _grantScopes);
        _gate = new AuthorizationGate(grants, new EmptyRecordStandingResolver(), grants);
        builder.Services.AddSingleton(_gate);
        // The shipped refusal-audit composition — the same registration Program.cs makes.
        builder.Services.AddAuthorizationRefusalAudit();
        builder.Services.AddAuthorizedActAudit();

        _app = builder.Build();

        _app.Services.GetRequiredService<IEntityTypeRegistry>().SeedType(new EntityTypeSeed(
            new EntityTypeId(UnitType),
            new EntityTypeDescriptor("Residential unit", EntityTrait.Container | EntityTrait.Maintainable),
            CascadeLayer.Pack));

        _activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));
        _tenant = NodeTenant.Resolve(_activeTeam);

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            if (_selected is not null) http.Features.Set(_selected);
            using var device = _lanDevice
                ? Harborline.Api.LocalNodeHost.Data.Audit.NodeCallerAttributionScope.EnterDevice(
                    "lan-device", _tenant.Value, "lan-device-principal")
                : null;
            await next(http);
        });
        _repository = new ReadTrackingRepository(_app.Services.GetRequiredService<IRegistryEntityRepository>());
        _edges = new ReadTrackingEdges(_app.Services.GetRequiredService<ITypedRelationshipStore>());
        _conditions = new ReadTrackingConditions(_app.Services.GetRequiredService<IConditionAssessmentStore>());
        _submissions = new ReadTrackingSubmissions(_app.Services.GetRequiredService<IFormSubmissionRecordStore>());
        AssetRegistryRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            _app.Services.GetRequiredService<IEntityTypeRegistry>(),
            _repository,
            _edges,
            _conditions,
            _submissions,
            _activeTeam,
            TimeProvider.System);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact(DisplayName = "holds 358.A1: a principal with no records:read grant is refused on the entity list and on get-by-id")]
    public async Task A_principal_without_a_covering_grant_is_refused_on_both_reads()
    {
        var entityId = await CreateUnitAsync("Unit 1");
        _holdsRecordsRead = false;

        var list = await _client.GetAsync($"{AssetBase}/entities");
        await AssertRefusedAsync(list, entityId);

        var byType = await _client.GetAsync($"{AssetBase}/entities?type={UnitType}");
        await AssertRefusedAsync(byType, entityId);

        var detail = await _client.GetAsync($"{AssetBase}/entities/{entityId}");
        await AssertRefusedAsync(detail, entityId);

        // The decision precedes the repository read, so a record that does NOT exist refuses identically —
        // an unauthorized caller cannot probe existence through the refusal.
        var missing = await _client.GetAsync($"{AssetBase}/entities/re_0000000000000000000000000");
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
    }

    [Fact(DisplayName = "holds 358.A2: a principal holding records:read reads the entity list and the entity detail")]
    public async Task A_principal_with_the_grant_reads_both()
    {
        var entityId = await CreateUnitAsync("Unit 1");

        var list = await _client.GetAsync($"{AssetBase}/entities");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(entityId, (await list.Content.ReadFromJsonAsync<JsonElement>()).GetRawText(), StringComparison.Ordinal);

        var byType = await _client.GetAsync($"{AssetBase}/entities?type={UnitType}");
        Assert.Equal(HttpStatusCode.OK, byType.StatusCode);
        Assert.Contains(entityId, (await byType.Content.ReadFromJsonAsync<JsonElement>()).GetRawText(), StringComparison.Ordinal);

        var detail = await _client.GetAsync($"{AssetBase}/entities/{entityId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(
            entityId,
            (await detail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());

        // The permitted read writes no refusal row.
        Assert.Empty(await RefusalRowsAsync());
    }

    [Fact(DisplayName = "holds 358.A2: the authorized list never carries another tenant's rows")]
    public async Task The_authorized_list_never_leaks_another_tenants_rows()
    {
        var teamAEntity = await CreateUnitAsync("Team A unit");

        _activeTeam.Active = TeamContextFor(TeamB, "Team B");
        var teamBList = await _client.GetAsync($"{AssetBase}/entities");
        Assert.Equal(HttpStatusCode.OK, teamBList.StatusCode);
        var rows = await teamBList.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(rows.GetProperty("entities").EnumerateArray());
        Assert.DoesNotContain(teamAEntity, rows.GetRawText(), StringComparison.Ordinal);

        _activeTeam.Active = TeamContextFor(TeamA, "Team A");
        var teamAList = await _client.GetAsync($"{AssetBase}/entities");
        Assert.Contains(teamAEntity, (await teamAList.Content.ReadFromJsonAsync<JsonElement>()).GetRawText(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "holds 358.A3: the refused read is recorded with the deciding decision and addressable by its audit id")]
    public async Task The_refused_read_is_recorded_with_the_deciding_decision()
    {
        var entityId = await CreateUnitAsync("Unit 1");
        _holdsRecordsRead = false;

        var detail = await _client.GetAsync($"{AssetBase}/entities/{entityId}");
        var body = (await detail.Content.ReadFromJsonAsync<JsonElement>()).GetRawText();

        var row = Assert.Single(await RefusalRowsAsync());
        using var json = JsonDocument.Parse(body);
        Assert.Equal(row.AuditId, json.RootElement.GetProperty("auditId").GetGuid());

        // The decision the guard made is the one recorded — never re-decided, never invented — and it
        // carries the four-stage trace.
        Assert.Equal(false, row.Payload.Payload.Body["preDecision"]);
        Assert.Equal("record", row.Target!.Value.RecordKind);
        Assert.NotNull(row.AuthoritySnapshot);
        Assert.Equal(4, row.AuthoritySnapshot.Trace!.Count);

        // The classified diagnostic the response withheld is on the row, not in the body.
        var diagnostic = Assert.IsType<string>(row.Payload.Payload.Body[AuthorizationRefusalAudit.DiagnosticKey]);
        Assert.Contains($"denied;operation={TeamRolePermissions.RecordsRead}", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostic, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selected_record_read_uses_selected_tenant_and_returns_its_actual_audit_receipt()
    {
        var entityId = await CreateUnitAsync("Selected tenant record");
        _activeTeam.Active = TeamContextFor(TeamB, "Other active team");
        _selected = new SelectedSessionRequestPrincipal("selected-account", _tenant,
            new PrincipalUserId("selected-holder"), new CanonicalPartyReference("selected-party"),
            "membership", 1, [new PinnedGrantOwnerVersion("fixture", 1)], 1, "session", "coordination");
        const string correlation = "43300000-0000-4000-8000-000000000021";
        _client.DefaultRequestHeaders.Add("X-Correlation-ID", correlation);
        using var response = await _client.GetAsync($"{AssetBase}/entities/{entityId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = Guid.Parse(Assert.Single(response.Headers.GetValues("X-Harborline-Audit-Id")));
        var rows = new List<AuditRecord>();
        await foreach (var row in _app.Services.GetRequiredService<IAuditTrail>().QueryAsync(new AuditQuery(_tenant)))
            rows.Add(row);
        var accepted = Assert.Single(rows, row => row.AuditId == id);
        Assert.Equal("selected-holder", accepted.Actor!.Value.Value);
        Assert.Equal(entityId, accepted.Target!.Value.RecordId);
        Assert.Equal("records:read", accepted.Act!.Value.Operation.Value);
        Assert.Equal(_tenant, accepted.TenantId);
        Assert.Equal("record", accepted.Target.Value.RecordKind);
        Assert.NotNull(accepted.AuthoritySnapshot);
        Assert.Equal(correlation, accepted.Payload.Payload.Body["correlation_id"]?.ToString());
        Assert.Equal(correlation, Assert.Single(response.Headers.GetValues("X-Harborline-Audit-Correlation")));

        _holdsRecordsRead = false;
        _repository.ReadTenants.Clear();
        using var denied = await _client.GetAsync($"{AssetBase}/entities/{entityId}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var body = await denied.Content.ReadFromJsonAsync<JsonElement>();
        var refusal = Assert.Single(await RefusalRowsAsync());
        Assert.Equal("selected-holder", refusal.Actor!.Value.Value);
        Assert.Equal(body.GetProperty("auditId").GetGuid(), refusal.AuditId);
        Assert.Equal(correlation, body.GetProperty("correlationId").GetString());
        Assert.Equal(correlation, refusal.Payload.Payload.Body["correlation_id"]?.ToString());
        Assert.Empty(_repository.ReadTenants);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Selected_entity_reads_never_enter_reusable_caches(bool allowed)
    {
        var entityId = await CreateUnitAsync("Selected tenant record");
        _selected = new SelectedSessionRequestPrincipal("selected-account", _tenant,
            new PrincipalUserId("selected-holder"), new CanonicalPartyReference("selected-party"),
            "membership", 1, [new PinnedGrantOwnerVersion("fixture", 1)], 1, "session", "coordination");
        _holdsRecordsRead = allowed;
        foreach (var route in new[] { $"{AssetBase}/entities", $"{AssetBase}/entities/{entityId}" })
        {
            using var response = await _client.GetAsync(route);
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task Selected_record_read_never_reads_the_ambient_tenants_matching_record()
    {
        var entityId = await CreateUnitAsync("Other tenant secret");
        _selected = new SelectedSessionRequestPrincipal("selected-account", new TenantId(TeamB.Value.ToString()),
            new PrincipalUserId("selected-holder"), new CanonicalPartyReference("selected-party"),
            "membership", 1, [new PinnedGrantOwnerVersion("fixture", 1)], 1, "session", "coordination");
        _repository.ReadTenants.Clear();
        using var response = await _client.GetAsync($"{AssetBase}/entities/{entityId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(_selected.TenantId, Assert.Single(_repository.ReadTenants));
        Assert.DoesNotContain(_tenant, _repository.ReadTenants);
        Assert.False(response.Headers.Contains("X-Harborline-Audit-Id"));
    }

    [Fact]
    public async Task Selected_cookie_without_its_principal_never_falls_back_or_reads()
    {
        var entityId = await CreateUnitAsync("Ambient secret");
        _repository.ReadTenants.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{AssetBase}/entities/{entityId}");
        request.Headers.Add("Cookie", "__Host-hl-selected=unresolved");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_repository.ReadTenants);
    }

    [Theory]
    [InlineData("tree")]
    [InlineData("condition")]
    [InlineData("submissions")]
    public async Task Entity_sub_resource_reads_are_authorized_and_tenant_bound_like_the_point_read(string resource)
    {
        var entityId = await CreateUnitAsync("Sub-resource record");
        const string missingId = "re_0000000000000000000000000";

        // Allowed: the desktop caller holding records:read reads the addressed record.
        using (var allowed = await _client.GetAsync($"{AssetBase}/entities/{entityId}/{resource}"))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.True(allowed.Headers.CacheControl?.NoStore);
            Assert.Contains(entityId, (await allowed.Content.ReadFromJsonAsync<JsonElement>()).GetRawText(), StringComparison.Ordinal);
        }

        // Denied: the decision precedes the repository read, for a present record and a missing one alike.
        _holdsRecordsRead = false;
        _repository.ReadTenants.Clear();
        using (var denied = await _client.GetAsync($"{AssetBase}/entities/{entityId}/{resource}"))
        {
            await AssertRefusedAsync(denied, entityId);
            Assert.True(denied.Headers.CacheControl?.NoStore);
        }
        using (var deniedMissing = await _client.GetAsync($"{AssetBase}/entities/{missingId}/{resource}"))
            Assert.Equal(HttpStatusCode.Forbidden, deniedMissing.StatusCode);
        Assert.Empty(_repository.ReadTenants);
        _holdsRecordsRead = true;

        // Selected tenant B while the node's active team is A: only B is read, so A's record is a 404.
        _selected = new SelectedSessionRequestPrincipal("selected-account", new TenantId(TeamB.Value.ToString()),
            new PrincipalUserId("selected-holder"), new CanonicalPartyReference("selected-party"),
            "membership", 1, [new PinnedGrantOwnerVersion("fixture", 1)], 1, "session", "coordination");
        _repository.ReadTenants.Clear();
        using (var otherTenant = await _client.GetAsync($"{AssetBase}/entities/{entityId}/{resource}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
            Assert.True(otherTenant.Headers.CacheControl?.NoStore);
        }
        Assert.Equal(_selected.TenantId, Assert.Single(_repository.ReadTenants));
        Assert.DoesNotContain(_tenant, _repository.ReadTenants);
        _selected = null;

        // A selected cookie whose principal did not resolve never falls back to the active team.
        _repository.ReadTenants.Clear();
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{AssetBase}/entities/{entityId}/{resource}"))
        {
            request.Headers.Add("Cookie", "__Host-hl-selected=unresolved");
            using var unresolved = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, unresolved.StatusCode);
        }
        Assert.Empty(_repository.ReadTenants);

        // A bound LAN-device principal without a selected session gets the point read's same 401.
        _lanDevice = true;
        using (var device = await _client.GetAsync($"{AssetBase}/entities/{entityId}/{resource}"))
            Assert.Equal(HttpStatusCode.Unauthorized, device.StatusCode);
        using (var pointRead = await _client.GetAsync($"{AssetBase}/entities/{entityId}"))
            Assert.Equal(HttpStatusCode.Unauthorized, pointRead.StatusCode);
        Assert.Empty(_repository.ReadTenants);
        _lanDevice = false;
    }

    [Fact]
    public async Task Readable_related_ids_cannot_be_mutated_after_the_decision()
    {
        var method = typeof(AssetRegistryRoutes).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "ReadableAsync" && m.GetParameters()[3].ParameterType == typeof(IReadOnlyList<RegistryEntityId>));
        var allowed = new RegistryEntityId("re_00000000000000000000000002");
        var refused = new RegistryEntityId("re_00000000000000000000000004");
        var decisions = new Dictionary<string, bool> { [allowed.Value] = true, [refused.Value] = false };
        var pending = (ValueTask<IReadOnlyList<RegistryEntityId>>)method.Invoke(null,
            [new Microsoft.AspNetCore.Http.DefaultHttpContext(), default(AuthorizationWriteContext), decisions,
                new[] { allowed, refused }, CancellationToken.None])!;
        var result = await pending;
        Assert.Equal("re_00000000000000000000000002", Assert.Single(result).Value);
        Assert.Throws<NotSupportedException>(() => ((IList<RegistryEntityId>)result).Add(refused));
    }

    [Fact]
    public async Task Invalid_related_id_is_refused_and_cached()
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = _app.Services };
        var authority = RequestAuthorization.Authority(http, _tenant, TimeProvider.System);
        var decisions = new Dictionary<string, bool>();
        Assert.False(await RequestAuthorization.AllowedAsync(http, authority, "records:read", "a/b", decisions, CancellationToken.None));
        Assert.False(decisions["a/b"]);
    }

    [Fact]
    public async Task Tree_query_with_refused_child_id_never_loads_or_projects_it()
    {
        await SeedSelectedTenantSubResourcesAsync();
        var tenant = new TenantId("aaaa0000-0000-0000-0000-0000003580a1");
        var at = new Instant(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var hidden = new RegistryEntityId("re_00000000000000000000000004");
        await _repository.UpsertAsync(new RegistryEntity
        {
            Id = hidden, TenantId = tenant, Type = new EntityTypeId(UnitType), DisplayName = "Hidden child", CreatedAt = at,
        }, at);
        await _edges.AddAsync(new TypedRelationship
        {
            Id = new TypedRelationshipId("query-hidden-edge"), TenantId = tenant, Kind = RelationshipKind.Contains,
            From = new RegistryEntityId("re_00000000000000000000000001"), To = hidden, EffectiveFrom = at,
        });
        _grantScopes.Add(ScopeExpression.Parse("/records/re_00000000000000000000000002"));
        _repository.ReadIds.Clear();
        using var response = await _client.GetAsync($"{AssetBase}/entities/re_00000000000000000000000001/tree?asOf=re_00000000000000000000000004");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_authorizationRequests, request => request.Target.RecordId == "re_00000000000000000000000004");
        Assert.DoesNotContain("re_00000000000000000000000004", _repository.ReadIds);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("re_00000000000000000000000004", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden child", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_child_does_not_change_tree_body_or_headers()
    {
        await SeedSelectedTenantSubResourcesAsync();
        _client.DefaultRequestHeaders.Add("X-Correlation-ID", "43300000-0000-4000-8000-000000000021");
        _grantScopes.Add(ScopeExpression.Parse("/records/re_00000000000000000000000002"));
        const string route = AssetBase + "/entities/re_00000000000000000000000001/tree?asOf=2026-01-02T00%3A00%3A00Z";
        using var before = await _client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var beforeBody = await before.Content.ReadAsStringAsync();
        var parsed = JsonNode.Parse(beforeBody)!.AsObject();
        Assert.Equal("re_00000000000000000000000002", Assert.Single(parsed["children"]!.AsArray())!["id"]!.GetValue<string>());

        var tenant = new TenantId("aaaa0000-0000-0000-0000-0000003580a1");
        var at = new Instant(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var hidden = new RegistryEntityId("re_00000000000000000000000004");
        await _repository.UpsertAsync(new RegistryEntity
        {
            Id = hidden, TenantId = tenant, Type = new EntityTypeId(UnitType), DisplayName = "Hidden child", CreatedAt = at,
        }, at);
        await _edges.AddAsync(new TypedRelationship
        {
            Id = new TypedRelationshipId("differential-hidden-edge"), TenantId = tenant, Kind = RelationshipKind.Contains,
            From = new RegistryEntityId("re_00000000000000000000000001"), To = hidden, EffectiveFrom = at,
        });
        using var after = await _client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Contains(_authorizationRequests, request => request.Target.RecordId == "re_00000000000000000000000004");
        var afterBody = await after.Content.ReadAsStringAsync();
        static string NormalizeBody(string body)
        {
            var parsedBody = JsonNode.Parse(body)!.AsObject();
            foreach (var field in new[] { "auditId", "correlationId" })
            {
                if (!parsedBody.ContainsKey(field)) continue;
                var value = parsedBody[field]?.ToJsonString() ?? "null";
                var original = "\"" + field + "\":" + value;
                Assert.Contains(original, body, StringComparison.Ordinal);
                body = body.Replace(original, "\"" + field + "\":null", StringComparison.Ordinal);
            }
            return body;
        }
        Assert.Equal(NormalizeBody(beforeBody), NormalizeBody(afterBody));

        static string[] Headers(HttpResponseMessage response) => response.Headers.Concat(response.Content.Headers)
            .Select(h => h.Key.ToLowerInvariant() + ":" +
                (h.Key.Equals("Date", StringComparison.OrdinalIgnoreCase)
                    || h.Key.Equals("X-Harborline-Audit-Id", StringComparison.OrdinalIgnoreCase)
                    || h.Key.Equals("X-Harborline-Audit-Correlation", StringComparison.OrdinalIgnoreCase)
                    ? "<normalized>" : JsonSerializer.Serialize(h.Value.ToArray())))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.True(before.Headers.Contains("X-Harborline-Audit-Id"));
        Assert.True(before.Headers.Contains("X-Harborline-Audit-Correlation"));
        Assert.Equal(Headers(before), Headers(after));
    }

    [Fact]
    public async Task Tree_shows_only_children_the_caller_can_open_and_records_one_act()
    {
        await SeedSelectedTenantSubResourcesAsync();
        var tenant = new TenantId("aaaa0000-0000-0000-0000-0000003580a1");
        var at = new Instant(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var hidden = new RegistryEntityId("re_00000000000000000000000004");
        await _repository.UpsertAsync(new RegistryEntity
        {
            Id = hidden, TenantId = tenant, Type = new EntityTypeId(UnitType),
            DisplayName = "Hidden child", CreatedAt = at,
        }, at);
        await _edges.AddAsync(new TypedRelationship
        {
            Id = new TypedRelationshipId("hidden-edge"), TenantId = tenant, Kind = RelationshipKind.Contains,
            From = new RegistryEntityId("re_00000000000000000000000001"), To = hidden, EffectiveFrom = at,
        });
        _grantScopes.Add(ScopeExpression.Parse("/records/re_00000000000000000000000002"));

        using var tree = await _client.GetAsync($"{AssetBase}/entities/re_00000000000000000000000001/tree");
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        var body = await tree.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("re_00000000000000000000000002", Assert.Single(body.GetProperty("children").EnumerateArray()).GetProperty("id").GetString());
        Assert.DoesNotContain("Hidden child", body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(hidden.Value, body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("count", body.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tree.Headers, header => header.Key.Contains("count", StringComparison.OrdinalIgnoreCase));
        var rows = new List<AuditRecord>();
        await foreach (var row in _app.Services.GetRequiredService<IAuditTrail>().QueryAsync(new AuditQuery(tenant))) rows.Add(row);
        var receipt = Assert.Single(rows);
        Assert.Equal("RecordRead", receipt.EventType.Value);
        Assert.Equal("re_00000000000000000000000001", receipt.Target!.Value.RecordId);
        Assert.Equal("re_00000000000000000000000001", receipt.Payload.Payload.Body["recordId"]);

        using var allowed = await _client.GetAsync($"{AssetBase}/entities/re_00000000000000000000000002");
        using var refused = await _client.GetAsync($"{AssetBase}/entities/{hidden.Value}");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Reads_omit_refused_ancestors_and_container_without_redeciding_ids()
    {
        await SeedSelectedTenantSubResourcesAsync();
        var tenant = new TenantId("aaaa0000-0000-0000-0000-0000003580a1");
        var at = new Instant(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var outer = new RegistryEntityId("re_00000000000000000000000005");
        var hidden = new RegistryEntityId("re_00000000000000000000000006");
        var root = new RegistryEntityId("re_00000000000000000000000007");
        var target = new RegistryEntityId("re_00000000000000000000000001");
        foreach (var ancestor in new[] { root, outer, hidden })
            await _repository.UpsertAsync(new RegistryEntity
            {
                Id = ancestor, TenantId = tenant, Type = new EntityTypeId(UnitType), DisplayName = "Ancestor", CreatedAt = at,
            }, at);
        foreach (var (from, to, edge) in new[] { (root, outer, "root-edge"), (outer, hidden, "outer-edge"), (hidden, target, "inner-edge") })
        {
            await _edges.AddAsync(new TypedRelationship
            {
                Id = new TypedRelationshipId(edge), TenantId = tenant, Kind = RelationshipKind.Contains,
                From = from, To = to, EffectiveFrom = at,
            });
        }
        _grantScopes.Add(ScopeExpression.Parse("/records/re_00000000000000000000000005"));
        _grantScopes.Add(ScopeExpression.Parse("/records/re_00000000000000000000000007"));
        string[] expected = ["re_00000000000000000000000007", "re_00000000000000000000000005", "re_00000000000000000000000001"];

        using var detail = await _client.GetAsync($"{AssetBase}/entities/{target.Value}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var body = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("containerId").ValueKind);
        Assert.Equal(expected, body.GetProperty("path").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(1, _authorizationRequests.Count(x => x.Target.RecordId == hidden.Value));
        Assert.Equal(1, _authorizationRequests.Count(x => x.Target.RecordId == target.Value));

        _authorizationRequests.Clear();
        using var tree = await _client.GetAsync($"{AssetBase}/entities/{target.Value}/tree");
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        var treeBody = await tree.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected, treeBody.GetProperty("path").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(1, _authorizationRequests.Count(x => x.Target.RecordId == hidden.Value));
        Assert.Equal(1, _authorizationRequests.Count(x => x.Target.RecordId == target.Value));
    }

    [Fact(DisplayName = "holds T-1080.A6: with every related record refused, no GET route shows one")]
    public async Task No_read_route_shows_a_related_record_the_caller_cannot_open()
    {
        // Oracle: the literal ids and names seeded here, and the T-1080 A2/A3 property that a refused
        // related record is absent from every response. The routes come from the host's endpoint data
        // sources, so a new GET route is driven without a hand list.
        const string addressed = "re_00000000000000000000000010";
        (string Id, string Name)[] related =
        [
            ("re_00000000000000000000000011", "Related parent"),
            ("re_00000000000000000000000012", "Related grandparent"),
            ("re_00000000000000000000000013", "Related child one"),
            ("re_00000000000000000000000014", "Related child two"),
            ("re_00000000000000000000000015", "Related sibling"),
            ("re_00000000000000000000000016", "Related grandchild"),
            ("re_00000000000000000000000017", "Related location"),
            ("re_00000000000000000000000018", "Related occupant"),
            ("re_00000000000000000000000019", "Related system"),
            ("re_00000000000000000000000020", "Related part"),
        ];
        var at = new Instant(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        foreach (var (id, name) in related.Append((addressed, "Addressed unit")))
            await _repository.UpsertAsync(new RegistryEntity
            {
                Id = new RegistryEntityId(id), TenantId = _tenant, Type = new EntityTypeId(UnitType),
                DisplayName = name, CreatedAt = at,
            }, at);
        // Every relationship kind, in both directions from the addressed record.
        (string From, string To, RelationshipKind Kind)[] seeded =
        [
            ("re_00000000000000000000000012", "re_00000000000000000000000011", RelationshipKind.Contains),
            ("re_00000000000000000000000011", addressed, RelationshipKind.Contains),
            ("re_00000000000000000000000011", "re_00000000000000000000000015", RelationshipKind.Contains),
            (addressed, "re_00000000000000000000000013", RelationshipKind.Contains),
            (addressed, "re_00000000000000000000000014", RelationshipKind.Contains),
            ("re_00000000000000000000000013", "re_00000000000000000000000016", RelationshipKind.Contains),
            (addressed, "re_00000000000000000000000017", RelationshipKind.LocatedAt),
            ("re_00000000000000000000000018", addressed, RelationshipKind.LocatedAt),
            (addressed, "re_00000000000000000000000019", RelationshipKind.PartOfSystem),
            ("re_00000000000000000000000020", addressed, RelationshipKind.PartOfSystem),
        ];
        foreach (var (from, to, kind) in seeded)
            await _edges.AddAsync(new TypedRelationship
            {
                Id = new TypedRelationshipId($"fence-{from}-{to}"), TenantId = _tenant, Kind = kind,
                From = new RegistryEntityId(from), To = new RegistryEntityId(to), EffectiveFrom = at,
            });
        _grantScopes.Add(ScopeExpression.Parse("/records/" + addressed));

        var routes = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)_app).DataSources
            .SelectMany(source => source.Endpoints).OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(route => route.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()
                ?.HttpMethods.Contains("GET") == true)
            .ToArray();
        var statuses = new Dictionary<string, HttpStatusCode>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            // A route this fence cannot address fails here; it is never skipped.
            var path = route.RoutePattern.RawText!.Replace("{id}", addressed, StringComparison.Ordinal);
            Assert.DoesNotContain("{", path, StringComparison.Ordinal);
            using var response = await _client.GetAsync(path);
            statuses[route.RoutePattern.RawText] = response.StatusCode;
            var seen = await response.Content.ReadAsStringAsync() + string.Join("\n",
                response.Headers.Concat(response.Content.Headers).SelectMany(header => header.Value.Prepend(header.Key)));
            Assert.All(related, record =>
            {
                Assert.DoesNotContain(record.Id, seen, StringComparison.Ordinal);
                Assert.DoesNotContain(record.Name, seen, StringComparison.Ordinal);
            });
        }
        // The two projecting routes were driven and answered, so the absence above is not a refusal.
        Assert.Equal(HttpStatusCode.OK, statuses["/api/local-node/asset-registry/entities/{id}"]);
        Assert.Equal(HttpStatusCode.OK, statuses["/api/local-node/asset-registry/entities/{id}/tree"]);

        // Control: the same seed shows its related records once the caller may open them.
        _grantScopes.Clear();
        var detail = await _client.GetStringAsync($"{AssetBase}/entities/{addressed}");
        Assert.Contains("re_00000000000000000000000011", detail, StringComparison.Ordinal);
        Assert.Contains("re_00000000000000000000000012", detail, StringComparison.Ordinal);
        var tree = await _client.GetStringAsync($"{AssetBase}/entities/{addressed}/tree");
        Assert.Contains("Related child one", tree, StringComparison.Ordinal);
        Assert.Contains("Related child two", tree, StringComparison.Ordinal);
        Assert.Contains("re_00000000000000000000000012", tree, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selected_tree_reads_its_children_and_edges_when_another_team_is_active()
    {
        await SeedSelectedTenantSubResourcesAsync();
        _grantScopes.Add(ScopeExpression.Parse("/records/re_00000000000000000000000002"));

        using var response = await _client.GetAsync($"{AssetBase}/entities/re_00000000000000000000000001/tree");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("re_00000000000000000000000001", body.GetProperty("container").GetString());
        var child = Assert.Single(body.GetProperty("children").EnumerateArray());
        Assert.Equal("re_00000000000000000000000002", child.GetProperty("id").GetString());
        Assert.Equal("Selected A child", child.GetProperty("displayName").GetString());
        IEnumerable<string> expectedPath = new[] { "re_00000000000000000000000001" };
        var actualPath = body.GetProperty("path").EnumerateArray()
            .Select(item => Assert.IsType<string>(item.GetString()));
        Assert.Equal<string>(expectedPath, actualPath);
        Assert.DoesNotContain("Ambient B", body.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(
            ["aaaa0000-0000-0000-0000-0000003580a1", "aaaa0000-0000-0000-0000-0000003580a1"],
            _repository.ReadTenants.Select(tenant => tenant.Value).ToArray());
        Assert.Equal(
            ["GetContentsAsAtAsync|aaaa0000-0000-0000-0000-0000003580a1",
             "GetContainmentPathAsAtAsync|aaaa0000-0000-0000-0000-0000003580a1"],
            _edges.Reads.ToArray());
        Assert.Empty(_conditions.Reads);
        Assert.Empty(_submissions.Reads);
        Assert.Equal(2, _authorizationRequests.Count);
        Assert.Equal(new[] { "re_00000000000000000000000001", "re_00000000000000000000000002" },
            _authorizationRequests.Select(request => request.Target.RecordId));
        AssertSelectedRecordAuthorization(_authorizationRequests[0],
            "re_00000000000000000000000001", "/records/re_00000000000000000000000001");
        AssertSelectedRecordAuthorization(_authorizationRequests[1],
            "re_00000000000000000000000002", "/records/re_00000000000000000000000002");
    }

    [Fact]
    public async Task Selected_condition_reads_its_history_when_another_team_is_active()
    {
        await SeedSelectedTenantSubResourcesAsync();

        using var response = await _client.GetAsync($"{AssetBase}/entities/re_00000000000000000000000001/condition");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("re_00000000000000000000000001", body.GetProperty("entity").GetString());
        var assessment = Assert.Single(body.GetProperty("history").EnumerateArray());
        Assert.Equal("selected-a-assessment", assessment.GetProperty("id").GetString());
        Assert.Equal(4, assessment.GetProperty("grade").GetInt32());
        Assert.Equal("Selected A condition", assessment.GetProperty("observations").GetString());
        Assert.DoesNotContain("Ambient B", body.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("aaaa0000-0000-0000-0000-0000003580a1", Assert.Single(_repository.ReadTenants).Value);
        Assert.Equal(["GetHistoryAsync|aaaa0000-0000-0000-0000-0000003580a1"], _conditions.Reads.ToArray());
        Assert.Empty(_edges.Reads);
        Assert.Empty(_submissions.Reads);
        AssertSelectedRecordAuthorization();
    }

    [Fact]
    public async Task Selected_submissions_read_its_links_when_another_team_is_active()
    {
        await SeedSelectedTenantSubResourcesAsync();

        using var response = await _client.GetAsync($"{AssetBase}/entities/re_00000000000000000000000001/submissions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("re_00000000000000000000000001", body.GetProperty("entity").GetString());
        var submission = Assert.Single(body.GetProperty("submissions").EnumerateArray());
        Assert.Equal("selected-a-instance", submission.GetProperty("instanceId").GetString());
        Assert.Equal("selected-a-form", submission.GetProperty("formId").GetString());
        Assert.Equal("Selected A submitter", submission.GetProperty("assessorRef").GetString());
        Assert.DoesNotContain("Ambient B", body.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("aaaa0000-0000-0000-0000-0000003580a1", Assert.Single(_repository.ReadTenants).Value);
        Assert.Equal(["ListForEntityAsync|aaaa0000-0000-0000-0000-0000003580a1"], _submissions.Reads.ToArray());
        Assert.Empty(_edges.Reads);
        Assert.Empty(_conditions.Reads);
        AssertSelectedRecordAuthorization();
    }

    [Theory]
    [InlineData("aaaa0000-0000-0000-0000-0000003580a1", "re_00000000000000000000000001",
        "/records/re_00000000000000000000000001", AuthorizationVerdict.Allowed)]
    [InlineData("aaaa0000-0000-0000-0000-0000003580a1", "re_00000000000000000000000003",
        "/records/re_00000000000000000000000003", AuthorizationVerdict.Denied)]
    [InlineData("bbbb0000-0000-0000-0000-0000003580b1", "re_00000000000000000000000001",
        "/records/re_00000000000000000000000001", AuthorizationVerdict.Denied)]
    public async Task Selected_fixture_grant_is_fixed_to_the_literal_tenant_and_record(
        string tenant, string recordId, string scopeText, AuthorizationVerdict expected)
    {
        await SeedSelectedTenantSubResourcesAsync();
        var scope = ScopeExpression.Parse(scopeText);
        var request = new AuthorizationGateRequest(
            new PermissionAtom(AuthorizationOperation.Parse("records:read"), scope),
            new ActorId("selected-holder"), new TenantId(tenant),
            new AuthorizationTarget("record", recordId, scope),
            AdmittedInstant.FromRecordedAct(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var decision = await _gate.DecideAsync(request);

        Assert.Equal(expected, decision.Verdict);
        var observed = Assert.Single(_authorizationRequests);
        Assert.Equal(tenant, observed.Tenant.Value);
        Assert.Equal(recordId, observed.Target.RecordId);
        Assert.Empty(_repository.ReadTenants);
        Assert.Empty(_edges.Reads);
        Assert.Empty(_conditions.Reads);
        Assert.Empty(_submissions.Reads);
    }

    private void AssertSelectedRecordAuthorization()
    {
        AssertSelectedRecordAuthorization(Assert.Single(_authorizationRequests),
            "re_00000000000000000000000001", "/records/re_00000000000000000000000001");
    }

    private static void AssertSelectedRecordAuthorization(AuthorizationGateRequest request, string recordId, string scope)
    {
        Assert.Equal("aaaa0000-0000-0000-0000-0000003580a1", request.Tenant.Value);
        Assert.Equal("selected-holder", request.Principal.Value);
        Assert.Equal("records:read", request.Act.Operation.Value);
        Assert.Equal("record", request.Target.RecordKind);
        Assert.Equal(recordId, request.Target.RecordId);
        Assert.Equal(scope, request.Target.Scope.Value);
    }

    private async Task SeedSelectedTenantSubResourcesAsync()
    {
        // F1 oracle: two independent literal tenant datasets share the addressed parent id. A wrong
        // ambient-tenant store call can therefore return convincing B data, rather than just a miss.
        var at = new Instant(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var parent = new RegistryEntityId("re_00000000000000000000000001");
        foreach (var (tenant, child, label, prefix, grade) in new[]
        {
            (new TenantId("aaaa0000-0000-0000-0000-0000003580a1"),
                new RegistryEntityId("re_00000000000000000000000002"), "Selected A", "selected-a", 4),
            (new TenantId("bbbb0000-0000-0000-0000-0000003580b1"),
                new RegistryEntityId("re_00000000000000000000000003"), "Ambient B", "ambient-b", 1),
        })
        {
            await _repository.UpsertAsync(new RegistryEntity
            {
                Id = parent, TenantId = tenant, Type = new EntityTypeId(UnitType),
                DisplayName = label + " parent", CreatedAt = at,
            }, at);
            await _repository.UpsertAsync(new RegistryEntity
            {
                Id = child, TenantId = tenant, Type = new EntityTypeId(UnitType),
                DisplayName = label + " child", CreatedAt = at,
            }, at);
            await _edges.AddAsync(new TypedRelationship
            {
                Id = new TypedRelationshipId(prefix + "-edge"), TenantId = tenant,
                Kind = RelationshipKind.Contains, From = parent, To = child, EffectiveFrom = at,
            });
            await _conditions.RecordAsync(new ConditionAssessment
            {
                Id = new ConditionAssessmentId(prefix + "-assessment"), TenantId = tenant,
                Entity = parent, Rating = new ConditionRating(grade), ObservedAt = at,
                Observations = label + " condition",
            });
            await _submissions.RecordAsync(new FormSubmissionRecord
            {
                Id = new FormSubmissionRecordId(prefix + "-submission"), TenantId = tenant,
                Entity = parent, InstanceId = prefix + "-instance", FormId = prefix + "-form",
                SubmittedAt = at, AssessorRef = label + " submitter",
            });
        }

        _activeTeam.Active = TeamContextFor(TeamB, "Ambient B");
        _selected = new SelectedSessionRequestPrincipal("selected-account",
            new TenantId("aaaa0000-0000-0000-0000-0000003580a1"),
            new PrincipalUserId("selected-holder"), new CanonicalPartyReference("selected-party"),
            "membership", 1, [new PinnedGrantOwnerVersion("fixture", 1)], 1, "session", "coordination");
        _repository.ReadTenants.Clear();
        _edges.Reads.Clear();
        _conditions.Reads.Clear();
        _submissions.Reads.Clear();
        _authorizationRequests.Clear();
        _grantScopes.Add(ScopeExpression.Parse("/records/re_00000000000000000000000001"));
        _pinSelectedTenant = true;
    }

    private async Task AssertRefusedAsync(HttpResponseMessage response, string entityId)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            AuthorizationRefusalRenderer.PermissionRequiredCode, body.GetProperty("code").GetString());
        Assert.Equal(TeamRolePermissions.RecordsRead, body.GetProperty("permission").GetString());
        // No body echo: neither the addressed record nor the tenant reaches an unauthorized caller.
        Assert.DoesNotContain(entityId, body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(_tenant.Value, body.GetRawText(), StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<AuditRecord>> RefusalRowsAsync()
    {
        var rows = new List<AuditRecord>();
        await foreach (var record in _app.Services.GetRequiredService<IAuditTrail>()
                           .QueryAsync(new AuditQuery(_tenant)))
        {
            if (record.EventType.Equals(AuthorizationRefusalAudit.AuthorizationRefusedEventType))
                rows.Add(record);
        }

        return rows;
    }

    private async Task<string> CreateUnitAsync(string displayName)
    {
        var created = await _client.PostAsJsonAsync($"{AssetBase}/entities", new { type = UnitType, displayName });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static TeamContext TeamContextFor(TeamId teamId, string name)
        => new(teamId, name, new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private sealed class ReadTrackingRepository(IRegistryEntityRepository inner) : IRegistryEntityRepository
    {
        public List<TenantId> ReadTenants { get; } = [];
        public List<string> ReadIds { get; } = [];
        public Task<RegistryEntity?> GetByIdAsync(TenantId tenant, RegistryEntityId id, CancellationToken cancellationToken = default)
        {
            ReadTenants.Add(tenant);
            ReadIds.Add(id.Value);
            return inner.GetByIdAsync(tenant, id, cancellationToken);
        }
        public Task<IReadOnlyList<RegistryEntity>> ListByTenantAsync(TenantId tenant, bool includeRetired = false, CancellationToken cancellationToken = default)
            => inner.ListByTenantAsync(tenant, includeRetired, cancellationToken);
        public Task<IReadOnlyList<RegistryEntity>> ListByTypeAsync(TenantId tenant, EntityTypeId type, bool includeRetired = false, CancellationToken cancellationToken = default)
            => inner.ListByTypeAsync(tenant, type, includeRetired, cancellationToken);
        public Task UpsertAsync(RegistryEntity entity, Instant at, string? actorRef = null, CancellationToken cancellationToken = default)
            => inner.UpsertAsync(entity, at, actorRef, cancellationToken);
        public Task RetireAsync(TenantId tenant, RegistryEntityId id, Instant at, string? actorRef = null, CancellationToken cancellationToken = default)
            => inner.RetireAsync(tenant, id, at, actorRef, cancellationToken);
    }

    private sealed class ReadTrackingEdges(ITypedRelationshipStore inner) : ITypedRelationshipStore
    {
        public List<string> Reads { get; } = [];
        public int MaxContainmentDepth => inner.MaxContainmentDepth;
        public Task<TypedRelationship> AddAsync(TypedRelationship edge, string? actorRef = null, CancellationToken cancellationToken = default)
            => inner.AddAsync(edge, actorRef, cancellationToken);
        public Task CloseAsync(TenantId tenant, TypedRelationshipId id, Instant effectiveTo, string? actorRef = null, CancellationToken cancellationToken = default)
            => inner.CloseAsync(tenant, id, effectiveTo, actorRef, cancellationToken);
        public Task<TypedRelationship?> GetByIdAsync(TenantId tenant, TypedRelationshipId id, CancellationToken cancellationToken = default)
        {
            Reads.Add("GetByIdAsync|" + tenant.Value);
            return inner.GetByIdAsync(tenant, id, cancellationToken);
        }
        public Task<IReadOnlyList<TypedRelationship>> GetEdgesAsAtAsync(TenantId tenant, RegistryEntityId entity, RelationshipKind? kind, Instant asOf, CancellationToken cancellationToken = default)
        {
            Reads.Add("GetEdgesAsAtAsync|" + tenant.Value);
            return inner.GetEdgesAsAtAsync(tenant, entity, kind, asOf, cancellationToken);
        }
        public Task<RegistryEntityId?> GetContainerAsAtAsync(TenantId tenant, RegistryEntityId child, Instant asOf, CancellationToken cancellationToken = default)
        {
            Reads.Add("GetContainerAsAtAsync|" + tenant.Value);
            return inner.GetContainerAsAtAsync(tenant, child, asOf, cancellationToken);
        }
        public Task<IReadOnlyList<RegistryEntityId>> GetContentsAsAtAsync(TenantId tenant, RegistryEntityId container, Instant asOf, CancellationToken cancellationToken = default)
        {
            Reads.Add("GetContentsAsAtAsync|" + tenant.Value);
            return inner.GetContentsAsAtAsync(tenant, container, asOf, cancellationToken);
        }
        public Task<IReadOnlyList<RegistryEntityId>> GetContainmentPathAsAtAsync(TenantId tenant, RegistryEntityId entity, Instant asOf, CancellationToken cancellationToken = default)
        {
            Reads.Add("GetContainmentPathAsAtAsync|" + tenant.Value);
            return inner.GetContainmentPathAsAtAsync(tenant, entity, asOf, cancellationToken);
        }
        public Task<RegistryEntityId?> GetLocationAsAtAsync(TenantId tenant, RegistryEntityId entity, Instant asOf, CancellationToken cancellationToken = default)
        {
            Reads.Add("GetLocationAsAtAsync|" + tenant.Value);
            return inner.GetLocationAsAtAsync(tenant, entity, asOf, cancellationToken);
        }
    }

    private sealed class ReadTrackingConditions(IConditionAssessmentStore inner) : IConditionAssessmentStore
    {
        public List<string> Reads { get; } = [];
        public Task RecordAsync(ConditionAssessment assessment, string? actorRef = null, CancellationToken cancellationToken = default)
            => inner.RecordAsync(assessment, actorRef, cancellationToken);
        public Task<ConditionAssessment?> GetByIdAsync(TenantId tenant, ConditionAssessmentId id, CancellationToken cancellationToken = default)
        {
            Reads.Add("GetByIdAsync|" + tenant.Value);
            return inner.GetByIdAsync(tenant, id, cancellationToken);
        }
        public Task<IReadOnlyList<ConditionAssessment>> GetHistoryAsync(TenantId tenant, RegistryEntityId entity, Instant? asOf = null, CancellationToken cancellationToken = default)
        {
            Reads.Add("GetHistoryAsync|" + tenant.Value);
            return inner.GetHistoryAsync(tenant, entity, asOf, cancellationToken);
        }
        public Task<ConditionAssessment?> GetLatestAsAtAsync(TenantId tenant, RegistryEntityId entity, Instant asOf, CancellationToken cancellationToken = default)
        {
            Reads.Add("GetLatestAsAtAsync|" + tenant.Value);
            return inner.GetLatestAsAtAsync(tenant, entity, asOf, cancellationToken);
        }
    }

    private sealed class ReadTrackingSubmissions(IFormSubmissionRecordStore inner) : IFormSubmissionRecordStore
    {
        public List<string> Reads { get; } = [];
        public Task RecordAsync(FormSubmissionRecord record, string? actorRef = null, CancellationToken cancellationToken = default)
            => inner.RecordAsync(record, actorRef, cancellationToken);
        public Task<IReadOnlyList<FormSubmissionRecord>> ListForEntityAsync(TenantId tenant, RegistryEntityId entity, CancellationToken cancellationToken = default)
        {
            Reads.Add("ListForEntityAsync|" + tenant.Value);
            return inner.ListForEntityAsync(tenant, entity, cancellationToken);
        }
    }

    /// <summary>A mutable active-team accessor so a test can switch the active team mid-flight.</summary>
    private sealed class MutableActiveTeamAccessor(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; set; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
