using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.DependencyInjection;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Compose;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class FailClosedWebPlaneRouteFenceTests
{
    private const string DesktopToken = "ticket-045-desktop-bootstrap-token";
    private const string Ticket066SelectedHandle = "ticket-066-selected-handle";

    [Fact]
    public async Task Selected_Session_Product_Group_Allows_Bound_Selected_Session()
    {
        const string path = WebSessionRoutes.MePath;
        const string devicePath = "/api/local-node/status/ticket-066-device-web-callers";
        var handlerCalls = 0;
        using var services = new ServiceCollection()
            .AddTestKernelClock()
            .AddLogging()
            .AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor())
            .AddSingleton(new NodeCallerSessionToken(DesktopToken))
            .AddSingleton<IWebSelectedSessionPrincipalAuthority>(new Ticket066SelectedSessionAuthority())
            .BuildServiceProvider();
        await using var app = new SharedHostedWebApp(
            services,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            services.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            services.GetRequiredService<TimeProvider>());
        app.MapApiRoutes(routes =>
        {
            var group = routes.MapSelectedSessionProductGroup();
            group.MapGet(path, (HttpContext context) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Results.Json(new
                {
                    singleHostMarkerPresent =
                        context.Features.Get<SingleHostTrustedRequestFeature>() is not null,
                });
            });
            routes.MapDeviceReachableProductDataGroup().MapGet(
                devicePath,
                () => Results.Ok());
        });
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(
            "Cookie",
            $"{WebSessionCookieNames.Selected}={Ticket066SelectedHandle}");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.False(body.RootElement.GetProperty("singleHostMarkerPresent").GetBoolean());
        }

        using var desktopRequest = new HttpRequestMessage(HttpMethod.Get, path);
        desktopRequest.Headers.Add(
            NodeCallerSessionToken.HeaderName,
            "Bearer " + DesktopToken);
        using var desktopResponse = await client.SendAsync(desktopRequest);
        Assert.Equal(HttpStatusCode.OK, desktopResponse.StatusCode);
        using (var body = JsonDocument.Parse(await desktopResponse.Content.ReadAsStringAsync()))
        {
            Assert.False(body.RootElement.GetProperty("singleHostMarkerPresent").GetBoolean());
        }

        using var selectedDeviceRequest = new HttpRequestMessage(HttpMethod.Get, devicePath);
        selectedDeviceRequest.Headers.Add(
            "Cookie",
            $"{WebSessionCookieNames.Selected}={Ticket066SelectedHandle}");
        using var selectedDeviceResponse = await client.SendAsync(selectedDeviceRequest);
        Assert.Equal(HttpStatusCode.OK, selectedDeviceResponse.StatusCode);

        using var desktopDeviceRequest = new HttpRequestMessage(HttpMethod.Get, devicePath);
        desktopDeviceRequest.Headers.Add(
            NodeCallerSessionToken.HeaderName,
            "Bearer " + DesktopToken);
        using var desktopDeviceResponse = await client.SendAsync(desktopDeviceRequest);
        Assert.Equal(HttpStatusCode.OK, desktopDeviceResponse.StatusCode);

        Assert.Equal(2, Volatile.Read(ref handlerCalls));
    }

    [Fact]
    public async Task Device_Reachable_Product_Data_Group_Allows_Lan_Without_Single_Host_Marker()
    {
        const string path = "/api/local-node/status/ticket-066-device";
        const string markerTypeName =
            "Harborline.Api.LocalNodeHost.Health.SingleHostTrustedRequestFeature";
        const string selectedOnlyPath = "/ticket-066/selected-session-product-lan-refusal";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services
            .AddTestKernelClock()
            .AddLogging()
            .AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor())
            .AddSingleton(new NodeCallerSessionToken(DesktopToken));
        await using var webApp = builder.Build();
        webApp.Use(async (context, next) =>
        {
            context.Features.Set(LanListenerRequestFeature.Instance);
            await next(context);
        });
        await using var app = new SharedHostedWebApp(
            webApp,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            webApp.Services.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            webApp.Services.GetRequiredService<TimeProvider>());
        app.MapApiRoutes(routes =>
        {
            var group = routes.MapDeviceReachableProductDataGroup();
            group.MapGet(path, (HttpContext context) => Results.Json(new
            {
                singleHostMarkerPresent = context.Features.Any(feature =>
                    feature.Key.FullName == markerTypeName),
            }));
            routes.MapSelectedSessionProductGroup().MapGet(
                selectedOnlyPath,
                () => Results.Ok());
        });
        await app.StartAsync(CancellationToken.None);
        await webApp.StartAsync(CancellationToken.None);
        app.CaptureSelectedUrl();

        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("singleHostMarkerPresent").GetBoolean());

        using var selectedOnlyResponse = await client.GetAsync(selectedOnlyPath);
        Assert.Equal(HttpStatusCode.Forbidden, selectedOnlyResponse.StatusCode);
        using var selectedOnlyBody = JsonDocument.Parse(
            await selectedOnlyResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            "route-audience.selected-session-product.unavailable",
            selectedOnlyBody.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Device_Reachable_Product_Data_Group_Refuses_When_Positive_Attribution_Is_Removed()
    {
        const string path = "/api/local-node/status/ticket-066-device-unattributed";
        var handlerCalls = 0;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services
            .AddTestKernelClock()
            .AddLogging()
            .AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor())
            .AddSingleton(new NodeCallerSessionToken(DesktopToken))
            .AddSingleton<IWebSelectedSessionPrincipalAuthority>(
                new Ticket066SelectedSessionAuthority())
            .AddSingleton<ISelectedSessionPermissionResolver>(
                new FailClosedSelectedSessionPermissionResolver())
            .AddHarborlineTenantContext<SelectedSessionTenantContext>();
        await using var webApp = builder.Build();
        await using var app = new SharedHostedWebApp(
            webApp,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            webApp.Services.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            webApp.Services.GetRequiredService<TimeProvider>());
        webApp.Use(async (context, next) =>
        {
            context.Features.Set<SelectedSessionRequestPrincipal>(null);
            await next(context);
        });
        app.MapApiRoutes(routes =>
            routes.MapDeviceReachableProductDataGroup().MapGet(path, () =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Results.Ok();
            }));
        await app.StartAsync(CancellationToken.None);
        await webApp.StartAsync(CancellationToken.None);
        app.CaptureSelectedUrl();

        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(
            "Cookie",
            $"{WebSessionCookieNames.Selected}={Ticket066SelectedHandle}");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "route-audience.device-reachable-product-data.unavailable",
            body.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, Volatile.Read(ref handlerCalls));
    }

    [Fact]
    public async Task Pre_Auth_Operational_Group_Delegates_Allowlisted_Request_Without_Single_Host_Marker()
    {
        const string path = "/ws/ticket-066-pre-auth";
        const string markerTypeName =
            "Harborline.Api.LocalNodeHost.Health.SingleHostTrustedRequestFeature";
        const string selectedOnlyPath = "/ws/ticket-066-selected-session-unattributed";
        using var services = new ServiceCollection()
            .AddTestKernelClock()
            .AddLogging()
            .AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor())
            .AddSingleton(new NodeCallerSessionToken(DesktopToken))
            .BuildServiceProvider();
        await using var app = new SharedHostedWebApp(
            services,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            services.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            services.GetRequiredService<TimeProvider>());
        app.MapApiRoutes(routes =>
        {
            var group = routes.MapPreAuthOperationalGroup();
            group.MapGet(path, (HttpContext context) => Results.Json(new
            {
                singleHostMarkerPresent = context.Features.Any(feature =>
                    feature.Key.FullName == markerTypeName),
            }));
            routes.MapSelectedSessionProductGroup().MapGet(
                selectedOnlyPath,
                () => Results.Ok());
        });
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("singleHostMarkerPresent").GetBoolean());

        using var selectedOnlyResponse = await client.GetAsync(selectedOnlyPath);
        Assert.Equal(HttpStatusCode.Forbidden, selectedOnlyResponse.StatusCode);
        using var selectedOnlyBody = JsonDocument.Parse(
            await selectedOnlyResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            "route-audience.selected-session-product.unavailable",
            selectedOnlyBody.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unenforced_Listener_Publishes_Single_Host_Trusted_Request_Marker()
    {
        const string path = "/ticket-066/single-host-selected-session-product";
        const string desktopOnlyPath = AuthorizationAdminRoutes.RouteBase;
        const string founderAdmissionPath = "/ticket-066/single-host-founder-admission-refusal";
        const string devicePath = "/api/local-node/status/ticket-066-single-host-device";
        const string markerTypeName =
            "Harborline.Api.LocalNodeHost.Health.SingleHostTrustedRequestFeature";
        using var services = new ServiceCollection()
            .AddTestKernelClock()
            .AddLogging()
            .AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor())
            .AddSingleton(new NodeCallerSessionToken(null))
            .BuildServiceProvider();
        await using var app = new SharedHostedWebApp(
            services,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            services.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            services.GetRequiredService<TimeProvider>());
        app.MapApiRoutes(routes =>
        {
            routes.MapSelectedSessionProductGroup().MapGet(path, (HttpContext context) =>
                Results.Json(new
                {
                    markerPresent = context.Features.Any(feature =>
                        feature.Key.FullName == markerTypeName),
                }));
            routes.MapDesktopPlaneOnlyGroup().MapGet(desktopOnlyPath, () => Results.Ok());
            routes.MapFounderWebAdmissionGroup(identityAuthority: null).MapGet(
                founderAdmissionPath,
                () => Results.Ok());
            routes.MapDeviceReachableProductDataGroup().MapGet(devicePath, () => Results.Ok());
        });
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("markerPresent").GetBoolean());

        using var desktopOnlyResponse = await client.GetAsync(desktopOnlyPath);
        Assert.Equal(HttpStatusCode.Forbidden, desktopOnlyResponse.StatusCode);
        using var desktopOnlyBody = JsonDocument.Parse(
            await desktopOnlyResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            "web-plane.route.unavailable",
            desktopOnlyBody.RootElement.GetProperty("code").GetString());

        using var founderAdmissionResponse = await client.GetAsync(founderAdmissionPath);
        Assert.Equal(HttpStatusCode.Forbidden, founderAdmissionResponse.StatusCode);
        using var founderAdmissionBody = JsonDocument.Parse(
            await founderAdmissionResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            "web-plane.route.unavailable",
            founderAdmissionBody.RootElement.GetProperty("code").GetString());

        using var deviceResponse = await client.GetAsync(devicePath);
        Assert.Equal(HttpStatusCode.OK, deviceResponse.StatusCode);
    }

    [Fact]
    public async Task Unclassified_Disjoint_Family_Refuses_Selected_Session_But_Allows_Desktop()
    {
        const string unclassifiedPath = "/api/local-node/ticket-066-unclassified/probe";
        const string classifiedAdmissionPath = "/ticket-066/classified-admission";
        var unclassifiedHandlerCalls = 0;
        var classifiedHandlerCalls = 0;
        using var services = new ServiceCollection()
            .AddTestKernelClock()
            .AddLogging()
            .AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor())
            .AddSingleton(new NodeCallerSessionToken(DesktopToken))
            .AddSingleton<IWebSelectedSessionPrincipalAuthority>(new Ticket066SelectedSessionAuthority())
            .BuildServiceProvider();
        await using var app = new SharedHostedWebApp(
            services,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            services.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            services.GetRequiredService<TimeProvider>());
        app.MapApiRoutes(routes =>
        {
            routes.MapGet(unclassifiedPath, () =>
            {
                Interlocked.Increment(ref unclassifiedHandlerCalls);
                return Results.Ok();
            });
            var classifiedAdmission = routes.MapFounderWebAdmissionGroup(
                new Ticket066FounderIdentityAuthority());
            classifiedAdmission.MapGet(classifiedAdmissionPath, () =>
            {
                Interlocked.Increment(ref classifiedHandlerCalls);
                return Results.Ok();
            });
        });
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        using (var desktopRequest = new HttpRequestMessage(HttpMethod.Get, unclassifiedPath))
        {
            desktopRequest.Headers.Add(NodeCallerSessionToken.HeaderName, "Bearer " + DesktopToken);
            using var desktopResponse = await client.SendAsync(desktopRequest);
            Assert.Equal(HttpStatusCode.OK, desktopResponse.StatusCode);
        }

        using (var selectedRequest = new HttpRequestMessage(HttpMethod.Get, unclassifiedPath))
        {
            selectedRequest.Headers.Add(
                "Cookie",
                $"{WebSessionCookieNames.Selected}={Ticket066SelectedHandle}");
            using var selectedResponse = await client.SendAsync(selectedRequest);
            Assert.Equal(HttpStatusCode.Forbidden, selectedResponse.StatusCode);
            using var body = JsonDocument.Parse(await selectedResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                "route-audience.unclassified",
                body.RootElement.GetProperty("code").GetString());
        }

        using (var admissionRequest = new HttpRequestMessage(HttpMethod.Get, classifiedAdmissionPath))
        {
            admissionRequest.Headers.Add(
                "Cookie",
                $"{WebSessionCookieNames.Selected}={Ticket066SelectedHandle}");
            using var admissionResponse = await client.SendAsync(admissionRequest);
            Assert.Equal(HttpStatusCode.OK, admissionResponse.StatusCode);
        }

        Assert.Equal(1, Volatile.Read(ref unclassifiedHandlerCalls));
        Assert.Equal(1, Volatile.Read(ref classifiedHandlerCalls));
    }

    [Fact]
    public async Task Migrated_Device_Reachable_Endpoint_Keeps_Selected_Session_Behavior()
    {
        const string path = "/api/local-node/status";
        var handlerCalls = 0;
        using var services = new ServiceCollection()
            .AddTestKernelClock()
            .AddLogging()
            .AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor())
            .AddSingleton(new NodeCallerSessionToken(DesktopToken))
            .AddSingleton<IWebSelectedSessionPrincipalAuthority>(new Ticket066SelectedSessionAuthority())
            .BuildServiceProvider();
        await using var app = new SharedHostedWebApp(
            services,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            services.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            services.GetRequiredService<TimeProvider>());
        app.MapApiRoutes(routes =>
            routes.MapDeviceReachableProductDataGroup().MapGet(path, () =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Results.Json(new { code = "ticket-066-migrated-handler" });
            }));
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(
            "Cookie",
            $"{WebSessionCookieNames.Selected}={Ticket066SelectedHandle}");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "ticket-066-migrated-handler",
            body.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, Volatile.Read(ref handlerCalls));
    }

    [Fact]
    public async Task Desktop_plane_only_route_refuses_when_listener_opens_no_attribution_scope()
    {
        await using var app = CreateApp();
        var desktopPlaneOnly = app.MapDesktopPlaneOnlyGroup();
        desktopPlaneOnly.MapGet("/ticket-045/no-attribution", () => Results.Ok());
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = GetBaseAddress(app) };
        using var response = await client.GetAsync("/ticket-045/no-attribution");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            WebPlaneUnavailableRouteFence.UnavailableCode,
            body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Admission_route_refuses_when_listener_opens_no_attribution_scope()
    {
        await using var app = CreateApp();
        var admission = app.MapFounderWebAdmissionGroup(identityAuthority: null);
        admission.MapPost("/ticket-045/admission/no-attribution", () => Results.Ok());
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = GetBaseAddress(app) };
        using var response = await client.PostAsync(
            "/ticket-045/admission/no-attribution",
            content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            WebPlaneUnavailableRouteFence.UnavailableCode,
            body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Current_principal_signature_refuses_when_listener_opens_no_attribution_scope()
    {
        using var signer = new NodePrincipalSigner(Enumerable.Repeat((byte)0x45, 32).ToArray());
        var roster = new NodeTeamRoster(MemberRoster.Genesis(
            Guid.Parse("29400000-0000-4000-8000-000000000001"), "fence-principal", signer.Signer,
            new Ed25519Verifier(), DateTimeOffset.UnixEpoch,
            Guid.Parse("29400000-0000-4000-8000-000000000002")));
        using var services = new ServiceCollection()
            .AddTestKernelClock()
            .AddLogging()
            .AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor())
            .AddSingleton(new NodeCallerSessionToken(null))
            .BuildServiceProvider();
        await using var app = new SharedHostedWebApp(
            services,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            services.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            services.GetRequiredService<TimeProvider>());
        var endpoint = new HostedCurrentPrincipalSignatureApiEndpoint(
            app,
            signer,
            roster,
            services.GetRequiredService<NodeCallerSessionToken>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<HostedCurrentPrincipalSignatureApiEndpoint>>());
        await endpoint.StartAsync(CancellationToken.None);
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        using var response = await client.GetAsync(CurrentPrincipalSignatureRoutes.Route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            WebPlaneUnavailableRouteFence.UnavailableCode,
            body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Pack_export_refuses_when_listener_opens_no_attribution_scope()
    {
        var activeTeam = new FixedTeamAccessor();
        using var services = new ServiceCollection()
            .AddTestKernelClock()
            .AddLogging()
            .AddSingleton<IActiveTeamAccessor>(activeTeam)
            .AddSingleton(new NodeCallerSessionToken(null))
            .BuildServiceProvider();
        await using var app = new SharedHostedWebApp(
            services,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            services.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            services.GetRequiredService<TimeProvider>());
        using var signer = new NodePrincipalSigner(Enumerable.Repeat((byte)0x46, 32).ToArray());
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec, timeProvider: TimeProvider.System);
        var endpoint = new HostedPackComposerApiEndpoint(
            app,
            exporter,
            new PackVerifier(new Ed25519Verifier(), codec),
            signer,
            activeTeam,
            Harborline.Api.LocalNodeHost.Tests.Packs.TestPackGate.AllowAll(),
            TimeProvider.System,
            services.GetRequiredService<ILogger<HostedPackComposerApiEndpoint>>());
        await endpoint.StartAsync(CancellationToken.None);
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        using var response = await client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, new
        {
            key = "ticket-045.pack",
            version = "1.0.0",
            name = "Ticket 045",
            description = "positive desktop attribution fence",
            scopeTier = "Vertical",
            contents = new[]
            {
                new
                {
                    key = "intake",
                    kind = "FormDefinition",
                    version = "1.0.0",
                    content = new { title = "Intake", assignee = "role:approver" },
                },
            },
            dependencies = Array.Empty<object>(),
            capabilityRequirements = new[] { "forms.dynamic" },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            WebPlaneUnavailableRouteFence.UnavailableCode,
            body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Pack_compose_family_refuses_when_listener_opens_no_attribution_scope()
    {
        var activeTeam = new FixedTeamAccessor();
        using var services = new ServiceCollection()
            .AddTestKernelClock()
            .AddLogging()
            .AddInMemoryAssetTypeSystem()
            .AddSingleton<IActiveTeamAccessor>(activeTeam)
            .AddSingleton(new NodeCallerSessionToken(null))
            .BuildServiceProvider();
        await using var app = new SharedHostedWebApp(
            services,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            services.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            services.GetRequiredService<TimeProvider>());
        using var signer = new NodePrincipalSigner(Enumerable.Repeat((byte)0x47, 32).ToArray());
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec, timeProvider: TimeProvider.System);
        var ceremony = new ComposeCeremony(
            services.GetRequiredService<IEntityTypeRegistry>(),
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new InMemoryDraftCompositionStore(), clock: TimeProvider.System);
        var endpoint = new HostedPackComposeApiEndpoint(
            app,
            ceremony,
            exporter,
            signer,
            activeTeam,
            Harborline.Api.LocalNodeHost.Tests.Packs.TestPackGate.AllowAll(),
            TimeProvider.System,
            services.GetRequiredService<ILogger<HostedPackComposeApiEndpoint>>());
        await endpoint.StartAsync(CancellationToken.None);
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        using var response = await client.GetAsync($"{PackComposeRoutes.ComposeRoute}/missing");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            WebPlaneUnavailableRouteFence.UnavailableCode,
            body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Desktop_bootstrap_bearer_can_reach_a_desktop_plane_only_route()
    {
        using var services = new ServiceCollection()
            .AddTestKernelClock()
            .AddLogging()
            .AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor())
            .AddSingleton(new NodeCallerSessionToken(DesktopToken))
            .BuildServiceProvider();
        await using var app = new SharedHostedWebApp(
            services,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            services.GetRequiredService<
                Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
            services.GetRequiredService<TimeProvider>());
        app.MapApiRoutes(routes =>
        {
            var desktopPlaneOnly = routes.MapDesktopPlaneOnlyGroup();
            desktopPlaneOnly.MapGet("/ticket-045/desktop", () => Results.Ok());
        });
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ticket-045/desktop");
        request.Headers.Add(NodeCallerSessionToken.HeaderName, "Bearer " + DesktopToken);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Legacy_founder_session_can_reach_a_desktop_plane_only_route()
    {
        using var services = new ServiceCollection()
            .AddTestKernelClock()
            .AddLogging()
            .AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor())
            .AddSingleton(new NodeCallerSessionToken(DesktopToken))
            .AddSingleton<INodeWebSessionAuthority, AcceptingLegacyFounderAuthority>()
            .BuildServiceProvider();
        await using var app = new SharedHostedWebApp(
            services,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new LocalNodeExecutableEndpointRegistry(),
            services.GetRequiredService<
                Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
            services.GetRequiredService<TimeProvider>());
        app.MapApiRoutes(routes =>
        {
            var desktopPlaneOnly = routes.MapDesktopPlaneOnlyGroup();
            desktopPlaneOnly.MapGet("/ticket-045/legacy-founder", () => Results.Ok());
        });
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        using var response = await client.GetAsync("/ticket-045/legacy-founder");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        return builder.Build();
    }

    private static Uri GetBaseAddress(WebApplication app)
    {
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();
        return new Uri(Assert.Single(addresses!.Addresses));
    }

    private sealed class NoTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active => null;

        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;

        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class FixedTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = new(
            new TeamId(Guid.Parse("04500000-0000-0000-0000-000000000001")),
            "Ticket 045",
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;

        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class AllowAllAuthorizationContext : IAuthorizationContext
    {
        public bool HasPermission(string permission) => true;
    }

    private sealed class AcceptingLegacyFounderAuthority : INodeWebSessionAuthority
    {
        public bool IsEnabled => true;

        public Task<bool> TryAuthenticateAsync(HttpContext context) => Task.FromResult(true);

        public Task<WebLoginAttemptResult> LoginAsync(
            string? username,
            string? password,
            CancellationToken ct) =>
            Task.FromResult(new WebLoginAttemptResult(null, null));

        public Task<bool> LogoutAsync(HttpContext context, CancellationToken ct) => Task.FromResult(false);

        public void IssueSessionCookie(HttpContext context, WebLoginResult login)
        {
        }

        public void ClearSessionCookie(HttpContext context)
        {
        }

        public Task<WebSessionSummary?> DescribeAsync(HttpContext context, CancellationToken ct) =>
            Task.FromResult<WebSessionSummary?>(null);
    }

    private sealed class Ticket066SelectedSessionAuthority : IWebSelectedSessionPrincipalAuthority
    {
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(
                    selectedHandle,
                    Ticket066SelectedHandle,
                    StringComparison.Ordinal)
                ? new SelectedSessionRequestPrincipal(
                    accountId: "ticket-066-account",
                    tenantId: new TenantId("06600000-0000-0000-0000-000000000001"),
                    principalUserId: new PrincipalUserId("ticket-066-principal"),
                    canonicalParty: new CanonicalPartyReference("ticket-066-party"),
                    membershipId: "ticket-066-membership",
                    membershipOwnerVersion: 1,
                    pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("ticket-066-grant", 1)],
                    authorizationEpoch: 1,
                    sessionCorrelationId: "ticket-066-session-correlation",
                    coordinationCorrelationId: "ticket-066-coordination-correlation")
                : null);
    }

    private sealed class Ticket066FounderIdentityAuthority : IWebSelectedSessionIdentityAuthority
    {
        public Task<SelectedSessionIdentity?> DescribeAsync(
            string? selectedHandle,
            SelectedSessionRequestPrincipal principal,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SelectedSessionIdentity?>(new SelectedSessionIdentity(
                principal.AccountId,
                principal.CanonicalParty,
                DisplayName: null,
                principal.TenantId,
                TenantDisplayName: null,
                SelectedSessionMembership.Founder,
                DateTimeOffset.UtcNow.AddMinutes(5)));
    }
}
