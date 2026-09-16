using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
    private ReadTrackingRepository _repository = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });
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
        builder.Services.AddSingleton(TestRouteGate.Following(operation =>
            _holdsRecordsRead || operation != TeamRolePermissions.RecordsRead));
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
            await next(http);
        });
        _repository = new ReadTrackingRepository(_app.Services.GetRequiredService<IRegistryEntityRepository>());
        AssetRegistryRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            _app.Services.GetRequiredService<IEntityTypeRegistry>(),
            _repository,
            _app.Services.GetRequiredService<ITypedRelationshipStore>(),
            _app.Services.GetRequiredService<IConditionAssessmentStore>(),
            _app.Services.GetRequiredService<IFormSubmissionRecordStore>(),
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
        public Task<RegistryEntity?> GetByIdAsync(TenantId tenant, RegistryEntityId id, CancellationToken cancellationToken = default)
        {
            ReadTenants.Add(tenant);
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

    /// <summary>A mutable active-team accessor so a test can switch the active team mid-flight.</summary>
    private sealed class MutableActiveTeamAccessor(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; set; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
