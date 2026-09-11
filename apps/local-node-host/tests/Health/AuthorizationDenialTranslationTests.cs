using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// Ticket 380 slice 1 — the CLASS-level fence: no handler on the node's inner application can let the gate's
/// <see cref="AuthorizationDeniedException"/> reach the server. A stub endpoint that throws one (the shape
/// every "authorizes inside the service" route family has) answers the node's rendered, audited 403.
/// </summary>
public sealed class AuthorizationDenialTranslationTests : IAsyncLifetime
{
    private const string ThrowingRoute = "/stub/throws-the-gates-denial";
    private static readonly TenantId Tenant = new("tenant-380-translation");

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(TestRouteGate.Denying());
        builder.Services.AddSingleton(new NodePrincipalSigner(RandomNumberGenerator.GetBytes(32)));
        builder.Services.AddSingleton<IOperationSigner>(
            sp => sp.GetRequiredService<NodePrincipalSigner>().Signer);
        builder.Services.AddEnrollmentCompensatingControlAudit();
        builder.Services.AddSingleton<IAuditTrail>(sp => sp.GetRequiredService<InMemoryAuditTrail>());
        builder.Services.AddAuthorizationRefusalAudit();

        _app = builder.Build();
        AuthorizationDenialTranslation.Use(_app);

        // The stub: a handler that meets its refusal as the gate's exception, exactly as a route whose
        // service re-decides does, and does nothing about it.
        _app.MapPost(ThrowingRoute, async (HttpContext http, CancellationToken ct) =>
        {
            var gate = http.RequestServices.GetRequiredService<AuthorizationGate>();
            var authority = new AuthorizationWriteContext(
                new ActorId("stub-principal"), Tenant, TimeProvider.System.GetUtcNow());
            var decision = await gate
                .DecideAsync(authority.Request(AuthorizationOperation.Parse("records:read"), "record", "stub-record"), ct)
                .ConfigureAwait(false);
            decision.RequireAllowed();
            return Results.Ok();
        });

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

    [Fact(DisplayName = "holds 380.A2: a handler's uncaught gate denial answers the rendered 403, not a 500")]
    public async Task A_thrown_gate_denial_answers_the_rendered_refusal()
    {
        var response = await _client.PostAsJsonAsync(ThrowingRoute, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var refusal = JsonDocument.Parse(body);
        Assert.Equal(
            new[] { "auditId", "code", "detail", "permission", "remediation", "title" },
            refusal.RootElement.EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            AuthorizationRefusalRenderer.PermissionRequiredCode,
            refusal.RootElement.GetProperty("code").GetString());
        Assert.Equal("records:read", refusal.RootElement.GetProperty("permission").GetString());
        Assert.NotEqual(Guid.Empty, refusal.RootElement.GetProperty("auditId").GetGuid());
        // The exception's own sentence, and the decision's classified reading, stay off the wire.
        Assert.DoesNotContain("The authorization gate denied", body, StringComparison.Ordinal);
        Assert.DoesNotContain(Tenant.Value, body, StringComparison.Ordinal);

        // The refusal is AUDITED against the decision the handler already made — never re-decided here.
        var rows = new List<AuditRecord>();
        await foreach (var record in _app.Services.GetRequiredService<IAuditTrail>()
                           .QueryAsync(new AuditQuery(Tenant)))
        {
            if (record.EventType.Equals(AuthorizationRefusalAudit.AuthorizationRefusedEventType))
                rows.Add(record);
        }
        var row = Assert.Single(rows);
        Assert.Equal(row.AuditId, refusal.RootElement.GetProperty("auditId").GetGuid());
        Assert.Equal(false, row.Payload.Payload.Body["preDecision"]);
        Assert.NotNull(row.AuthoritySnapshot);
        Assert.Equal(4, row.AuthoritySnapshot.Trace!.Count);
    }

    [Fact(DisplayName = "holds 380.A2: the shared host composition registers the translation")]
    public void The_shared_composition_registers_the_translation()
    {
        // The stub above proves the middleware; this proves it is installed where every route of the node's
        // inner application passes through it, rather than only in this fixture.
        var composition = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "apps", "local-node-host", "Health", "SharedHostedWebApp.cs"));
        // Matched at the start of a statement line, so a commented-out registration does not count.
        var registrations = composition.Split(
            "\n        AuthorizationDenialTranslation.Use(_app);", StringSplitOptions.None).Length - 1;
        var pipelines = composition.Split(
            "\n        NodeMutationIdempotency.UseOnce(_app, timeProvider);", StringSplitOptions.None).Length - 1;

        Assert.True(pipelines > 0, "Could not find the shared host's request pipeline.");
        Assert.Equal(pipelines, registrations);
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var hostRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!;
        var repositoryRoot = Path.GetDirectoryName(Path.GetDirectoryName(hostRoot)!)!;
        Assert.True(
            Directory.Exists(Path.Combine(repositoryRoot, "apps"))
                && Directory.Exists(Path.Combine(repositoryRoot, "packages")),
            $"Could not locate the repository root from '{thisFile}'.");
        return repositoryRoot;
    }
}
