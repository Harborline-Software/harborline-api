using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

public sealed class LocalNodeExecutableEndpointRegistryTests
{
    [Fact]
    public void Current_BeforeSeal_Throws()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        Assert.False(registry.IsSealed);
        Assert.Throws<InvalidOperationException>(() => registry.Current);
    }

    [Fact]
    public void Seal_Captures_Normalizes_And_Orders_Routes()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        registry.Seal(
            [Source(Route("/z", "post", "GET", "get"), Route("/a"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false);

        var snapshot = registry.Current;
        Assert.True(registry.IsSealed);
        Assert.Equal(LocalNodeExecutableEndpointSnapshot.SupportedSchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(LocalNodeExecutableEndpointSnapshot.ProducerId, snapshot.Producer);
        Assert.True(snapshot.ListenerCallerAuthEnforced);
        Assert.False(snapshot.PublicStaticFilesEnabled);
        Assert.Equal(["/a", "/z"], snapshot.Endpoints.Select(endpoint => endpoint.RoutePattern));
        Assert.Equal<string>(["*"], snapshot.Endpoints[0].HttpMethods);
        Assert.Equal<string>(["GET", "POST"], snapshot.Endpoints[1].HttpMethods);
    }

    [Fact]
    public void Seal_Reports_Explicit_Route_Fence_Classification_Per_Method()
    {
        var routes = new TestEndpointRouteBuilder();
        var desktopPlaneOnly = routes.MapDesktopPlaneOnlyGroup();
        desktopPlaneOnly.MapGet("/api/local-node/forms/classified", () => Results.Ok());
        routes.MapPost("/api/local-node/ticket-066-unclassified/probe", () => Results.Ok());

        var registry = new LocalNodeExecutableEndpointRegistry();
        registry.Seal(
            routes.DataSources,
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false);

        var evidenceProperty = typeof(LocalNodeExecutableEndpoint)
            .GetProperty("MethodRouteFences");

        Assert.NotNull(evidenceProperty);
        Assert.Equal(
            new[]
            {
                "/api/local-node/forms/classified|GET|DesktopPlaneOnly",
                "/api/local-node/ticket-066-unclassified/probe|POST|none",
            },
            registry.Current.Endpoints.SelectMany(endpoint =>
                Assert.IsAssignableFrom<System.Collections.IEnumerable>(
                        evidenceProperty.GetValue(endpoint))
                    .Cast<object>()
                    .Select(evidence =>
                    {
                        var evidenceType = evidence.GetType();
                        var method = evidenceType.GetProperty("HttpMethod")!.GetValue(evidence);
                        var fence = evidenceType.GetProperty("RouteFenceKind")!.GetValue(evidence);
                        return $"{endpoint.RoutePattern}|{method}|{fence ?? "none"}";
                    })));
    }

    [Fact]
    public void Seal_Captures_All_Named_Audience_Markers()
    {
        const string selectedPath = "/ticket-066/selected-marker";
        const string devicePath = "/api/local-node/status/ticket-066-device-marker";
        const string preAuthPath = "/ws/{ticket066Id}";
        var routes = new TestEndpointRouteBuilder();
        routes.MapSelectedSessionProductGroup().MapGet(selectedPath, () => Results.Ok());
        routes.MapDeviceReachableProductDataGroup().MapGet(devicePath, () => Results.Ok());
        routes.MapPreAuthOperationalGroup().MapGet(preAuthPath, () => Results.Ok());

        var registry = new LocalNodeExecutableEndpointRegistry();
        registry.Seal(
            routes.DataSources,
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false);

        Assert.Equal(
            new[]
            {
                "/api/local-node/status/ticket-066-device-marker|GET|DeviceReachableProductData",
                "/ticket-066/selected-marker|GET|SelectedSessionProduct",
                "/ws/{ticket066Id}|GET|PreAuthOperational",
            },
            registry.Current.Endpoints.SelectMany(endpoint =>
                endpoint.MethodRouteFences.Select(method =>
                    $"{endpoint.RoutePattern}|{method.HttpMethod}|{method.RouteFenceKind}")));
    }

    [Theory]
    [InlineData("SelectedSessionProduct", "/ticket-066/selected-filter-removed")]
    [InlineData("DeviceReachableProductData", "/api/local-node/status/ticket-066-filter-removed")]
    [InlineData("PreAuthOperational", "/ws/ticket-066-filter-removed")]
    public void Seal_Refuses_When_New_Audience_Filter_Is_Removed_Before_Marker_Stamp(
        string kind,
        string route)
    {
        var routes = new TestEndpointRouteBuilder();
        RouteGroupBuilder group = kind switch
        {
            "SelectedSessionProduct" => routes.MapSelectedSessionProductGroup(),
            "DeviceReachableProductData" => routes.MapDeviceReachableProductDataGroup(),
            "PreAuthOperational" => routes.MapPreAuthOperationalGroup(),
            _ => throw new InvalidOperationException("Unexpected test audience."),
        };
        EndpointBuilder? mutatedEndpoint = null;
        ((IEndpointConventionBuilder)group).Add(endpoint =>
        {
            endpoint.FilterFactories.Clear();
            mutatedEndpoint = endpoint;
        });
        group.MapGet(route, () => Results.Ok());

        var registry = new LocalNodeExecutableEndpointRegistry();
        var exception = Assert.Throws<RouteFenceViolationException>(() => registry.Seal(
            routes.DataSources,
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains(
            $"{kind} route fence filter was removed",
            exception.Message,
            StringComparison.Ordinal);
        Assert.NotNull(mutatedEndpoint);
        Assert.DoesNotContain(mutatedEndpoint.Metadata, metadata => metadata is RouteFenceMetadata);
        Assert.False(registry.IsSealed);
    }

    [Fact]
    public void Seal_Is_Deterministic_Across_Source_Order()
    {
        var first = new LocalNodeExecutableEndpointRegistry();
        var second = new LocalNodeExecutableEndpointRegistry();

        first.Seal(
            [Source(Route("/b", "POST")), Source(Route("/a", "GET"))],
            listenerCallerAuthEnforced: false,
            publicStaticFilesEnabled: true);
        second.Seal(
            [Source(Route("/a", "GET")), Source(Route("/b", "POST"))],
            listenerCallerAuthEnforced: false,
            publicStaticFilesEnabled: true);

        Assert.Equal(
            Project(first.Current),
            Project(second.Current));
    }

    [Fact]
    public void Seal_SamePattern_DifferentMethods_Merges()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        registry.Seal(
            [Source(Route("/items", "GET"), Route("/items", "POST"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false);

        var endpoint = Assert.Single(registry.Current.Endpoints);
        Assert.Equal<string>(["GET", "POST"], endpoint.HttpMethods);
    }

    [Fact]
    public void Seal_Uses_MostSignificant_HttpMethodMetadata()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();
        var endpoint = Route("/items", "GET");
        var builder = new RouteEndpointBuilder(
            endpoint.RequestDelegate,
            endpoint.RoutePattern,
            endpoint.Order);
        foreach (var metadata in endpoint.Metadata)
        {
            builder.Metadata.Add(metadata);
        }
        builder.Metadata.Add(new HttpMethodMetadata(["PATCH"]));

        registry.Seal(
            [Source(builder.Build())],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false);

        Assert.Equal<string>(["PATCH"], Assert.Single(registry.Current.Endpoints).HttpMethods);
    }

    [Fact]
    public void Seal_Equivalent_ParameterNames_And_TrailingSlash_Collide()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Seal(
            [Source(Route("/items/{id}", "GET"), Route("/ITEMS/{name}/", "get"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains("Duplicate or overlapping", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seal_Differing_ParameterDefaults_Do_Not_Disambiguate_InboundRoutes()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.Seal(
            [Source(Route("/items/{id=one}", "GET"), Route("/items/{name=two}", "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));
    }

    [Fact]
    public void Seal_Reordered_ConjunctivePolicies_Do_Not_Disambiguate_InboundRoutes()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.Seal(
            [Source(
                Route("/items/{id:int:min(1)}", "GET"),
                Route("/items/{name:min(1):int}", "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));
    }

    [Fact]
    public void Seal_Preserves_CaseSensitive_PolicyArguments()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        registry.Seal(
            [Source(
                Route("/items/{id:custom(alpha)}", "GET"),
                Route("/items/{name:custom(ALPHA)}", "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false);

        Assert.Equal(2, registry.Current.Endpoints.Length);
    }

    [Fact]
    public void Seal_CorsPreflight_Metadata_Refuses_Until_Profiled()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();
        var builder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/items"),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"], acceptCorsPreflight: true));

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Seal(
            [Source(builder.Build())],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains("CORS preflight", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seal_DuplicateMethodAndPattern_Throws()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Seal(
            [Source(Route("/items", "GET"), Route("/ITEMS", "get"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains("Duplicate or overlapping", exception.Message, StringComparison.Ordinal);
        Assert.False(registry.IsSealed);
    }

    [Fact]
    public void Seal_WildcardAndSpecificMethod_Throws()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.Seal(
            [Source(Route("/items"), Route("/items", "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));
    }

    [Fact]
    public void Seal_UnknownEndpointKind_Throws()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();
        var endpoint = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(),
            "non-route");

        Assert.Throws<InvalidOperationException>(() => registry.Seal(
            [Source(endpoint)],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));
    }

    [Fact]
    public void Seal_NonRootedRoute_Throws()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Seal(
            [Source(Route("health", "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains("not rooted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seal_SecondInvocation_Throws()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();
        registry.Seal(
            [Source(Route("/one", "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false);

        Assert.Throws<InvalidOperationException>(() => registry.Seal(
            [Source(Route("/two", "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));
    }

    [Fact]
    public void Seal_Accepts_Each_Fenced_Base_With_Its_Expected_Marker()
    {
        var routes = new TestEndpointRouteBuilder();
        var desktopPlaneOnly = routes.MapDesktopPlaneOnlyGroup();
        var founderAdmission = routes.MapFounderWebAdmissionGroup(identityAuthority: null);
        desktopPlaneOnly.MapGet("/api/local-node/forms/example", () => Results.Ok());
        desktopPlaneOnly.MapGet("/api/local-node/comms/example", () => Results.Ok());
        desktopPlaneOnly.MapPost("/api/session/founder-bind", () => Results.Ok());
        founderAdmission.MapPost("/api/local-node/admission/example", () => Results.Ok());

        var registry = new LocalNodeExecutableEndpointRegistry();
        registry.Seal(
            routes.DataSources,
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false);

        Assert.Equal(4, registry.Current.Endpoints.Length);
    }

    [Fact]
    public void Seal_Refuses_Device_Reachable_Audience_Outside_Lan_Allowlist()
    {
        var routes = new TestEndpointRouteBuilder();
        var deviceReachable = routes.MapDeviceReachableProductDataGroup();
        deviceReachable.MapGet(
            "/ticket-066/not-lan-allowlisted",
            () => Results.Ok());

        var registry = new LocalNodeExecutableEndpointRegistry();
        var exception = Assert.Throws<RouteFenceViolationException>(() => registry.Seal(
            routes.DataSources,
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains(
            "/ticket-066/not-lan-allowlisted",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("LAN", exception.Message, StringComparison.Ordinal);
        Assert.False(registry.IsSealed);
    }

    [Theory]
    [InlineData("/ticket-066/not-pre-auth-allowlisted")]
    [InlineData("/health/{value?}")]
    public void Seal_Refuses_Pre_Auth_Audience_Unless_Template_Is_Provably_Allowlisted(
        string route)
    {
        var routes = new TestEndpointRouteBuilder();
        var preAuth = routes.MapPreAuthOperationalGroup();
        preAuth.MapGet(route, () => Results.Ok());

        var registry = new LocalNodeExecutableEndpointRegistry();
        var exception = Assert.Throws<RouteFenceViolationException>(() => registry.Seal(
            routes.DataSources,
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains(route, exception.Message, StringComparison.Ordinal);
        Assert.Contains("pre-caller-auth allowlist", exception.Message, StringComparison.Ordinal);
        Assert.False(registry.IsSealed);
    }

    [Fact]
    public void Seal_Refuses_When_Later_Convention_Removes_Fence_Filter_And_No_Marker_Is_Stamped()
    {
        var routes = new TestEndpointRouteBuilder();
        var desktopPlaneOnly = routes.MapDesktopPlaneOnlyGroup();
        EndpointBuilder? mutatedEndpoint = null;
        ((IEndpointConventionBuilder)desktopPlaneOnly).Add(endpoint =>
        {
            endpoint.FilterFactories.Clear();
            mutatedEndpoint = endpoint;
        });
        desktopPlaneOnly.MapGet("/api/local-node/forms/filter-removed", () => Results.Ok());

        var registry = new LocalNodeExecutableEndpointRegistry();
        var exception = Assert.Throws<RouteFenceViolationException>(() => registry.Seal(
            routes.DataSources,
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains("DesktopPlaneOnly route fence filter was removed", exception.Message);
        Assert.Contains("/api/local-node/forms/filter-removed", exception.Message);
        Assert.NotNull(mutatedEndpoint);
        Assert.DoesNotContain(mutatedEndpoint.Metadata, metadata => metadata is RouteFenceMetadata);
        Assert.False(registry.IsSealed);
    }

    [Fact]
    public void Seal_Catches_Route_Mapped_Outside_Group_Under_Fenced_Base()
    {
        var routes = new TestEndpointRouteBuilder();
        var desktopPlaneOnly = routes.MapDesktopPlaneOnlyGroup();
        desktopPlaneOnly.MapGet("/api/local-node/forms/grouped", () => Results.Ok());
        routes.MapGet("/api/local-node/forms/escaped", () => Results.Ok());

        var registry = new LocalNodeExecutableEndpointRegistry();
        var exception = Assert.Throws<RouteFenceViolationException>(() => registry.Seal(
            routes.DataSources,
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains("/api/local-node/forms/escaped", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DesktopPlaneOnly", exception.Message, StringComparison.Ordinal);
        Assert.Contains("actual marker: 'none'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seal_Catches_Current_Principal_Signature_Mapped_Outside_Desktop_Group()
    {
        var routes = new TestEndpointRouteBuilder();
        routes.MapGet(CurrentPrincipalSignatureRoutes.Route, () => Results.Ok());

        var registry = new LocalNodeExecutableEndpointRegistry();
        var exception = Assert.Throws<RouteFenceViolationException>(() => registry.Seal(
            routes.DataSources,
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains(CurrentPrincipalSignatureRoutes.Route, exception.Message, StringComparison.Ordinal);
        Assert.Contains("DesktopPlaneOnly", exception.Message, StringComparison.Ordinal);
        Assert.Contains("actual marker: 'none'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seal_Catches_Pack_Export_Mapped_Outside_Desktop_Group()
    {
        var routes = new TestEndpointRouteBuilder();
        routes.MapPost(PackComposerRoutes.ExportRoute, () => Results.Ok());

        var registry = new LocalNodeExecutableEndpointRegistry();
        var exception = Assert.Throws<RouteFenceViolationException>(() => registry.Seal(
            routes.DataSources,
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains(PackComposerRoutes.ExportRoute, exception.Message, StringComparison.Ordinal);
        Assert.Contains("DesktopPlaneOnly", exception.Message, StringComparison.Ordinal);
        Assert.Contains("actual marker: 'none'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seal_Catches_Pack_Compose_Route_Mapped_Outside_Desktop_Group()
    {
        var routes = new TestEndpointRouteBuilder();
        routes.MapPost($"{PackComposeRoutes.ComposeRoute}/draft/export", () => Results.Ok());

        var registry = new LocalNodeExecutableEndpointRegistry();
        var exception = Assert.Throws<RouteFenceViolationException>(() => registry.Seal(
            routes.DataSources,
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains(PackComposeRoutes.ComposeRoute, exception.Message, StringComparison.Ordinal);
        Assert.Contains("DesktopPlaneOnly", exception.Message, StringComparison.Ordinal);
        Assert.Contains("actual marker: 'none'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seal_Rejects_Unmarked_Parameterized_Route_That_Overlaps_Fenced_Base()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        var exception = Assert.Throws<RouteFenceViolationException>(() => registry.Seal(
            [Source(Route("/api/local-node/{family}/leak", "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains("/api/local-node/{family}/leak", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DesktopPlaneOnly", exception.Message, StringComparison.Ordinal);
        Assert.Contains("actual marker: 'none'", exception.Message, StringComparison.Ordinal);
        Assert.False(registry.IsSealed);
    }

    [Fact]
    public void Seal_Rejects_Unmarked_CatchAll_Route_That_Overlaps_Fenced_Base()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        var exception = Assert.Throws<RouteFenceViolationException>(() => registry.Seal(
            [Source(Route("/api/local-node/{**rest}", "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains("/api/local-node/{**rest}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DesktopPlaneOnly", exception.Message, StringComparison.Ordinal);
        Assert.Contains("actual marker: 'none'", exception.Message, StringComparison.Ordinal);
        Assert.False(registry.IsSealed);
    }

    [Fact]
    public void Seal_Allows_Parameterized_Route_Proven_Disjoint_From_Fenced_Bases()
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        registry.Seal(
            [Source(Route("/api/other/{family}/thing", "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false);

        Assert.Equal(
            "/api/other/{family}/thing",
            Assert.Single(registry.Current.Endpoints).RoutePattern);
    }

    [Fact]
    public void Seal_Rejects_The_Wrong_Fence_Kind()
    {
        var routes = new TestEndpointRouteBuilder();
        var founderAdmission = routes.MapFounderWebAdmissionGroup(identityAuthority: null);
        founderAdmission.MapGet("/api/local-node/forms/wrong-fence", () => Results.Ok());

        var registry = new LocalNodeExecutableEndpointRegistry();
        var exception = Assert.Throws<RouteFenceViolationException>(() => registry.Seal(
            routes.DataSources,
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains("DesktopPlaneOnly", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FounderWebAdmission", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/health", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/health/", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/ws/connect", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/api/session/login", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/api/session/login/", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/api/session/account-challenge", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/api/session/account-challenge/", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/api/session/antiforgery", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/api/session/antiforgery/", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/api/session/select", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/api/session/select/", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/api/session/logout", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/api/session/logout/", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/health/details", NodeListenerCallerAuthPolicy.CallerCredentialRequiredPolicy)]
    [InlineData("/api/session/login/reset", NodeListenerCallerAuthPolicy.CallerCredentialRequiredPolicy)]
    [InlineData("/api/session/account-challenge/reset", NodeListenerCallerAuthPolicy.CallerCredentialRequiredPolicy)]
    [InlineData("/api/session/antiforgery/reset", NodeListenerCallerAuthPolicy.CallerCredentialRequiredPolicy)]
    [InlineData("/api/session/select/reset", NodeListenerCallerAuthPolicy.CallerCredentialRequiredPolicy)]
    [InlineData("/api/session/logout/reset", NodeListenerCallerAuthPolicy.CallerCredentialRequiredPolicy)]
    [InlineData("/wsx", NodeListenerCallerAuthPolicy.CallerCredentialRequiredPolicy)]
    [InlineData("/api/local-node/status", NodeListenerCallerAuthPolicy.CallerCredentialRequiredPolicy)]
    public void Seal_Uses_The_Request_Middleware_Allowlist(string route, string expected)
    {
        var registry = new LocalNodeExecutableEndpointRegistry();
        registry.Seal(
            [Source(Route(route, "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false);

        Assert.Equal(expected, Assert.Single(registry.Current.Endpoints).ListenerCallerAuthPolicy);
    }

    // The four templates that once lived here — /{**rest}, /api/session/{value},
    // /api/{area}/{action} and /api/{**rest} — moved to the fence-violation theory below. Each
    // overlaps a fenced base, so the route-fence assertion refuses them BEFORE the allowlist boundary
    // is reached. They still refuse; they simply no longer prove THIS property.
    //
    // The three that remain still do. `/api/{area}/login` is the one that matters: it is
    // parameterized, it straddles the boundary (it matches the allowlisted /api/session/login and
    // also paths that are not allowlisted), and it is provably disjoint from every fenced base
    // because its third segment is a literal that matches none of them. Do not "restore breadth"
    // here with templates outside the allowlist's reach — a template that touches no allowlisted
    // path is uniformly classified and never refuses, so it tests nothing.
    [Theory]
    [InlineData("/health/{value?}")]
    [InlineData("/health/{value=details}")]
    [InlineData("/api/{area}/login")]
    public void Seal_Mixed_ListenerPolicy_Templates_Refuse(string route)
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Seal(
            [Source(Route(route, "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains("allowlist boundary", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/{**rest}")]
    [InlineData("/api/session/{value}")]
    [InlineData("/api/{area}/{action}")]
    [InlineData("/api/{**rest}")]
    public void Seal_Mixed_ListenerPolicy_Templates_Overlapping_A_Fence_Refuse_As_Fence_Violations(
        string route)
    {
        var registry = new LocalNodeExecutableEndpointRegistry();

        var exception = Assert.Throws<RouteFenceViolationException>(() => registry.Seal(
            [Source(Route(route, "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false));

        Assert.Contains("route-fence marker", exception.Message, StringComparison.Ordinal);
        Assert.False(registry.IsSealed);
    }

    [Theory]
    [InlineData("/ws/{id}", NodeListenerCallerAuthPolicy.PreCallerAuthAllowlistedPolicy)]
    [InlineData("/wsx/{id}", NodeListenerCallerAuthPolicy.CallerCredentialRequiredPolicy)]
    [InlineData("/api/llm/{**rest}", NodeListenerCallerAuthPolicy.CallerCredentialRequiredPolicy)]
    public void Seal_Uniform_ListenerPolicy_Templates_Are_Classified(string route, string expected)
    {
        var registry = new LocalNodeExecutableEndpointRegistry();
        registry.Seal(
            [Source(Route(route, "GET"))],
            listenerCallerAuthEnforced: true,
            publicStaticFilesEnabled: false);

        Assert.Equal(expected, Assert.Single(registry.Current.Endpoints).ListenerCallerAuthPolicy);
    }

    private static EndpointDataSource Source(params Endpoint[] endpoints) =>
        new DefaultEndpointDataSource(endpoints);

    private static RouteEndpoint Route(string pattern, params string[] methods)
    {
        var builder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            order: 0);
        if (methods.Length > 0)
        {
            builder.Metadata.Add(new HttpMethodMetadata(methods));
        }

        return (RouteEndpoint)builder.Build();
    }

    private static string[] Project(LocalNodeExecutableEndpointSnapshot snapshot) =>
        [
            snapshot.SchemaVersion,
            snapshot.Producer,
            snapshot.ListenerCallerAuthEnforced.ToString(),
            snapshot.PublicStaticFilesEnabled.ToString(),
            .. snapshot.Endpoints.Select(endpoint =>
                $"{endpoint.RoutePattern}|{string.Join(',', endpoint.HttpMethods)}|" +
                endpoint.ListenerCallerAuthPolicy),
        ];

    private sealed class TestEndpointRouteBuilder : IEndpointRouteBuilder
    {
        internal TestEndpointRouteBuilder()
        {
            ServiceProvider = new ServiceCollection()
                .AddRouting()
                .BuildServiceProvider();
        }

        public IServiceProvider ServiceProvider { get; }
        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() =>
            new ApplicationBuilder(ServiceProvider);
    }
}
