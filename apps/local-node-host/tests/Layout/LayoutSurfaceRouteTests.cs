using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Layout;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Entities;
using Harborline.Blocks.LayoutRuntime;

using static Harborline.Api.LocalNodeHost.Tests.Layout.LayoutDenialTestKit;

namespace Harborline.Api.LocalNodeHost.Tests.Layout;

/// <summary>
/// T-735 — DES-0052 layout-eng-31 at the route: <c>GET /api/local-node/layout/surfaces/{surfaceId}/records/{recordId}</c>
/// serves a signed, installed Layout surface through <see cref="LayoutSurfaceHost"/> over the real authorization gate and
/// the seeded production definitions. Related values come from the Records reference fields the root's Record Type declares
/// in the pack's signed closure (owner ruling 2026-11-04; layout-ck-28, layout-ck-39), looked up in a fixture store.
/// </summary>
/// <remarks>
/// Oracles: status codes, stable codes, block ids and values are literals; the related-record values are the fixture
/// store's literal <c>Contoso</c>; parity compares two responses' raw bytes with each other, with a control caller whose
/// response differs, so the equality is not vacuous; timing uses <see cref="TimingParity"/>'s fixed bounds.
/// </remarks>
[Collection(LayoutTimingParityCollection.Name)]
public sealed class LayoutSurfaceRouteTests(ITestOutputHelper output)
{
    private const string Root = "invoice-7";
    private const string UnknownRoot = "invoice-404";
    private const string MistypedRoot = "invoice-8";
    private const string SlashedRoot = "invoice-9";
    private const string RegistryRoot = "invoice-r1";
    private static readonly LayoutRecordReference OwnerRecord = new(SurfacePack.PartyType, "party-19");

    /// <summary>May read the root record only: records:read is scoped to <c>/records/invoice-7</c>.</summary>
    private static readonly ActorId Viewer = new("viewer-735");

    /// <summary>May read every record.</summary>
    private static readonly ActorId Member = new("member-735");

    /// <summary>Holds no grant.</summary>
    private static readonly ActorId Stranger = new("stranger-735");

    /// <summary>Holds audit:read and records:read: the reader of the denial trace.</summary>
    private static readonly ActorId Auditor = new("auditor-member-735");

    [Fact(DisplayName = "layout-eng-31 T-735: the route resolves an installed surface for a root record through LayoutSurfaceHost")]
    public async Task The_route_resolves_an_installed_surface_through_the_host()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        using var response = await h.GetAsync(Member, SurfacePack.Invoice, Root);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(("invoice", "1.0.0"),
            (body.RootElement.GetProperty("surface").GetString(), body.RootElement.GetProperty("version").GetString()));
        Assert.Equal(
            ["owner-card=\"Owner\"", "owner-name=\"Contoso\"", "supplier=\"Fabrikam\""],
            Blocks(body.RootElement));
        Assert.Empty(body.RootElement.GetProperty("refusals").EnumerateArray());
        // The host walks the surface once per discovery pass and once more to resolve; the related record is
        // looked up once per request, inside the host, never before it.
        Assert.Equal(1, h.Related.Lookups);
        Assert.Equal(0, h.Related.OutsideResolver);
    }

    [Fact(DisplayName = "T-735: an unknown surface refuses with layout.surface.unknown")]
    public async Task An_unknown_surface_refuses_with_a_stable_code()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        using var response = await h.GetAsync(Member, "no-such-surface", Root);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("layout.surface.unknown", await CodeAsync(response));
    }

    [Fact(DisplayName = "T-735 (CWE-203, API1:2023): to a caller who may not read the root, an unknown and a denied root are the same bytes; only an authorized caller learns the root is unknown")]
    public async Task An_unknown_root_and_a_denied_root_are_indistinguishable_to_an_unauthorized_caller()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        var denied = await h.ReadAsync(Stranger, SurfacePack.Invoice, Root);
        var unknown = await h.ReadAsync(Stranger, SurfacePack.Invoice, UnknownRoot);
        var unknownSurface = await h.ReadAsync(Stranger, "no-such-surface", Root);

        // The realistic probe: a caller who may read one record asks about one it may not read and one that is absent.
        var scopedDenied = await h.ReadAsync(Viewer, SurfacePack.Invoice, MistypedRoot);
        var scopedUnknown = await h.ReadAsync(Viewer, SurfacePack.Invoice, UnknownRoot);

        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal(denied, unknown);
        Assert.Equal(denied, unknownSurface);
        Assert.Equal(HttpStatusCode.Forbidden, scopedDenied.Status);
        Assert.Equal(scopedDenied, scopedUnknown);
        // The decision came before the lookup: the root store was never read for the stranger.
        Assert.Equal(0, h.Roots.Lookups);

        // The control: the root does not exist, and a caller who may read it is told so.
        using var authorized = await h.GetAsync(Member, SurfacePack.Invoice, UnknownRoot);
        Assert.Equal(HttpStatusCode.NotFound, authorized.StatusCode);
        Assert.Equal("layout.surface.root_unknown", await CodeAsync(authorized));
    }

    [Theory(DisplayName = "T-735 layout-ck-28: a related key with no single-target Records reference on the root's Record Type refuses")]
    [InlineData(SurfacePack.ApproversSurface, "layout.surface.relationship_many_target", "finance.invoice.approvers")]
    [InlineData(SurfacePack.PlainFieldSurface, "layout.surface.relationship_undeclared", "finance.invoice.supplier")]
    [InlineData(SurfacePack.UnknownFieldSurface, "layout.surface.relationship_undeclared", "finance.invoice.payee")]
    [InlineData(SurfacePack.NestedSurface, "layout.surface.relationship_undeclared", "finance.invoice.owner")]
    public async Task A_relationship_the_root_type_does_not_declare_as_single_target_refuses(string surface, string code, string relationship)
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        using var response = await h.GetAsync(Member, surface, Root);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((code, relationship),
            (body.RootElement.GetProperty("code").GetString(), body.RootElement.GetProperty("relationship").GetString()));
        Assert.Equal(0, h.Related.Lookups);
    }

    [Fact(DisplayName = "T-735 layout-ck-28: the declaration supplies the target type; a reference value naming another type has no target")]
    public async Task A_reference_value_of_another_type_has_no_target()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        using var response = await h.GetAsync(Member, SurfacePack.Invoice, MistypedRoot);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["supplier=\"Fabrikam\""], Blocks(body.RootElement));
        Assert.Equal(0, h.Related.Lookups);
    }

    [Fact(DisplayName = "layout-eng-31 T-735: with no Records instance store every related target is absent, identically for every caller")]
    public async Task With_no_instance_store_every_related_target_is_absent_for_every_caller()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero, AbsentLayoutRelatedRecordSource.Instance);

        var member = await h.ReadAsync(Member, SurfacePack.Invoice, Root);
        var viewer = await h.ReadAsync(Viewer, SurfacePack.Invoice, Root);

        Assert.Equal(HttpStatusCode.OK, member.Status);
        Assert.Equal(member, viewer);
        using var body = JsonDocument.Parse(member.Body);
        Assert.Equal(["supplier=\"Fabrikam\""], Blocks(body.RootElement));
        // No target was found, so the gate was never asked about one and nothing was traced.
        await h.Pipeline.Appender.IdleAsync();
        Assert.Empty(await RowsAsync(h.Pipeline.Trail, h.Tenant));
    }

    [Fact(DisplayName = "layout-eng-31 T-735: over HTTP, a missing and a denied related target give an unauthorized caller the same status and body bytes")]
    public async Task Missing_and_denied_related_targets_are_the_same_bytes_over_http()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        h.Related.Present = false;
        var missing = await h.ReadAsync(Viewer, SurfacePack.Invoice, Root);
        await h.Pipeline.Appender.IdleAsync();
        Assert.Empty(await RowsAsync(h.Pipeline.Trail, h.Tenant));
        h.Related.Present = true;
        var denied = await h.ReadAsync(Viewer, SurfacePack.Invoice, Root);
        await h.Pipeline.Appender.IdleAsync();

        Assert.Equal(HttpStatusCode.OK, missing.Status);
        Assert.Equal(missing, denied);
        // The denied path is real: the target exists, the gate refused it and the trace recorded it.
        Assert.Single(await RowsAsync(h.Pipeline.Trail, h.Tenant));
        // The control: a caller who may read the target sees it, so the equality above is not vacuous.
        var allowed = await h.ReadAsync(Member, SurfacePack.Invoice, Root);
        Assert.NotEqual(denied.Body, allowed.Body);
        using var body = JsonDocument.Parse(allowed.Body);
        Assert.Contains("owner-name=\"Contoso\"", Blocks(body.RootElement));
    }

    /// <summary>
    /// The timing-parity gate of <see cref="LayoutSurfaceHostParityTests"/>, measured at the route: each sample is
    /// one HTTP request, so the route's own work (authorization, surface and closure lookup, serialization) is in
    /// the measured time. Each local-store operation costs 5 ms of CPU, so without the floor the denied path is
    /// about 15 ms slower and the median rule fails.
    /// </summary>
    [Trait(LayoutTimingParityCollection.LaneTrait, LayoutTimingParityCollection.PerfLane)]
    [Fact(DisplayName = "layout-eng-31 T-735: at the route, missing and denied timings meet the TimingParity bounds (paired median within ±0.1 ms at 99.96% coverage, p90 gap under 1 ms)")]
    public async Task Missing_and_denied_route_timing_distributions_are_the_same()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);
        h.Pipeline.Db.Delay = TimeSpan.FromMilliseconds(5);

        await TimingParity.AssertSameAsync(floor =>
        {
            h.Options.ResponseFloor = floor;
            return async ownerExists =>
            {
                h.Related.Present = ownerExists;
                using var response = await h.GetAsync(Viewer, SurfacePack.Invoice, Root);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                _ = await response.Content.ReadAsByteArrayAsync();
            };
        }, h.Pipeline.Appender.IdleAsync, "route", output);
    }

    [Fact(DisplayName = "layout-eng-31 T-735: a denial made at the surface route reaches the denial reader route for a principal holding audit:read and records:read")]
    public async Task A_denial_reaches_the_reader_route_end_to_end()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);
        var request = Guid.NewGuid();

        using var surface = await h.GetAsync(Viewer, SurfacePack.Invoice, Root, request);
        Assert.Equal(HttpStatusCode.OK, surface.StatusCode);
        // Nothing the viewer receives names the request or the denial.
        Assert.DoesNotContain(request.ToString("D"), await surface.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await h.Pipeline.Appender.IdleAsync();

        var query = $"?block_id=owner-card&relationship_key={SurfacePack.OwnerKey}&request_id={request:D}";
        using var auditor = await h.GetAsync(Auditor, LayoutDenialRoutes.RouteBase + query);
        using var viewer = await h.GetAsync(Viewer, LayoutDenialRoutes.RouteBase + query);

        Assert.Equal(HttpStatusCode.OK, auditor.StatusCode);
        using var denials = JsonDocument.Parse(await auditor.Content.ReadAsStringAsync());
        var denial = Assert.Single(denials.RootElement.EnumerateArray());
        Assert.Contains("party-19", denial.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
        using var nothing = JsonDocument.Parse(await viewer.Content.ReadAsStringAsync());
        Assert.Empty(nothing.RootElement.EnumerateArray());
    }

    [Fact(DisplayName = "T-735: the route serves the selected-session principal only; the desktop plane without one is unauthorized and any other caller is outside the audience")]
    public async Task A_request_without_a_selected_session_is_refused()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        using var desktop = new HttpRequestMessage(HttpMethod.Get, RoutePath(SurfacePack.Invoice, Root));
        desktop.Headers.Add(Host.DesktopHeader, "1");
        using var desktopResponse = await h.Client.SendAsync(desktop);
        using var outside = await h.Client.GetAsync(RoutePath(SurfacePack.Invoice, Root));

        // The desktop plane is in the selected-session product audience, but this route has no desktop tenant
        // fallback: without a selected session there is no principal to authorize.
        Assert.Equal(HttpStatusCode.Unauthorized, desktopResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        Assert.Equal("route-audience.selected-session-product.unavailable", await CodeAsync(outside));
        Assert.Equal(0, h.Roots.Lookups);
    }

    [Theory(DisplayName = "layout-eng-31 T-735: a reference id the gate cannot address has no target, whether or not a record with that id exists")]
    [InlineData("party/19")]
    [InlineData("party?19")]
    [InlineData("party#19")]
    [InlineData("party\\19")]
    [InlineData("..")]
    public async Task An_unaddressable_reference_id_has_no_target_either_way(string id)
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);
        h.Related.Add(new LayoutRecordReference(SurfacePack.PartyType, id), """{"name":"Unaddressable"}""");
        h.Roots.Add(SlashedRoot, SurfacePack.InvoiceType,
            $$$"""{"supplier":"Fabrikam","owner":{"type":"finance.party","id":{{{JsonSerializer.Serialize(id)}}}}}""");

        h.Related.Present = false;
        var absent = await h.ReadAsync(Member, SurfacePack.Invoice, SlashedRoot);
        h.Related.Present = true;
        var present = await h.ReadAsync(Member, SurfacePack.Invoice, SlashedRoot);

        Assert.Equal(HttpStatusCode.OK, absent.Status);
        Assert.Equal(absent, present);
        using var body = JsonDocument.Parse(present.Body);
        Assert.Equal(["supplier=\"Fabrikam\""], Blocks(body.RootElement));
        Assert.Equal(0, h.Related.Lookups);
    }

    [Fact(DisplayName = "T-735: through the production root source, a registry entity's type names its Record Type and a declared field with no value resolves to null")]
    public async Task The_registry_root_source_types_the_root_by_its_entity_type()
    {
        var entities = new InMemoryRegistryEntityRepository(new Harborline.Api.Blocks.Assets.Registry.Audit.InMemoryRegistryAuditLog());
        var tenant = NodeTenant.Resolve(NodeTestActiveTeam.Accessor);
        await entities.UpsertAsync(new RegistryEntity
        {
            Id = new RegistryEntityId(RegistryRoot), TenantId = tenant, Type = new EntityTypeId(SurfacePack.InvoiceType),
            DisplayName = "Registry invoice", CreatedAt = new Instant(At),
        }, new Instant(At));
        // An entity with no bound property form has no values, so the writer's stores are never read.
        var roots = new RegistryLayoutRootRecordSource(entities, new PackBoundRegistryRecordWriter(null!, null!, null!, entities));
        await using var h = await Host.CreateAsync(TimeSpan.Zero, rootSource: roots);

        using var response = await h.GetAsync(Member, SurfacePack.Invoice, RegistryRoot);
        using var unknown = await h.GetAsync(Member, SurfacePack.Invoice, UnknownRoot);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["supplier=null"], Blocks(body.RootElement));
        Assert.Empty(body.RootElement.GetProperty("refusals").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("layout.surface.root_unknown", await CodeAsync(unknown));
    }

    [Fact(DisplayName = "layout-eng-31 T-735: in audit-degraded mode a surface with a related block refuses with layout.audit.degraded")]
    public async Task Audit_degraded_mode_refuses_with_its_stable_code()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);
        h.Options.AuditDegradedMode = true;
        h.Pipeline.Db.Fault = true;

        using var response = await h.GetAsync(Viewer, SurfacePack.Invoice, Root);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("layout.audit.degraded", await CodeAsync(response));
    }

    [Fact(DisplayName = "layout-eng-31 T-735: the final local-node composition maps the surface route through LocalNodeEndpointMapping over the registered LayoutSurfaceHost")]
    public async Task The_final_composition_maps_the_surface_route()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"layout-surface-route-{Guid.NewGuid():N}");
        ServiceProvider? provider = null;
        try
        {
            await Assert.ThrowsAsync<CompositionProbeCompleteException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    [
                        "--environment=Production",
                        "--LocalNode:RootSeedHex=" + new string('7', 64),
                        "--LocalNode:MultiTeam:Enabled=false",
                        "--LocalNode:Diagnostics:CommsDiagnosticLogging=false",
                        "--Logging:EventLog:LogLevel:Default=None",
                    ],
                    sessionTokenOverride: "layout-surface-route-probe",
                    dataDirectory: directory,
                    installFootprintRootOverride: directory,
                    finalServiceProviderProbe: (services, factory) =>
                    {
                        provider = (ServiceProvider)factory.CreateServiceProvider(factory.CreateBuilder(services));
                        throw new CompositionProbeCompleteException();
                    }));

            var registry = new LocalNodeExecutableEndpointRegistry();
            await using var listener = new SharedHostedWebApp(provider!,
                Options.Create(new LocalNodeOptions { HealthPort = 0 }), registry,
                NullLogger<SharedHostedWebApp>.Instance, TimeProvider.System);
            // Every mapper resolves from the final graph: removing the host's registration fails here.
            await using (await LocalNodeEndpointMapping.MapAsync(provider!, listener, new LocalNodeHostedComponentProfile(false, false, false)))
            {
                await listener.StartAsync(CancellationToken.None);
                Assert.Contains("/api/local-node/layout/surfaces/{surfaceId}/records/{recordId}|GET|SelectedSessionProduct",
                    registry.Current.Endpoints.SelectMany(endpoint => endpoint.MethodRouteFences.Select(method =>
                        $"{endpoint.RoutePattern}|{method.HttpMethod}|{method.RouteFenceKind}")));
                await listener.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (provider is not null) await provider.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string RoutePath(string surface, string record)
        => LayoutSurfaceRoutes.RouteTemplate.Replace("{surfaceId}", Uri.EscapeDataString(surface), StringComparison.Ordinal)
            .Replace("{recordId}", Uri.EscapeDataString(record), StringComparison.Ordinal);

    private static IReadOnlyList<string> Blocks(JsonElement body) => [.. body.GetProperty("blocks").EnumerateArray()
        .Select(block => $"{block.GetProperty("blockId").GetString()}={block.GetProperty("value").GetRawText()}")];

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("code").GetString();
    }

    /// <summary>What a caller observes of one response: status, media type and the raw body bytes.</summary>
    private sealed record Observed(HttpStatusCode Status, string? MediaType, string Body);

    private sealed class CompositionProbeCompleteException : Exception;

    private sealed class Host : IAsyncDisposable
    {
        private const string CallerHeader = "X-Test-Caller";
        internal const string DesktopHeader = "X-Test-Desktop-Plane";
        private readonly WebApplication _app;
        private readonly ServiceProvider _authorization;

        private Host(WebApplication app, ServiceProvider authorization, Pipeline pipeline, LayoutSurfaceOptions options,
            FixtureRoots roots, FixtureRelatedRecords related)
        {
            _app = app;
            _authorization = authorization;
            Pipeline = pipeline;
            Options = options;
            Roots = roots;
            Related = related;
            Client = new HttpClient
            {
                BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
            };
        }

        public HttpClient Client { get; }
        public TenantId Tenant { get; } = NodeTenant.Resolve(NodeTestActiveTeam.Accessor);
        public Pipeline Pipeline { get; }
        public LayoutSurfaceOptions Options { get; }
        public FixtureRoots Roots { get; }
        public FixtureRelatedRecords Related { get; }

        public static async Task<Host> CreateAsync(TimeSpan floor, ILayoutRelatedRecordSource? relatedSource = null,
            ILayoutRootRecordSource? rootSource = null)
        {
            var tenant = NodeTenant.Resolve(NodeTestActiveTeam.Accessor);
            var authorization = await SeededAuthorizationAsync(tenant);
            var gate = new AuthorizationGate(
                authorization.GetRequiredService<IAuthorizationClosureSnapshotReader>(),
                authorization.GetRequiredService<IRecordStandingResolver>(),
                authorization.GetRequiredService<IAuthorizationDefinitionAtomReader>());
            var pipeline = new Pipeline(new InMemoryAuditTrail());
            var options = new LayoutSurfaceOptions { ResponseFloor = floor };
            var time = new FixedTime(At);
            var surfaces = new LayoutSurfaceHost(gate, pipeline.Outbox, pipeline.Appender, pipeline.Alarms, time,
                NullLogger.Instance, options);
            var packs = await SurfacePack.InstallAsync(tenant);
            var roots = new FixtureRoots();
            roots.Add(Root, SurfacePack.InvoiceType,
                """{"supplier":"Fabrikam","owner":{"type":"finance.party","id":"party-19"}}""");
            roots.Add(MistypedRoot, SurfacePack.InvoiceType,
                """{"supplier":"Fabrikam","owner":{"type":"finance.invoice","id":"party-19"}}""");
            var related = new FixtureRelatedRecords();
            related.Add(OwnerRecord, """{"name":"Contoso"}""");

            var builder = WebApplication.CreateBuilder();
            TestDesktopOperator.AddTestDesktopOperator(builder.Services);
            builder.Services.AddSingleton(gate);
            builder.Services.AddSingleton<TimeProvider>(time);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Use(async (http, next) =>
            {
                // Authentication is upstream: the fixture binds the selected session the header names.
                if (http.Request.Headers.ContainsKey(DesktopHeader))
                    http.Features.Set(DesktopPlaneRequestFeature.Instance);
                if (http.Request.Headers.TryGetValue(CallerHeader, out var caller))
                {
                    http.Features.Set(new SelectedSessionRequestPrincipal(
                        "route-account", tenant, new PrincipalUserId(caller!), new CanonicalPartyReference(caller!),
                        "route-membership", 1, [new PinnedGrantOwnerVersion("route-grant", 1)], 1,
                        "route-session", "route-coordination"));
                }
                await next(http);
            });
            LayoutSurfaceRoutes.Map(app.MapSelectedSessionProductGroup(), surfaces, packs, rootSource ?? roots, relatedSource ?? related);
            LayoutDenialRoutes.Map(app.MapDeviceReachableProductDataGroup(),
                new LayoutDenialReader(pipeline.Trail, gate), NodeTestActiveTeam.Accessor, time);
            await app.StartAsync();
            return new Host(app, authorization, pipeline, options, roots, related);
        }

        public Task<HttpResponseMessage> GetAsync(ActorId caller, string surface, string record, Guid? correlation = null)
            => GetAsync(caller, RoutePath(surface, record), correlation);

        public Task<HttpResponseMessage> GetAsync(ActorId caller, string path, Guid? correlation = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add(CallerHeader, caller.Value);
            if (correlation is { } value) request.Headers.Add(SelectedRequestCorrelation.Header, value.ToString("D"));
            return Client.SendAsync(request);
        }

        public async Task<Observed> ReadAsync(ActorId caller, string surface, string record)
        {
            using var response = await GetAsync(caller, surface, record);
            return new Observed(response.StatusCode, response.Content.Headers.ContentType?.MediaType,
                await response.Content.ReadAsStringAsync());
        }

        private static async Task<ServiceProvider> SeededAuthorizationAsync(TenantId tenant)
        {
            var services = new ServiceCollection();
            services.AddSingleton(TestAuthorization.AllowGate());
            services.AddAccessGrantModule();
            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<AccessGrantAuthorizationSeed>()
                .InstallAsync(tenant, AdmittedInstant.FromRecordedAct(At), AuthorizationSeedProfile.Production, TestDesktopOperator.Actor);
            var grants = provider.GetRequiredService<IGrantStore>();
            var member = AccessGrantAuthorizationSeed.MemberRole;
            await grants.AppendAsync(tenant, Grant(tenant, Viewer, member, $"/records/{Root}"));
            await grants.AppendAsync(tenant, Grant(tenant, Member, member, "/"));
            await grants.AppendAsync(tenant, Grant(tenant, Auditor, RoleReference.Auditor, "/"));
            await grants.AppendAsync(tenant, Grant(tenant, Auditor, member, "/"));
            return provider;
        }

        private static AccessGrant Grant(TenantId tenant, ActorId subject, RoleReference role, string scope) => new(
            GrantId.New(), tenant, subject, role, ScopeExpression.Parse(scope),
            GrantResidency.Cache, new GrantValidity(At.AddHours(-1)), GranterKind.Person, new ActorId("tenant-admin"),
            At.AddHours(-1), new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual),
                new ActorId("tenant-admin")), At.AddHours(-1));

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Pipeline.Appender.IdleAsync();
            await _app.StopAsync();
            await _app.DisposeAsync();
            await _authorization.DisposeAsync();
            Pipeline.Dispose();
        }
    }
}
