using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Entities;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Audit;

/// <summary>
/// Ticket 217 / L628 -- the audit trail is read THROUGH the <c>audit:read</c> act at the point of use.
/// </summary>
/// <remarks>
/// <para>
/// Hosts the same <see cref="AuditEventRoutes.Map"/> the production
/// <c>HostedAuditEventApiEndpoint</c> registers, over a real Kestrel listener and a real temp SQLite
/// <c>local-node.db</c>, and decides every request with the REAL <see cref="AuthorizationGate"/> over the
/// definition-joined closure the REAL platform seed installs. So what a caller may read here is decided by
/// the Auditor's actual capability binding, not by a test double.
/// </para>
/// <para>
/// Two entries are seeded: the BOOTSTRAP entry (the genesis of this tenant's hash chain, written by the
/// installer with no predecessor) and an ORDINARY one chained to it. Both are readable through the one act,
/// and neither is readable without it.
/// </para>
/// </remarks>
public sealed class AuditEventRouteAuthorizationTests
{
    private const string BootstrapAuditId = "00000000-0000-0000-0000-0000000000b0";
    private const string OrdinaryAuditId = "00000000-0000-0000-0000-0000000000a1";
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-06T12:00:00Z");

    [Fact(DisplayName = "The Auditor reads the audit list and both the bootstrap and the ordinary entry")]
    public async Task The_auditor_reads_the_trail_and_both_entries()
    {
        await using var h = await Host.CreateAsync(grantAuditor: true);

        var list = await h.Client.GetAsync(AuditEventRoutes.RouteBase);
        var bootstrap = await h.Client.GetAsync($"{AuditEventRoutes.RouteBase}/{BootstrapAuditId}");
        var ordinary = await h.Client.GetAsync($"{AuditEventRoutes.RouteBase}/{OrdinaryAuditId}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
        var page = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal<string?[]>(
            [OrdinaryAuditId, BootstrapAuditId],
            page.GetProperty("events").EnumerateArray()
                .Select(item => item.GetProperty("audit_id").GetString()).ToArray());
        Assert.Equal(
            BootstrapAuditId,
            (await bootstrap.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("audit_id").GetString());
    }

    [Fact(DisplayName = "With audit:read narrowed away the list and every entry refuse fail-closed")]
    public async Task Without_the_capability_every_audit_read_refuses()
    {
        await using var h = await Host.CreateAsync(grantAuditor: false);

        var list = await h.Client.GetAsync(AuditEventRoutes.RouteBase);
        var bootstrap = await h.Client.GetAsync($"{AuditEventRoutes.RouteBase}/{BootstrapAuditId}");
        var ordinary = await h.Client.GetAsync($"{AuditEventRoutes.RouteBase}/{OrdinaryAuditId}");

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, bootstrap.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, ordinary.StatusCode);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ServiceProvider _authorization;
        private readonly string _dir;

        private Host(WebApplication app, ServiceProvider authorization, string dir, HttpClient client)
        {
            _app = app;
            _authorization = authorization;
            _dir = dir;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<Host> CreateAsync(bool grantAuditor)
        {
            var tenant = NodeTenant.Resolve(NodeTestActiveTeam.Accessor);
            var authorization = await SeededAuthorizationAsync(tenant, grantAuditor);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            var dir = Path.Combine(Path.GetTempPath(), "harborline-audit-routes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            builder.Services.AddSingleton<Harborline.Api.Foundation.Persistence.IHarborlineEntityModule,
                AuditEventEntityModule>();
            builder.Services.AddDbContextFactory<LocalNodeDbContext>(
                opt => opt.UseSqlite($"Data Source={Path.Combine(dir, "audit-routes.db")};Pooling=False"));
            builder.Services.AddTestKernelClock();
            builder.Services.AddSingleton(RealGate(authorization));

            var app = builder.Build();
            var factory = app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            await using (var ctx = await factory.CreateDbContextAsync())
            {
                await ctx.Database.EnsureCreatedAsync();
                Seed(ctx, tenant.Value);
                await ctx.SaveChangesAsync();
            }

            app.Use(async (http, next) =>
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
                await next(http);
            });
            AuditEventRoutes.Map(
                app.MapDeviceReachableProductDataGroup(),
                new NodeAuditEventReader(factory),
                NodeTestActiveTeam.Accessor);
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            return new Host(app, authorization, dir,
                new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) });
        }

        /// <summary>The production closure gate over the real seed: the audit routes' verdicts here are the
        /// Auditor's real binding deciding.</summary>
        private static AuthorizationGate RealGate(ServiceProvider authorization) => new(
            authorization.GetRequiredService<IAuthorizationClosureSnapshotReader>(),
            authorization.GetRequiredService<IRecordStandingResolver>(),
            authorization.GetRequiredService<IAuthorizationDefinitionAtomReader>());

        private static async Task<ServiceProvider> SeededAuthorizationAsync(TenantId tenant, bool grantAuditor)
        {
            var services = new ServiceCollection();
            services.AddSingleton(TestAuthorization.AllowGate());
            services.AddAccessGrantModule();
            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<AccessGrantAuthorizationSeed>()
                .InstallAsync(tenant, At, AuthorizationSeedProfile.Production);
            if (!grantAuditor)
            {
                // The caller keeps every other holding the seed gives it and loses ONLY audit:read, by the
                // tenant narrowing the one definition that offers it. So a refusal below is this capability
                // refusing, not a caller who happens to hold nothing at all.
                var catalogue = provider.GetRequiredService<IAuthorizationDefinitionCatalogueReader>();
                var auditRead = Assert.Single(
                    await catalogue.ListAsync(tenant),
                    row => row.Definition.Operation.Value == Permission.AuditRead);
                await provider.GetRequiredService<AuthorizationDefinitionWriter>().WriteAsync(
                    new NarrowCapabilityRoleBinding(
                        tenant, auditRead.Definition.DefinitionId, RoleBindingSet.Empty,
                        new ActorId("tenant-admin"), At, new BindingChangeReason("no-audit-read")));
            }

            return provider;
        }

        private static void Seed(LocalNodeDbContext ctx, string tenantId)
        {
            var bootstrapPayload = """{"kind":"bootstrap"}""";
            var bootstrapHash = NodeAuditHashChain.ComputeHash(
                null, BootstrapAuditId, "Security.InstallationFounderBootstrapped",
                "installer", tenantId, At, bootstrapPayload);
            var ordinaryPayload = """{"kind":"ordinary"}""";
            var ordinaryHash = NodeAuditHashChain.ComputeHash(
                bootstrapHash, OrdinaryAuditId, "Financial.JournalPosted",
                "local", tenantId, At.AddMinutes(1), ordinaryPayload);

            ctx.Set<NodeAuditEventRow>().AddRange(
                new NodeAuditEventRow
                {
                    AuditId = BootstrapAuditId,
                    TenantId = tenantId,
                    EventType = "Security.InstallationFounderBootstrapped",
                    OccurredAt = At,
                    Actor = "installer",
                    Payload = bootstrapPayload,
                    PrevHash = null,
                    Hash = bootstrapHash,
                },
                new NodeAuditEventRow
                {
                    AuditId = OrdinaryAuditId,
                    TenantId = tenantId,
                    EventType = "Financial.JournalPosted",
                    OccurredAt = At.AddMinutes(1),
                    Actor = "local",
                    Payload = ordinaryPayload,
                    PrevHash = bootstrapHash,
                    Hash = ordinaryHash,
                });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            await _authorization.DisposeAsync();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
