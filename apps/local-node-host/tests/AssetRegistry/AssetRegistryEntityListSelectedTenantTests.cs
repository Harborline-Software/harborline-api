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
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

/// <summary>T-1081: literal response, repository and decision oracles for both entity-list branches.</summary>
public sealed class AssetRegistryEntityListSelectedTenantTests : IAsyncLifetime
{
    private const string ListRoute = "/api/local-node/asset-registry/entities";
    private const string TenantA = "aaaa1081-0000-0000-0000-0000000000a1";
    private const string TenantB = "bbbb1081-0000-0000-0000-0000000000b1";
    private const string Correlation = "10810000-0000-4000-8000-000000000001";
    private readonly List<string> _events = [];
    private readonly List<AuthorizationGateRequest> _decisions = [];
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private ReadTrackingRepository _repository = null!;
    private SelectedSessionRequestPrincipal? _selected;
    private string? _boundCaller;
    private bool _allowRead = true;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTestDesktopOperator();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        builder.Services.AddTestAuthorizationGate().AddTestNodeForms();
        builder.Services.AddNodeAssetRegistry();
        // Observe the real gate's request without replacing its decision algorithm.
        var source = new TestRouteGate.ScopedGrantSource(request =>
        {
            _decisions.Add(request);
            _events.Add("authorize");
            return _allowRead;
        }, []);
        builder.Services.AddSingleton(new AuthorizationGate(source, new EmptyRecordStandingResolver(), source));
        builder.Services.AddAuthorizationRefusalAudit();
        _app = builder.Build();
        _repository = new ReadTrackingRepository(
            _app.Services.GetRequiredService<IRegistryEntityRepository>(), _events);
        var at = new Instant(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        foreach (var (tenant, id, type, name) in new[]
        {
            (TenantA, "re_00000000000000000000000001", "residential-unit", "Active A unit"),
            (TenantB, "re_00000000000000000000000002", "residential-unit", "Selected B unit"),
            (TenantA, "re_00000000000000000000000003", "plant-equipment", "Active A equipment"),
            (TenantB, "re_00000000000000000000000004", "plant-equipment", "Selected B equipment"),
        })
            await _repository.UpsertAsync(new RegistryEntity
            {
                Id = new RegistryEntityId(id), TenantId = new TenantId(tenant), Type = new EntityTypeId(type),
                DisplayName = name, CreatedAt = at,
            }, at);

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            if (_selected is not null) http.Features.Set(_selected);
            using IDisposable? caller = _boundCaller switch
            {
                "web" => NodeCallerAttributionScope.Enter(new NodeCallerAttribution(
                    "bound-party", "membership", 1, "session", "coordination", 1)),
                "device" => NodeCallerAttributionScope.EnterDevice("device", TenantA, "device-principal"),
                _ => _selected is not null ? NodeCallerAttributionScope.Enter(NodeCallerAttribution.From(_selected)) : null,
            };
            await next(http);
        });
        AssetRegistryRoutes.Map(_app.MapDeviceReachableProductDataGroup(),
            _app.Services.GetRequiredService<IEntityTypeRegistry>(), _repository,
            _app.Services.GetRequiredService<ITypedRelationshipStore>(),
            _app.Services.GetRequiredService<IConditionAssessmentStore>(),
            _app.Services.GetRequiredService<IFormSubmissionRecordStore>(),
            new ActiveTeam(new TeamContext(new TeamId(Guid.Parse(TenantA)), "Active A",
                new ServiceCollection().BuildServiceProvider(), TimeProvider.System)), TimeProvider.System);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A1_selected_B_lists_only_B_and_passes_B_and_correlation_to_the_decision(bool byType)
    {
        SelectB();
        using var request = new HttpRequestMessage(HttpMethod.Get, Route(byType));
        request.Headers.Add("X-Correlation-ID", Correlation);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(byType
                ? new[] { "re_00000000000000000000000002|Selected B unit|residential-unit" }
                : new[] { "re_00000000000000000000000002|Selected B unit|residential-unit",
                          "re_00000000000000000000000004|Selected B equipment|plant-equipment" },
            await RowsAsync(response));
        Assert.Equal(byType ? "ListByTypeAsync|bbbb1081-0000-0000-0000-0000000000b1|residential-unit"
            : "ListByTenantAsync|bbbb1081-0000-0000-0000-0000000000b1|", Assert.Single(_repository.Reads));
        Assert.Equal(byType ? new[] { "authorize", "ListByTypeAsync" } : new[] { "authorize", "ListByTenantAsync" }, _events);
        var decision = Assert.Single(_decisions);
        Assert.Equal("bbbb1081-0000-0000-0000-0000000000b1", decision.Tenant.Value);
        Assert.Equal("selected-holder", decision.Principal.Value);
        Assert.Equal("records:read", decision.Act.Operation.Value);
        Assert.Equal(Guid.Parse("10810000-0000-4000-8000-000000000001"), decision.CorrelationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A2_unresolved_selected_cookie_returns_401_without_a_decision_or_repository_read(bool byType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Route(byType));
        request.Headers.Add("Cookie", "__Host-hl-selected=unresolved");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(_repository.Reads);
        Assert.Empty(_decisions);
        Assert.Empty(_events);
    }

    [Theory]
    [InlineData("web", false)]
    [InlineData("web", true)]
    [InlineData("device", false)]
    [InlineData("device", true)]
    public async Task A_bound_caller_without_a_selected_session_returns_401_before_deciding_or_reading(string caller, bool byType)
    {
        _boundCaller = caller;
        using var response = await _client.GetAsync(Route(byType));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_repository.Reads);
        Assert.Empty(_decisions);
        Assert.Empty(_events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_invalid_correlation_is_refused_before_the_gate_and_repository(bool byType)
    {
        SelectB();
        foreach (var values in new[]
        {
            new[] { "not-a-guid" },
            new[] { "00000000-0000-0000-0000-000000000000" },
            new[] { Correlation, "10810000-0000-4000-8000-000000000002" },
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Route(byType));
            Assert.True(request.Headers.TryAddWithoutValidation("X-Correlation-ID", values));
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("request.correlation_invalid", body.GetProperty("code").GetString());
            Assert.Empty(_repository.Reads);
            Assert.Empty(_decisions);
            Assert.Empty(_events);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_correlation_is_optional_but_the_tenant_and_permission_are_still_bound(bool byType)
    {
        SelectB();
        using var response = await _client.GetAsync(Route(byType));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var decision = Assert.Single(_decisions);
        Assert.Equal("bbbb1081-0000-0000-0000-0000000000b1", decision.Tenant.Value);
        Assert.Equal("records:read", decision.Act.Operation.Value);
        Assert.Null(decision.CorrelationId);
        Assert.Equal(byType ? "ListByTypeAsync|bbbb1081-0000-0000-0000-0000000000b1|residential-unit"
            : "ListByTenantAsync|bbbb1081-0000-0000-0000-0000000000b1|", Assert.Single(_repository.Reads));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_read_without_the_grant_still_refuses_before_any_repository_read(bool byType)
    {
        SelectB();
        _allowRead = false;
        using var response = await _client.GetAsync(Route(byType));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        Assert.Equal("records:read", body.GetProperty("permission").GetString());
        Assert.Equal("bbbb1081-0000-0000-0000-0000000000b1", Assert.Single(_decisions).Tenant.Value);
        Assert.Empty(_repository.Reads);
        Assert.Equal(["authorize"], _events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A3_desktop_without_a_selected_session_keeps_the_active_A_list_and_ignores_selected_correlation(bool byType)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Route(byType));
        request.Headers.Add("X-Correlation-ID", "not-a-selected-correlation");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(byType
                ? new[] { "re_00000000000000000000000001|Active A unit|residential-unit" }
                : new[] { "re_00000000000000000000000001|Active A unit|residential-unit",
                          "re_00000000000000000000000003|Active A equipment|plant-equipment" },
            await RowsAsync(response));
        Assert.Equal(byType ? "ListByTypeAsync|aaaa1081-0000-0000-0000-0000000000a1|residential-unit"
            : "ListByTenantAsync|aaaa1081-0000-0000-0000-0000000000a1|", Assert.Single(_repository.Reads));
        Assert.Equal(byType ? new[] { "authorize", "ListByTypeAsync" } : new[] { "authorize", "ListByTenantAsync" }, _events);
        var decision = Assert.Single(_decisions);
        Assert.Equal("aaaa1081-0000-0000-0000-0000000000a1", decision.Tenant.Value);
        Assert.Equal("desktop-operator-under-test", decision.Principal.Value);
        Assert.Equal("records:read", decision.Act.Operation.Value);
        Assert.Null(decision.CorrelationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A3_desktop_without_the_grant_keeps_the_existing_refusal_before_the_list(bool byType)
    {
        _allowRead = false;
        using var response = await _client.GetAsync(Route(byType));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        Assert.Equal("records:read", body.GetProperty("permission").GetString());
        Assert.Equal("aaaa1081-0000-0000-0000-0000000000a1", Assert.Single(_decisions).Tenant.Value);
        Assert.Empty(_repository.Reads);
        Assert.Equal(["authorize"], _events);
    }

    private void SelectB() => _selected = new SelectedSessionRequestPrincipal("selected-account", new TenantId(TenantB),
        new PrincipalUserId("selected-holder"), new CanonicalPartyReference("selected-party"),
        "membership", 1, [new PinnedGrantOwnerVersion("fixture", 1)], 1, "session", "coordination");

    private static string Route(bool byType) => byType ? ListRoute + "?type=residential-unit" : ListRoute;

    private static async Task<string[]> RowsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("entities").EnumerateArray()
            .Select(row => row.GetProperty("id").GetString() + "|" + row.GetProperty("displayName").GetString()
                + "|" + row.GetProperty("type").GetString())
            .Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class ReadTrackingRepository(IRegistryEntityRepository inner, List<string> events) : IRegistryEntityRepository
    {
        public List<string> Reads { get; } = [];
        public Task<RegistryEntity?> GetByIdAsync(TenantId tenant, RegistryEntityId id, CancellationToken cancellationToken = default)
        {
            events.Add("GetByIdAsync");
            Reads.Add("GetByIdAsync|" + tenant.Value + "|" + id.Value);
            return inner.GetByIdAsync(tenant, id, cancellationToken);
        }
        public Task<IReadOnlyList<RegistryEntity>> ListByTenantAsync(TenantId tenant, bool includeRetired = false, CancellationToken cancellationToken = default)
        {
            events.Add("ListByTenantAsync");
            Reads.Add("ListByTenantAsync|" + tenant.Value + "|");
            return inner.ListByTenantAsync(tenant, includeRetired, cancellationToken);
        }
        public Task<IReadOnlyList<RegistryEntity>> ListByTypeAsync(TenantId tenant, EntityTypeId type, bool includeRetired = false, CancellationToken cancellationToken = default)
        {
            events.Add("ListByTypeAsync");
            Reads.Add("ListByTypeAsync|" + tenant.Value + "|" + type.Value);
            return inner.ListByTypeAsync(tenant, type, includeRetired, cancellationToken);
        }
        public Task UpsertAsync(RegistryEntity entity, Instant at, string? actorRef = null, CancellationToken cancellationToken = default)
            => inner.UpsertAsync(entity, at, actorRef, cancellationToken);
        public Task RetireAsync(TenantId tenant, RegistryEntityId id, Instant at, string? actorRef = null, CancellationToken cancellationToken = default)
            => inner.RetireAsync(tenant, id, at, actorRef, cancellationToken);
    }

    private sealed class ActiveTeam(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active => active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void KeepEvent() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
