using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// Ticket 379 slice 1 — the first-boot operator installs and activates a pack on a CLEAN node, through
/// the real composition: the compiled entry point, the real <c>AuthorizationGate</c>, the real grant
/// store, and the genesis founder inputs <c>docs/install/first-start.md</c> names (root seed + per-boot
/// session token). No <c>TestPackGate.AllowAll()</c> and — the load-bearing part — no grant injected
/// into the store behind the route's back.
/// </summary>
/// <remarks>
/// This is deliberately distinct from
/// <see cref="Program_Composition_InstallsAndServesPackNavigationThroughTheShippingHost"/>: that proof
/// used to pre-seed an Administrator grant for the node's SIGNING KEY id, which is what hid the defect
/// the m3 exit run found (the installer re-decided about the signing key id instead of the acting
/// operator, so every first-boot install was a 500). Here the ONLY holdings are the ones the genesis
/// installer seed confers on the desktop operator party.
/// </remarks>
public sealed partial class ComposedHostBootSmokeTests
{
    [Fact]
    public async Task Program_Composition_FirstBootOperatorInstallsAndActivatesAPackOnACleanNode()
    {
        const string rootSeedHex =
            "3535353535353535353535353535353535353535353535353535353535353535";
        await using var host = ComposedHost.Start(
            "first-boot operator pack install on a clean node",
            webClient: false,
            llmProxy: false,
            schedulingDogfood: false,
            multiTeam: false,
            environment: "Production",
            rootSeedHex: rootSeedHex);
        var baseUri = await host.AwaitReadinessAsync();
        using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "composed-host-smoke-token");

        using var export = await client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute, FirstBootPackExportRequest(), host.Deadline);
        var packBytes = await export.Content.ReadAsByteArrayAsync(host.Deadline);
        Assert.True(export.IsSuccessStatusCode, Encoding.UTF8.GetString(packBytes));

        // 379.A1 — install and activate as the first-boot operator session. Before the fix both of these
        // were HTTP 500 (AuthorizationDeniedException out of PackInstaller.AuthorizeOrAudit).
        using var packContent = new ByteArrayContent(packBytes);
        packContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var install = await client.PostAsync(
            PackInstallRoutes.InstallRoute, packContent, host.Deadline);
        var installBody = await install.Content.ReadAsStringAsync(host.Deadline);
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);
        using var installed = JsonDocument.Parse(installBody);
        Assert.True(installed.RootElement.GetProperty("installed").GetBoolean(), installBody);

        using var activate = await client.PostAsJsonAsync(
            PackInstallRoutes.ActivateRoute,
            new { packKey = FirstBootPackKey, version = "1.0.0" },
            host.Deadline);
        var activateBody = await activate.Content.ReadAsStringAsync(host.Deadline);
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);

        // 379.A2 — the transition is RECORDED against the founder's party (the desktop operator party
        // the bootstrap bearer resolves to and the genesis seed grants), never the node's signing key id.
        // The durable evidence is the gate-admitted projection admission the activate path writes with the
        // authorizing principal of the one decision it made. (The pack-install audit row itself is not yet
        // readable over HTTP on a clean node: KernelAuditPackInstallAudit writes to the in-memory
        // IAuthorizedAuditTrail while GET /audit-events reads local-node.db — ticket 331's durable-trail
        // gap, deliberately not papered over here.)
        await host.StopAsync();
        using var signer = new NodePrincipalSigner(Convert.FromHexString(rootSeedHex));
        var signingKeyId = signer.Signer.IssuerId.ToBase64Url();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlCipherLocalNodeDbContext(
            Convert.FromHexString(rootSeedHex),
            Path.Combine(host.DataDirectory, "local-node.db"),
            new SqlCipherKeyDerivation());
        await using var provider = services.BuildServiceProvider();
        await using var packs = await provider
            .GetRequiredService<IDbContextFactory<NodeLocalPacksDbContext>>()
            .CreateDbContextAsync();
        var principals = await packs.ProjectionAdmissions.AsNoTracking()
            .Where(row => row.PackId == FirstBootPackKey)
            .Select(row => row.Principal)
            .ToListAsync();
        Assert.NotEmpty(principals);
        Assert.DoesNotContain(signingKeyId, principals);
        Assert.All(principals, principal =>
            Assert.Equal(NodeCallerParty.OperatorParty.Value, principal));
    }

    private const string FirstBootPackKey = "ticket379.first-boot";

    private static object FirstBootPackExportRequest() => new
    {
        key = FirstBootPackKey,
        version = "1.0.0",
        name = "Ticket 379 first boot",
        description = "first-boot operator install on a clean node",
        scopeTier = "Horizontal",
        contents = new[]
        {
            new
            {
                key = "chrome",
                kind = "NavWorkspaceConfig",
                version = "1.0.0",
                content = new
                {
                    seedWorkspaces = new[]
                    {
                        new { id = "ticket-379-workspace", labelKey = "workspaces.ticket-379" },
                    },
                },
            },
        },
        dependencies = Array.Empty<object>(),
    };
}
