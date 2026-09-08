using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Contracts;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

[Collection("Harborline process environment")]
public sealed class AuthorizationTraceRouteTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_route_returns_fixture_shape_and_reports_volatile_history_after_restart(bool allowed)
    {
        await using var host = await Host.OpenAsync();
        var (id, decision) = await host.RecordAsync(allowed);
        var before = await host.ReadAsync(id);
        AssertShape(before, decision);
        await host.RestartAsync();
        var after = await host.ReadAsync(id);
        Assert.Equal((int)AuthorizationTraceAvailability.NotAvailable, after.GetProperty("availability").GetInt32());
        Assert.Empty(after.GetProperty("steps").EnumerateArray());
    }

    [Fact]
    public async Task Refused_read_renders_no_trace_and_records_the_same_decision_evidence()
    {
        await using var host = await Host.OpenAsync();

        var definitions = await host.Services.GetRequiredService<IAuthorizationDefinitionCatalogueReader>()
            .ListAsync(host.Tenant);
        var definition = Assert.Single(definitions, row => row.Definition.Operation.Value == Permission.AuditRead);
        await host.Services.GetRequiredService<AuthorizationDefinitionWriter>().WriteAsync(
            new NarrowCapabilityRoleBinding(host.Tenant, definition.Definition.DefinitionId, RoleBindingSet.Empty,
                host.Actor, host.Now, new BindingChangeReason("331 refused read")),
            new AuthorizationWriteContext(host.Actor, host.Tenant, host.Now));
        // Reopen the real persisted closure and the listener, rather than trusting an in-memory seed.
        await host.RestartAsync();
        var (id, _) = await host.RecordAsync(false);
        using var response = await host.Client.GetAsync(PathFor(id));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AuthorizationRefusalRenderer.PermissionRequiredCode, body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("steps", out _));
        Assert.False(body.TryGetProperty("counterfactual", out _));
        Assert.False(body.TryGetProperty("diagnostic", out _));
        var refusalId = body.GetProperty("auditId").GetGuid();
        Assert.NotEqual(id, refusalId);
        var records = new List<AuditRecord>();
        await foreach (var row in host.Services.GetRequiredService<IAuditTrail>().QueryAsync(new AuditQuery(host.Tenant)))
            records.Add(row);
        var refusal = Assert.Single(records, row => row.AuditId == refusalId);
        Assert.NotNull(refusal.AuthoritySnapshot);
        var own = await host.ReadAsync(refusalId);
        Assert.Contains("verdict:denied", own.GetProperty("steps")[3].GetProperty("facts").EnumerateArray().Select(f => f.GetString()));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(refusal.AuthoritySnapshot.Trace,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), own.GetProperty("steps")));
        using var missing = await host.Client.GetAsync(PathFor(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        var missingBody = await missing.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var field in new[] { "code", "permission", "title", "detail", "remediation" })
            Assert.Equal(body.GetProperty(field).GetRawText(), missingBody.GetProperty(field).GetRawText());
    }

    [Fact]
    public async Task Binding_write_feedback_addresses_its_recorded_decision_through_real_route()
    {
        await using var host = await Host.OpenAsync();
        var definitions = await host.Services.GetRequiredService<IAuthorizationDefinitionCatalogueReader>().ListAsync(host.Tenant);
        var definition = Assert.Single(definitions, row => row.Definition.Operation.Value == Permission.AuditRead);
        host.Client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await host.Client.PostAsJsonAsync(
            $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions/{definition.Definition.DefinitionId.Value}/binding",
            new NarrowAuthorizationBindingRequest(definition.EffectiveRoles.Roles.Select(r => new RoleReferenceDto(r.Vocabulary, r.Name)).ToArray(),
                "331 feedback receipt"));
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = receipt.GetProperty("auditId").GetGuid();
        var trace = await host.ReadAsync(id);
        Assert.Contains("verdict:allowed", trace.GetProperty("steps")[3].GetProperty("facts").EnumerateArray().Select(f => f.GetString()));
        Assert.Contains(trace.GetProperty("steps")[0].GetProperty("facts").EnumerateArray(),
            fact => fact.GetString()!.StartsWith("act:" + Permission.GrantPermissions + "@/records/"
                + definition.Definition.DefinitionId.Value, StringComparison.Ordinal));
        var grants = Assert.Single(definitions, row => row.Definition.Operation.Value == Permission.GrantPermissions);
        var at = host.Now;
        await host.Services.GetRequiredService<AuthorizationDefinitionWriter>().WriteAsync(
            new NarrowCapabilityRoleBinding(host.Tenant, grants.Definition.DefinitionId, RoleBindingSet.Empty,
                host.Actor, at, new BindingChangeReason("331 refused binding write")),
            new AuthorizationWriteContext(host.Actor, host.Tenant, at));
        host.Client.DefaultRequestHeaders.Remove("Idempotency-Key");
        host.Client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var denied = await host.Client.PostAsJsonAsync(
            $"{AuthorizationAdminRoutes.RouteBase}/capability-definitions/{definition.Definition.DefinitionId.Value}/binding",
            new NarrowAuthorizationBindingRequest([], "331 denied write"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var deniedBody = await denied.Content.ReadFromJsonAsync<JsonElement>();
        var deniedTrace = await host.ReadAsync(deniedBody.GetProperty("auditId").GetGuid());
        Assert.Contains("verdict:denied", deniedTrace.GetProperty("steps")[3].GetProperty("facts").EnumerateArray().Select(f => f.GetString()));
        await host.RestartAsync();
        var restarted = await host.ReadAsync(id);
        Assert.Equal((int)AuthorizationTraceAvailability.NotAvailable, restarted.GetProperty("availability").GetInt32());
        Assert.Empty(restarted.GetProperty("steps").EnumerateArray());
    }

    private static void AssertShape(JsonElement read, AuthorizationDecision decision)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Authorization", "authorization-trace.json")));
        var expected = fixture.RootElement.GetProperty("read");
        Assert.Equal(expected.EnumerateObject().Select(p => p.Name).Order(), read.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(expected.GetProperty("availability").GetInt32(), read.GetProperty("availability").GetInt32());
        Assert.Equal(expected.GetProperty("version").GetInt32(), read.GetProperty("version").GetInt32());
        var steps = read.GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal(4, steps.Length);
        for (var i = 0; i < steps.Length; i++)
        {
            Assert.Equal(expected.GetProperty("steps")[i].GetProperty("ordinal").GetInt32(), steps[i].GetProperty("ordinal").GetInt32());
            Assert.Equal(expected.GetProperty("steps")[i].GetProperty("stage").GetString(), steps[i].GetProperty("stage").GetString());
            Assert.Equal(decision.Evidence.Project()[i].Facts, steps[i].GetProperty("facts").EnumerateArray().Select(f => f.GetString()));
        }
        Assert.Equal(AuthorizationCounterfactual.From(decision.Evidence).Description,
            read.GetProperty("counterfactual").GetProperty("description").GetString());
    }

    private static string PathFor(Guid id) => $"{AuthorizationAdminRoutes.RouteBase}/traces/{id}";

    private sealed class Host : IAsyncDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "s331-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> environment = new();
        public IServiceProvider Services => LocalNodeHostRuntime.CurrentServices!;
        public HttpClient Client { get; private set; } = null!;
        public TenantId Tenant => NodeTenant.Resolve(Services.GetRequiredService<IActiveTeamAccessor>());
        public ActorId Actor => new(NodeCallerParty.OperatorParty.Value);
        public DateTimeOffset Now => Services.GetRequiredService<TimeProvider>().GetUtcNow();
        public static async Task<Host> OpenAsync()
        {
            var host = new Host();
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["DOTNET_ENVIRONMENT"] = "Production", ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["ASPNETCORE_URLS"] = "http://127.0.0.1:7316", ["LocalNode__HealthPort"] = "7316",
                ["LocalNode__RootSeedHex"] = new string('3', 64), ["LocalNode__WebClient__Enabled"] = "false",
                ["LocalNode__MultiTeam__Enabled"] = "false", ["LocalNode__SchedulingDogfood__Enabled"] = "false",
                ["Logging__EventLog__LogLevel__Default"] = "None"
            })
            {
                host.environment[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
            try
            {
                await host.StartAsync();
                var at = host.Now;
                await host.Services.GetRequiredService<IGrantStore>().AppendAsync(host.Tenant,
                    new AccessGrant(GrantId.New(), host.Tenant, host.Actor, RoleReference.Administrator,
                        ScopeExpression.Parse("/"), GrantResidency.Cache, new GrantValidity(at), GranterKind.Person,
                        host.Actor, at, new GrantProvenance(GrantSourceKind.Manual,
                            new GrantReason(GrantReasonCodes.Manual, "331 composition fixture"), host.Actor), at));
                await host.RestartAsync();
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }
        private async Task StartAsync()
        {
            var uri = await LocalNodeHostRuntime.StartAsync("s331-route-token", directory, CancellationToken.None);
            Client = new HttpClient { BaseAddress = uri };
            Client.DefaultRequestHeaders.Add("Authorization", "Bearer s331-route-token");
        }
        public async Task RestartAsync()
        {
            Client.Dispose();
            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            await StartAsync();
        }
        public async Task<JsonElement> ReadAsync(Guid id)
        {
            using var response = await Client.GetAsync(PathFor(id));
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        public async Task<(Guid, AuthorizationDecision)> RecordAsync(bool allowed)
        {
            var at = Now;
            var principal = allowed ? Actor : new ActorId("s331-no-authority");
            var operation = AuthorizationOperation.Parse(Permission.AuditRead);
            var request = new AuthorizationWriteContext(principal, Tenant, at).Request(operation, "audit", Guid.NewGuid().ToString());
            var decision = await Services.GetRequiredService<AuthorizationGate>().DecideAsync(request);
            Assert.Equal(allowed, decision.Verdict == AuthorizationVerdict.Allowed);
            var payload = await Services.GetRequiredService<IOperationSigner>().SignAsync(
                new AuditPayload(new Dictionary<string, object?> { ["secret"] = "must-not-leak" }), at, Guid.NewGuid());
            var record = new AuditRecord(Guid.NewGuid(), Tenant, new AuditEventType("Ticket331Decision"), at, payload, [],
                Actor: principal, Target: request.Target, Act: request.Act);
            var trail = Services.GetRequiredService<IAuditTrail>();
            if (allowed)
            {
                await Assert.ThrowsAsync<AuthorizedAuditRefusedException>(() =>
                    ((IRefusedAuditTrail)trail).AppendRefusedAsync(record, decision).AsTask());
                await ((IAuthorizedAuditTrail)trail).AppendAuthorizedAsync(record, decision);
            }
            else
            {
                await Assert.ThrowsAsync<AuthorizedAuditRefusedException>(() =>
                    ((IAuthorizedAuditTrail)trail).AppendAuthorizedAsync(record, decision).AsTask());
                await Assert.ThrowsAsync<AuthorizedAuditRefusedException>(() =>
                    ((IRefusedAuditTrail)trail).AppendRefusedAsync(record with { TenantId = new TenantId("wrong") }, decision).AsTask());
                await ((IRefusedAuditTrail)trail).AppendRefusedAsync(record, decision);
            }
            return (record.AuditId, decision);
        }
        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            foreach (var (key, value) in environment) Environment.SetEnvironmentVariable(key, value);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
