using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Entities;
using Harborline.Blocks.LayoutRuntime;

using static Harborline.Api.LocalNodeHost.Tests.Layout.LayoutDenialTestKit;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>HTTP parity for the protected related-binding denial reader.</summary>
public sealed class LayoutDenialRouteTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
    private static readonly ActorId Both = new("auditor-member-route-735");
    private static readonly ActorId AuditorOnly = new("auditor-route-735");
    private static readonly ActorId MemberOnly = new("member-route-735");

    [Fact(DisplayName = "T-735: the denial route returns the denial only to a reader with audit:read and records:read")]
    public async Task The_denial_route_preserves_reader_authorization_parity()
    {
        await using var allowed = await Host.CreateAsync(Both);
        await using var auditOnly = await Host.CreateAsync(AuditorOnly);
        await using var recordsOnly = await Host.CreateAsync(MemberOnly);

        var query = "?block_id=owner-card&relationship_key=invoice.owner&request_id=request-735";
        var permitted = await allowed.Client.GetAsync(LayoutDenialRoutes.RouteBase + query);
        var noRecord = await auditOnly.Client.GetAsync(LayoutDenialRoutes.RouteBase + query);
        var noAudit = await recordsOnly.Client.GetAsync(LayoutDenialRoutes.RouteBase + query);

        Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, noRecord.StatusCode);
        Assert.Equal(HttpStatusCode.OK, noAudit.StatusCode);
        Assert.Single((await ReadArrayAsync(permitted.Content)).EnumerateArray());
        Assert.Empty((await ReadArrayAsync(noRecord.Content)).EnumerateArray());
        Assert.Empty((await ReadArrayAsync(noAudit.Content)).EnumerateArray());
    }

    [Fact(DisplayName = "T-735: the denial route rejects incomplete queries")]
    public async Task The_denial_route_rejects_incomplete_queries()
    {
        await using var h = await Host.CreateAsync(Both);
        var response = await h.Client.GetAsync($"{LayoutDenialRoutes.RouteBase}?block_id=owner-card");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<JsonElement> ReadArrayAsync(HttpContent content)
    {
        using var document = JsonDocument.Parse(await content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ServiceProvider _authorization;
        private readonly Pipeline _pipeline;

        private Host(WebApplication app, ServiceProvider authorization, Pipeline pipeline, HttpClient client)
        {
            _app = app;
            _authorization = authorization;
            _pipeline = pipeline;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<Host> CreateAsync(ActorId caller)
        {
            var tenant = NodeTenant.Resolve(NodeTestActiveTeam.Accessor);
            var authorization = await SeededAuthorizationAsync(tenant);
            var pipeline = new Pipeline(new InMemoryAuditTrail());
            var gate = RealGate(authorization);
            var trace = new LayoutDenialGateLog(pipeline.Outbox, pipeline.Appender, pipeline.Alarms, tenant,
                new FixedTime(At), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Resolve(new OutcomeSources(LayoutRelatedResult.Denied(
                "authorization.permission_required", "/records/party-19", Owner)), trace,
                new LayoutResolutionRequest("request-735", "principal.clerk-4"));
            await trace.WrittenAsync();
            await trace.AppendAsync();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Use(async (http, next) =>
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
                http.Features.Set(new SelectedSessionRequestPrincipal(
                    "route-account", tenant, new PrincipalUserId(caller.Value), new CanonicalPartyReference(caller.Value),
                    "route-membership", 1, [new PinnedGrantOwnerVersion("route-grant", 1)], 1,
                    "route-session", "route-coordination"));
                await next(http);
            });
            LayoutDenialRoutes.Map(app.MapDeviceReachableProductDataGroup(),
                new LayoutDenialReader(pipeline.Trail, gate), NodeTestActiveTeam.Accessor, new FixedTime(At));
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            return new Host(app, authorization, pipeline,
                new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) });
        }

        private static AuthorizationGate RealGate(ServiceProvider authorization) => new(
            authorization.GetRequiredService<IAuthorizationClosureSnapshotReader>(),
            authorization.GetRequiredService<IRecordStandingResolver>(),
            authorization.GetRequiredService<IAuthorizationDefinitionAtomReader>());

        private static async Task<ServiceProvider> SeededAuthorizationAsync(TenantId tenant)
        {
            var services = new ServiceCollection();
            services.AddSingleton(TestAuthorization.AllowGate());
            services.AddAccessGrantModule();
            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<AccessGrantAuthorizationSeed>()
                .InstallAsync(tenant, At, AuthorizationSeedProfile.Production);
            var grants = provider.GetRequiredService<IGrantStore>();
            var member = AccessGrantAuthorizationSeed.MemberRole;
            await grants.AppendAsync(tenant, Grant(Both, RoleReference.Auditor, "/"));
            await grants.AppendAsync(tenant, Grant(Both, member, "/"));
            await grants.AppendAsync(tenant, Grant(AuditorOnly, RoleReference.Auditor, "/"));
            await grants.AppendAsync(tenant, Grant(MemberOnly, member, "/"));
            return provider;
        }

        private static AccessGrant Grant(ActorId subject, RoleReference role, string scope) => new(
            GrantId.New(), NodeTenant.Resolve(NodeTestActiveTeam.Accessor), subject, role, ScopeExpression.Parse(scope),
            GrantResidency.Cache, new GrantValidity(At.AddHours(-1)), GranterKind.Person, new ActorId("tenant-admin"),
            At.AddHours(-1), new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual),
                new ActorId("tenant-admin")), At.AddHours(-1));

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            await _authorization.DisposeAsync();
            _pipeline.Dispose();
        }
    }
}
