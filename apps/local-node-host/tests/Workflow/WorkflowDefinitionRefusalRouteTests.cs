using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// Ticket 214 slice 2 — the workflow-definition PUT authorizes INSIDE its store, so a refusal reaches the
/// route as <see cref="AuthorizationDeniedException"/>. These tests ride the production route map
/// (<see cref="WorkflowDefinitionRoutes.Map"/>) over a real listener with a DENYING gate and prove three
/// things at once: the response is the node's one rendered 403 rather than raw exception text, its body
/// shape is pinned, and the classified diagnostic the body withheld is on the refusal's AUDIT row.
/// </summary>
public sealed class WorkflowDefinitionRefusalRouteTests : IAsyncLifetime
{
    private static readonly TeamId Team = new(Guid.Parse("cccc0000-0000-0000-0000-0000000ff003"));
    private const string Base = "/api/local-node/workflows/definitions";
    private const string Key = "invoice-approval.v1";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private TenantId _tenant;
    private FaultingAuditTrail _trail = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddTestKernelClock();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var entityStore = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        builder.Services.AddSingleton(entityStore);
        builder.Services.AddSingleton<IEntityStore>(new InMemoryEntityStoreReader(entityStore));
        builder.Services.AddSingleton<ICapabilityAuthorityRegistry>(CapabilityAuthorityRegistry.Canonical);
        builder.Services.AddSingleton<IWorkflowAdmissionValidator, WorkflowAdmissionValidator>();

        // The ONE difference from WorkflowDefinitionRouteTests: a gate that grants nothing, so the
        // lifecycle's own decision denies and the route meets the refusal.
        builder.Services.AddSingleton(TestRouteGate.Denying());
        var roleGate = TestAuthorization.RoleGate();
        builder.Services.AddSingleton(roleGate);
        builder.Services.AddSingleton<IRoleGateAdmission>(roleGate);
        builder.Services.AddEntityStoreWorkflowDefinitionStore(_ => entityStore);

        // The shipped refusal-audit composition — the same registration Program.cs makes.
        builder.Services.AddSingleton(new NodePrincipalSigner(RandomNumberGenerator.GetBytes(32)));
        builder.Services.AddSingleton<IOperationSigner>(
            sp => sp.GetRequiredService<NodePrincipalSigner>().Signer);
        builder.Services.AddEnrollmentCompensatingControlAudit();
        builder.Services.AddSingleton<IAuditTrail>(sp =>
            _trail = new FaultingAuditTrail(sp.GetRequiredService<InMemoryAuditTrail>()));
        builder.Services.AddAuthorizationRefusalAudit();

        _app = builder.Build();
        var activeTeam = new FixedActiveTeamAccessor(new TeamContext(
            Team, "Team C", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
        _tenant = NodeTenant.Resolve(activeTeam);

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        WorkflowDefinitionRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            _app.Services.GetRequiredService<AuthorizedWorkflowDefinitionLifecycle>(),
            activeTeam,
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

    [Fact(DisplayName = "214 s2: a refused save answers the rendered 403, not the exception's text")]
    public async Task A_refused_save_answers_the_rendered_refusal()
    {
        var response = await _client.PutAsJsonAsync($"{Base}/{Key}", Body());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            AuthorizationRefusalRenderer.PermissionRequiredCode, body.GetProperty("code").GetString());
        // The exception's own sentence is what slice 1 fenced out of every response.
        Assert.DoesNotContain(
            "The authorization gate denied", body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Key, body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "214 s2: the 403 body shape is pinned — code, permission, title, detail, remediation")]
    public async Task The_refusal_body_shape_is_pinned()
    {
        var response = await _client.PutAsJsonAsync($"{Base}/{Key}", Body());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The WHOLE public surface, named. A later change to what a refusal serializes fails here first.
        Assert.Equal(
            new[] { "auditId", "code", "detail", "permission", "remediation", "title" },
            body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("scheduling:author", body.GetProperty("permission").GetString());
        Assert.Equal("You do not have permission for this action.", body.GetProperty("title").GetString());
        Assert.Equal(
            "The action was refused because a required permission is missing.",
            body.GetProperty("detail").GetString());
        Assert.Equal(
            "Ask an administrator to grant the permission this action requires.",
            body.GetProperty("remediation").GetString());
        // No extensions, no headers: the refusal has no other public surface to drift into.
        Assert.False(response.Headers.Contains("X-Authorization-Diagnostic"));

        // A failed append still renders the same refusal, without an unrecorded receipt id.
        _trail.FailRefusalAppend = true;
        var failedAppendResponse = await _client.PutAsJsonAsync($"{Base}/{Key}", Body());
        Assert.Equal(HttpStatusCode.Forbidden, failedAppendResponse.StatusCode);
        var failedAppendBody = await failedAppendResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            new[] { "code", "detail", "permission", "remediation", "title" },
            failedAppendBody.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        foreach (var property in failedAppendBody.EnumerateObject())
            Assert.Equal(body.GetProperty(property.Name).GetRawText(), property.Value.GetRawText());
        Assert.Equal(1, _trail.FailedAppends);
    }

    [Fact(DisplayName = "214 s2: the audit row keeps the classified diagnostic the response withheld")]
    public async Task The_audit_row_keeps_the_classified_diagnostic()
    {
        var response = await _client.PutAsJsonAsync($"{Base}/{Key}", Body());
        var body = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetRawText();

        var rows = new List<AuditRecord>();
        await foreach (var record in _app.Services.GetRequiredService<IAuditTrail>()
                           .QueryAsync(new AuditQuery(_tenant)))
        {
            if (record.EventType.Equals(AuthorizationRefusalAudit.AuthorizationRefusedEventType))
                rows.Add(record);
        }

        var row = Assert.Single(rows);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(row.AuditId, json.RootElement.GetProperty("auditId").GetGuid());
        var diagnostic = Assert.IsType<string>(
            row.Payload.Payload.Body[AuthorizationRefusalAudit.DiagnosticKey]);

        // The row carries the decision's full reading — operation, principal, tenant, the target record —
        // and the response carries none of it.
        Assert.Contains("denied;operation=scheduling:author", diagnostic, StringComparison.Ordinal);
        Assert.Contains(_tenant.Value, diagnostic, StringComparison.Ordinal);
        Assert.Contains(Key, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostic, body, StringComparison.Ordinal);
        Assert.DoesNotContain(_tenant.Value, body, StringComparison.Ordinal);

        // The decision the guard made is the one recorded — never re-decided, never invented.
        Assert.Equal(false, row.Payload.Payload.Body["preDecision"]);
        Assert.Equal("scheduling", row.Target!.Value.RecordKind);
        Assert.NotNull(row.AuthoritySnapshot);
        Assert.Equal(4, row.AuthoritySnapshot.Trace!.Count);
    }

    private sealed class FaultingAuditTrail(InMemoryAuditTrail inner) : IRefusedAuditTrail
    {
        internal bool FailRefusalAppend { get; set; }
        internal int FailedAppends { get; private set; }

        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) =>
            inner.AppendAsync(record, ct);

        public ValueTask AppendRefusedAsync(AuditRecord record, AuthorizationDecision decision, CancellationToken ct = default)
        {
            if (FailRefusalAppend)
            {
                FailedAppends++;
                throw new IOException("Injected refusal append failure.");
            }
            return inner.AppendRefusedAsync(record, decision, ct);
        }

        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default) =>
            inner.QueryAsync(query, ct);
    }

    private static object Body() => new
    {
        key = Key,
        version = "0.0.1",
        status = "Draft",
        title = Text("Invoice approval"),
        mutability = "Locked",
        initialState = "Draft",
        states = new object[]
        {
            new { id = "Draft", label = Text("Draft"), kind = "Normal" },
            new { id = "Posted", label = Text("Posted"), kind = "Terminal" },
        },
        triggers = new object[] { new { id = "approve", kind = "HumanAction", task = "invoice-approval" } },
        transitions = new object[] { new { id = "t-approve", from = "Draft", on = "approve", to = "Posted" } },
        actions = Array.Empty<object>(),
        guards = Array.Empty<object>(),
    };

    private static object Text(string en) =>
        new { defaultLocale = "en", values = new Dictionary<string, string> { ["en"] = en } };

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
