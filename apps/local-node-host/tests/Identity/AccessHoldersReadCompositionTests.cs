using System.Text.Json;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.RuleEngine.Standings;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class AccessHoldersReadCompositionTests
{
    private static readonly TenantId Tenant = new("29400000-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("resolved")]
    [InlineData("missing")]
    [InlineData("tombstoned")]
    [InlineData("detached")]
    [InlineData("duplicated")]
    [InlineData("wrong-tenant")]
    public async Task Mapped_read_projects_each_grant_and_attribution_across_restart(string binding)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ticket329-holders-{Guid.NewGuid():N}");
        try
        {
            for (var boot = 0; boot < 2; boot++)
            {
                await using var host = await UnattributedGrantCompositionTests.OpenAsync(directory);
                if (boot == 0)
                {
                    await UnattributedGrantCompositionTests.SeedAsync(host.Services, binding);
                    await host.Services.GetRequiredService<IGrantStore>().AppendAsync(Tenant, new AccessGrant(
                        new GrantId(Guid.Parse("32900000-0000-4000-8000-000000000001")), Tenant,
                        new ActorId("principal-target"), AccessGrantAuthorizationSeed.MemberRole,
                        ScopeExpression.Parse("/records/example"), GrantResidency.Cache,
                        new GrantValidity(Now.AddDays(-2), Now.AddDays(3)), GranterKind.Person,
                        new ActorId("principal-other-granter"), Now.AddDays(-2),
                        new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual),
                            new ActorId("principal-other-granter")), Now.AddDays(-2)));
                }
                var (status, body) = await ReadAsync(host.Services, "principal-admin");
                using var json = JsonDocument.Parse(body);
                Assert.Equal(200, status);
                var rows = json.RootElement.GetProperty("holders").EnumerateArray().ToArray();
                var target = Assert.Single(rows, row => row.GetProperty("grantId").GetString() ==
                    "29400000-0000-4000-8000-000000000002");
                Assert.Equal(binding == "resolved" ? "party-target" : "UNATTRIBUTED", target.GetProperty("partyId").GetString());
                Assert.Equal(binding == "resolved" ? "grant" : "unattributed", target.GetProperty("source").GetString());
                Assert.Equal(RoleReference.Administrator.Name, target.GetProperty("role").GetProperty("name").GetString());
                Assert.Equal(RoleReference.Administrator.Vocabulary, target.GetProperty("role").GetProperty("vocabulary").GetString());
                Assert.Equal("principal-admin", target.GetProperty("granter").GetString());
                Assert.Equal("/", target.GetProperty("scope").GetString());
                Assert.Equal(Now.AddMinutes(-1), target.GetProperty("effectiveFrom").GetDateTimeOffset());
                Assert.Equal(JsonValueKind.Null, target.GetProperty("effectiveTo").ValueKind);
                if (binding == "resolved") Assert.False(target.TryGetProperty("attributionFailure", out _));
                else Assert.Equal("No unique live party binding in this tenant: missing, tombstoned, detached, duplicated or wrong-tenant.",
                    target.GetProperty("attributionFailure").GetString());
                Assert.Single(rows, row => row.GetProperty("source").GetString() == "roster");
                // One row per grant: two holdings of one unresolved principal must not collapse.
                var expectedIds = (await host.Services.GetRequiredService<IGrantStore>().SnapshotAsync(Tenant))
                    .Where(grant => grant.IsActiveAt(Now)).Select(grant => grant.GrantId.ToString()).Order().ToArray();
                Assert.Equal(expectedIds, rows.Select(row => row.GetProperty("grantId").GetString()).Order().ToArray());
                var scoped = Assert.Single(rows, row => row.GetProperty("scope").GetString() == "/records/example");
                Assert.Equal("principal-other-granter", scoped.GetProperty("granter").GetString());
                Assert.Equal(Now.AddDays(-2), scoped.GetProperty("effectiveFrom").GetDateTimeOffset());
                Assert.Equal(Now.AddDays(3), scoped.GetProperty("effectiveTo").GetDateTimeOffset());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Mapped_read_without_members_atom_refuses_with_the_gate_decision_recorded()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ticket329-denied-{Guid.NewGuid():N}");
        try
        {
            await using var host = await UnattributedGrantCompositionTests.OpenAsync(directory);
            await UnattributedGrantCompositionTests.SeedAsync(host.Services, "resolved");
            var (status, body) = await ReadAsync(host.Services, "principal-without-read-atom");
            Assert.Equal(403, status);
            Assert.DoesNotContain("holders", body);
            var records = new List<AuditRecord>();
            await foreach (var record in host.Services.GetRequiredService<IAuditTrail>().QueryAsync(
                new AuditQuery(Tenant, AuthorizationRefusalAudit.AuthorizationRefusedEventType))) records.Add(record);
            var refusal = Assert.Single(records, record => record.Actor?.Value == "principal-without-read-atom");
            Assert.Equal(TeamRolePermissions.MembersManage, refusal.Act!.Value.Operation.Value);
            using var payload = JsonDocument.Parse(JsonSerializer.Serialize(refusal.Payload.Payload.Body));
            Assert.False(payload.RootElement.GetProperty("preDecision").GetBoolean());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    // Execute the production mapping with Program's actual service provider; no listener or alternate gate.
    private static async Task<(int Status, string Body)> ReadAsync(IServiceProvider services, string principal)
    {
        await using var app = WebApplication.CreateBuilder().Build();
        AuthorizationAdminRoutes.Map(app,
            services.GetRequiredService<IRoleVocabularyReader>(),
            services.GetRequiredService<IAuthorizationDefinitionCatalogueReader>(),
            services.GetRequiredService<AuthorizationDefinitionWriter>(),
            services.GetRequiredService<IStandingRuleDefinitionStore>(),
            services.GetRequiredService<StandingCatalogue>(), new ActiveTenant(services), services.GetRequiredService<TimeProvider>());
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(endpoint => endpoint.RoutePattern.RawText == AccessHoldersRead.Route);
        using var body = new MemoryStream();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Features.Set(new SelectedSessionRequestPrincipal("account-admin", Tenant,
            new PrincipalUserId(principal), new CanonicalPartyReference("party-admin"),
            "membership", 1, [new PinnedGrantOwnerVersion("grant-fixture", 1)], 1, "session", "coordination"));
        http.Response.Body = body;
        await endpoint.RequestDelegate!(http);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        return (http.Response.StatusCode, System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    private sealed class ActiveTenant(IServiceProvider services) : IActiveTeamAccessor
    {
        public TeamContext? Active => new(new TeamId(Guid.Parse(Tenant.Value)), "Access test", services, services.GetRequiredService<TimeProvider>());
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
#pragma warning disable CS0067
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
#pragma warning restore CS0067
    }
}
