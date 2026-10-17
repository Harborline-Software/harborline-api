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
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

public sealed class AssetRegistrySubresourceAuditReceiptTests
{
    [Theory]
    [InlineData("tree", false)]
    [InlineData("condition", false)]
    [InlineData("submissions", false)]
    [InlineData("tree", true)]
    [InlineData("condition", true)]
    [InlineData("submissions", true)]
    public async Task Allowed_subresource_read_returns_its_one_record_receipt_and_optional_selected_correlation(
        string subResource, bool correlated)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Selected = correlated;
        using var request = fixture.Request(subResource, correlated);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("record-1079", body.GetProperty(subResource == "tree" ? "container" : "entity").GetString());
        Assert.Empty(body.GetProperty(subResource switch
        {
            "tree" => "children", "condition" => "history", _ => "submissions",
        }).EnumerateArray());
        var id = Guid.Parse(Assert.Single(response.Headers.GetValues("X-Harborline-Audit-Id")));
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(id, body.GetProperty("auditId").GetGuid());
        var rows = await fixture.RowsAsync();
        var row = Assert.Single(rows);
        Assert.Equal("RecordRead", row.EventType.Value);
        Assert.Equal(id, row.AuditId);
        Assert.Equal("record-1079", row.Target!.Value.RecordId);
        Assert.Equal("records:read", row.Act!.Value.Operation.Value);
        Assert.Equal("10790000-0000-0000-0000-000000000001", row.TenantId.Value);
        Assert.Equal(correlated ? "selected-holder" : "desktop-operator-under-test", row.Actor!.Value.Value);
        Assert.Equal("record-1079", row.Payload.Payload.Body["recordId"]);
        Assert.Equal(subResource, row.Payload.Payload.Body["subResource"]);
        Assert.NotNull(row.AuthoritySnapshot);
        var decision = Assert.Single(fixture.Requests);
        Assert.Equal("record-1079", decision.Target.RecordId);
        Assert.Equal("records:read", decision.Act.Operation.Value);
        Assert.Equal(row.OccurredAt, decision.At);
        if (correlated)
        {
            Assert.Equal("43300000-0000-4000-8000-000000000021", row.Payload.Payload.Body["correlation_id"]);
            Assert.Equal("43300000-0000-4000-8000-000000000021",
                Assert.Single(response.Headers.GetValues("X-Harborline-Audit-Correlation")));
            Assert.Equal("43300000-0000-4000-8000-000000000021", body.GetProperty("correlationId").GetString());
            Assert.Equal(Guid.Parse("43300000-0000-4000-8000-000000000021"), decision.CorrelationId);
        }
        else
        {
            Assert.False(row.Payload.Payload.Body.ContainsKey("correlation_id"));
            Assert.False(response.Headers.Contains("X-Harborline-Audit-Correlation"));
            Assert.False(body.TryGetProperty("correlationId", out _));
            Assert.Null(decision.CorrelationId);
        }
    }

    [Theory]
    [InlineData("tree")]
    [InlineData("condition")]
    [InlineData("submissions")]
    public async Task Denied_subresource_read_keeps_the_typed_refusal_and_has_no_RecordRead(string subResource)
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.AllowRead = false;
        using var request = fixture.Request(subResource, false);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        Assert.Equal("records:read", body.GetProperty("permission").GetString());
        var rows = await fixture.RowsAsync();
        Assert.DoesNotContain(rows, row => row.EventType.Value == "RecordRead");
        var refusal = Assert.Single(rows);
        Assert.Equal("AuthorizationRefused", refusal.EventType.Value);
        Assert.Equal(refusal.AuditId, body.GetProperty("auditId").GetGuid());
        Assert.Equal(refusal.AuditId.ToString("D"), Assert.Single(response.Headers.GetValues("X-Harborline-Audit-Id")));
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData("tree", false)]
    [InlineData("condition", false)]
    [InlineData("submissions", false)]
    [InlineData("tree", true)]
    [InlineData("condition", true)]
    [InlineData("submissions", true)]
    public async Task Missing_or_faulted_audit_sink_returns_no_fabricated_receipt(string subResource, bool fault)
    {
        await using var fixture = await Fixture.StartAsync(includeAudit: fault, faultAudit: fault);
        fixture.Selected = true;
        using var request = fixture.Request(subResource, true);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("auditId", out _));
        Assert.False(body.TryGetProperty("correlationId", out _));
        Assert.False(response.Headers.Contains("X-Harborline-Audit-Id"));
        Assert.False(response.Headers.Contains("X-Harborline-Audit-Correlation"));
        Assert.Empty(await fixture.RowsAsync());
        Assert.Single(fixture.Requests);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly TeamId Team = new(Guid.Parse("10790000-0000-0000-0000-000000000001"));
        private static readonly TenantId Tenant = new("10790000-0000-0000-0000-000000000001");
        private WebApplication _app = null!;
        private ServiceProvider _teamServices = null!;
        public HttpClient Client { get; private set; } = null!;
        public List<AuthorizationGateRequest> Requests { get; } = [];
        public bool Selected { get; set; }
        public bool AllowRead { get; set; } = true;

        public static async Task<Fixture> StartAsync(bool includeAudit = true, bool faultAudit = false)
        {
            var fixture = new Fixture();
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
            var source = new TestRouteGate.ScopedGrantSource(request =>
            {
                fixture.Requests.Add(request);
                return fixture.AllowRead || request.Act.Operation.Value != "records:read";
            }, []);
            builder.Services.AddSingleton(new AuthorizationGate(source, new EmptyRecordStandingResolver(), source));
            builder.Services.AddAuthorizationRefusalAudit();
            if (includeAudit) builder.Services.AddAuthorizedActAudit();
            if (faultAudit) builder.Services.AddSingleton<IAuthorizedAuditTrail>(services =>
                new FaultingTrail(services.GetRequiredService<InMemoryAuditTrail>()));
            fixture._teamServices = new ServiceCollection().BuildServiceProvider();
            var active = new FixedActiveTeam(new TeamContext(Team, "Team1079", fixture._teamServices, TimeProvider.System));
            fixture._app = builder.Build();
            var types = fixture._app.Services.GetRequiredService<IEntityTypeRegistry>();
            types.SeedType(new EntityTypeSeed(new EntityTypeId("container"),
                new EntityTypeDescriptor("Container", EntityTrait.Container), CascadeLayer.Pack));
            var entities = fixture._app.Services.GetRequiredService<IRegistryEntityRepository>();
            await entities.UpsertAsync(new RegistryEntity
            {
                Id = new RegistryEntityId("record-1079"), TenantId = Tenant, Type = new EntityTypeId("container"),
                DisplayName = "Empty container", CreatedAt = new Instant(DateTimeOffset.UnixEpoch),
            }, new Instant(DateTimeOffset.UnixEpoch), "fixture:seed");
            fixture._app.Use(async (http, next) =>
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
                if (fixture.Selected) http.Features.Set(new SelectedSessionRequestPrincipal("account", Tenant,
                    new PrincipalUserId("selected-holder"), new CanonicalPartyReference("party"),
                    "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination"));
                await next(http);
            });
            AssetRegistryRoutes.Map(fixture._app.MapDeviceReachableProductDataGroup(), types, entities,
                fixture._app.Services.GetRequiredService<ITypedRelationshipStore>(),
                fixture._app.Services.GetRequiredService<IConditionAssessmentStore>(),
                fixture._app.Services.GetRequiredService<IFormSubmissionRecordStore>(), active, TimeProvider.System);
            await fixture._app.StartAsync();
            var addresses = fixture._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            fixture.Client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.Single()) };
            fixture.Requests.Clear();
            return fixture;
        }

        public HttpRequestMessage Request(string subResource, bool correlated)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/local-node/asset-registry/entities/record-1079/" + subResource);
            if (correlated) request.Headers.Add("X-Correlation-ID", "43300000-0000-4000-8000-000000000021");
            return request;
        }

        public async Task<List<AuditRecord>> RowsAsync()
        {
            var rows = new List<AuditRecord>();
            await foreach (var row in _app.Services.GetRequiredService<IAuditTrail>().QueryAsync(new AuditQuery(Tenant)))
                rows.Add(row);
            return rows;
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            if (_app is not null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
            if (_teamServices is not null) await _teamServices.DisposeAsync();
        }
    }

    private sealed class FixedActiveTeam(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed class FaultingTrail(IAuditTrail inner) : IAuthorizedAuditTrail
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) => inner.AppendAsync(record, ct);
        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default) => inner.QueryAsync(query, ct);
        public ValueTask AppendAuthorizedAsync(AuditRecord record, AuthorizationDecision decision, CancellationToken ct = default,
            SeparationOfDutyDecision? approval = null) => throw new InvalidOperationException("fixture append failure");
    }
}
