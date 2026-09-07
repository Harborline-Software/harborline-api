using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>Automatable W4 transport conformance coverage for the LAN listener.</summary>
public sealed class LocalNodeLanTransportTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(), $"harborline-lan-pfx-tests-{Guid.NewGuid():N}");

    private static readonly string[] NormativeAllowlist =
    [
        "/api/local-node/status", "/api/local-node/sync-status", "/api/local-node/teams",
        "/api/local-node/contacts", "/api/local-node/entities", "/api/local-node/documents",
        "/api/local-node/calendar", "/api/local-node/scheduling/definitions",
        "/api/local-node/scheduling/subjects", "/api/local-node/scheduling/appointments",
        "/api/local-node/scheduling/events", "/api/local-node/scheduling/resources/availability",
        "/api/local-node/org-branding", "/api/local-node/asset-registry",
        "/api/local-node/audit-events", "/api/local-node/accounting",
        "/api/local-node/accounting-periods", "/api/local-node/bank-accounts",
        "/api/local-node/bills", "/api/local-node/chart-of-accounts",
        "/api/local-node/invoices", "/api/local-node/journal-entries",
        "/api/local-node/leases", "/api/local-node/payments", "/api/local-node/payroll",
        "/api/local-node/properties", "/api/local-node/recurring-invoices",
        "/api/local-node/approval-tasks", "/api/local-node/kg-action-tasks", "/api/local-node/kg",
        "/api/local-node/reports", "/api/local-node/charts", "/api/local-node/workflows/definitions",
        "/api/local-node/workflow-confirmations", "/api/local-node/workflow-run-report",
        "/api/local-node/navigation/workspaces", "/api/local-node/packs/installed",
        "/api/local-node/packs/graph",
    ];

    [Fact(DisplayName = "W4-C1: LAN remains opt-in with the normative defaults")]
    public void Defaults_AreLoopbackOnlyAndLanDisabled()
    {
        var options = new LocalNodeOptions();

        Assert.False(options.Lan.Enabled);
        Assert.Null(options.Lan.BindAddress);
        Assert.Equal(7443, options.Lan.Port);
        Assert.Null(options.Lan.AdvertisedHost);
        Assert.Null(options.Lan.Certificate);
    }

    [Theory(DisplayName = "W4-C2/C4: invalid LAN startup preconditions fail closed")]
    [InlineData("0.0.0.0", "lan.example.test", 7443)]
    [InlineData("127.0.0.1", "lan.example.test", 7443)]
    [InlineData("192.0.2.10", "lan.example.test", 7442)]
    [InlineData("192.0.2.10", "https://lan.example.test", 7443)]
    public void InvalidAddressPortOrAdvertisedHost_RefusesStartup(
        string bindAddress, string advertisedHost, int port)
    {
        var options = NewOptions(bindAddress, advertisedHost, port);

        Assert.Throws<InvalidOperationException>(() => LocalNodeLanValidation.ValidateAndResolve(
            options,
            listenerCallerAuthEnforced: true,
            timeProvider: TimeProvider.System,
            localAddresses: [IPAddress.Parse("192.0.2.10")],
            aspNetCoreUrls: "http://127.0.0.1:0"));
    }

    [Fact(DisplayName = "W4-C2: ListenerCallerAuthEnforced is a startup precondition")]
    public void CallerAuthSnapshotFalse_RefusesStartup()
    {
        var options = NewOptions("192.0.2.10", "lan.example.test", 7443);
        using var certificate = CreateCertificate("lan.example.test", IPAddress.Parse("192.0.2.10"));
        var path = WritePfx(certificate);
        try
        {
            options.Certificate = Path.GetFileName(path);
            Assert.Throws<InvalidOperationException>(() => LocalNodeLanValidation.ValidateAndResolve(
                options,
                listenerCallerAuthEnforced: false,
                timeProvider: TimeProvider.System,
                localAddresses: [IPAddress.Parse("192.0.2.10")],
                aspNetCoreUrls: "http://127.0.0.1:0"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "W4-C2/C4: enabled LAN without a certificate or assigned interface refuses startup")]
    public void MissingCertificateOrInterface_RefusesStartup()
    {
        var options = NewOptions("192.0.2.10", "lan.example.test", 7443);

        Assert.Throws<InvalidOperationException>(() => LocalNodeLanValidation.ValidateAndResolve(
            options,
            listenerCallerAuthEnforced: true,
            timeProvider: TimeProvider.System,
            localAddresses: [IPAddress.Parse("192.0.2.11")],
            aspNetCoreUrls: "http://127.0.0.1:0"));

        options.Certificate = Path.Combine(Path.GetTempPath(), "missing-w4-leaf.pfx");
        Assert.Throws<InvalidOperationException>(() => LocalNodeLanValidation.ValidateAndResolve(
            options,
            listenerCallerAuthEnforced: true,
            timeProvider: TimeProvider.System,
            localAddresses: [IPAddress.Parse("192.0.2.10")],
            aspNetCoreUrls: "http://127.0.0.1:0"));
    }

    [Fact(DisplayName = "W4-C3/C5: valid node leaf requires SAN, private key, and server-auth")]
    public void ValidCertificate_IsAcceptedOnlyForTheConfiguredEndpoint()
    {
        var bind = IPAddress.Parse("192.0.2.10");
        using var certificate = CreateCertificate("lan.example.test", bind);
        var path = WritePfx(certificate);
        try
        {
            var options = NewOptions(bind.ToString(), "lan.example.test", 7443);
            options.Certificate = Path.GetFileName(path);
            options.CertificatePassword = "test-password";
            using var resolved = LocalNodeLanValidation.ValidateAndResolve(
                options,
                listenerCallerAuthEnforced: true,
                timeProvider: TimeProvider.System,
                localAddresses: [bind],
                aspNetCoreUrls: "http://127.0.0.1:0",
                dataDirectory: _dataDirectory);

            Assert.True(resolved.HasPrivateKey);
            Assert.Contains(resolved.Extensions.OfType<X509SubjectAlternativeNameExtension>(), _ => true);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact(DisplayName = "W4-C3: plaintext or non-loopback ASPNETCORE_URLS cannot weaken the listener")]
    public void EnvironmentUrls_OnlyPermitLoopbackHttp()
    {
        Assert.Throws<InvalidOperationException>(() => LocalNodeLanValidation.ValidateEnvironmentUrls(
            "http://0.0.0.0:7443"));
        Assert.Throws<InvalidOperationException>(() => LocalNodeLanValidation.ValidateEnvironmentUrls(
            "https://127.0.0.1:7443"));
        LocalNodeLanValidation.ValidateEnvironmentUrls("http://127.0.0.1:0");
    }

    [Fact(DisplayName = "W4-B2: LAN-disabled host starts with a non-loopback ASPNETCORE_URLS")]
    public async Task LanDisabled_NonLoopbackEnvironmentUrl_Starts()
    {
        var (app, provider) = NewSharedApp(
            new LocalNodeOptions(),
            urlsOverride: "http://0.0.0.0:0");
        try
        {
            await app.StartAsync(CancellationToken.None);
            Assert.NotNull(app.SelectedUrl);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    [Fact(DisplayName = "W4-C4: enabled LAN host binds exactly loopback and LAN endpoints")]
    public async Task EnabledLan_BindsLoopbackAndLan()
    {
        var bindAddress = FindAssignedIpv4();
        using var certificate = CreateCertificate(bindAddress.ToString(), bindAddress);
        var certificatePath = WritePfx(certificate);

        var options = NewOptions(bindAddress.ToString(), bindAddress.ToString(), 7443);
        options.Certificate = Path.GetFileName(certificatePath);
        options.CertificatePassword = "test-password";
        var (app, provider) = NewSharedApp(
            new LocalNodeOptions { DataDirectory = _dataDirectory, HealthPort = 0, Lan = options },
            sessionToken: "lan-bind-test-token",
            urlsOverride: "http://127.0.0.1:0");
        try
        {
            await app.StartAsync(CancellationToken.None);
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                $"http://127.0.0.1:{new Uri(app.SelectedUrl!).Port}",
                $"https://{bindAddress}:7443",
            };

            Assert.Equal(expected, addresses.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            await provider.DisposeAsync();
            File.Delete(certificatePath);
        }
    }

    [Fact(DisplayName = "W4-C10-C14: pairing redeem rate substrate keeps the normative caps")]
    public void PairingRedeemLimiter_UsesSpecCaps()
    {
        var limiter = new PairingRedeemRateLimiter(clock: TimeProvider.System);

        for (var index = 0; index < 20; index++)
        {
            Assert.True(limiter.TryAcquire("198.51.100.7", "tenant-a"));
        }
        Assert.False(limiter.TryAcquire("198.51.100.7", "tenant-a"));
        Assert.True(limiter.TryAcquire("198.51.100.8", "tenant-a"));
    }

    [Fact(DisplayName = "W4-C23: redeem attempts are capped at 120 per target tenant")]
    public void PairingRedeemLimiter_EnforcesTenantCap()
    {
        var limiter = new PairingRedeemRateLimiter(clock: TimeProvider.System);

        for (var index = 0; index < 120; index++)
        {
            Assert.True(limiter.TryAcquire($"198.51.100.{index + 1}", "tenant-a"));
        }

        Assert.False(limiter.TryAcquire("203.0.113.1", "tenant-a"));
        Assert.True(limiter.TryAcquire("203.0.113.1", "tenant-b"));
    }

    [Fact(DisplayName = "W4-policy: LAN route matching is segment-bounded and redeem is exact")]
    public void RoutePolicy_UsesSegmentBoundedDescendants()
    {
        foreach (var root in LocalNodeLanRoutePolicy.AllowlistedRoots)
        {
            Assert.True(LocalNodeLanRoutePolicy.IsDataRoute(root));
            Assert.True(LocalNodeLanRoutePolicy.IsDataRoute(root + "/child"));
            Assert.False(LocalNodeLanRoutePolicy.IsDataRoute(root + "-near-miss"));
        }

        var redeem = new DefaultHttpContext();
        redeem.Request.Method = HttpMethods.Post;
        redeem.Request.Path = LocalNodeLanRoutePolicy.PairingRedeemPath;
        Assert.True(LocalNodeLanRoutePolicy.IsPairingRedeem(redeem.Request));
        redeem.Request.Method = HttpMethods.Get;
        Assert.False(LocalNodeLanRoutePolicy.IsPairingRedeem(redeem.Request));
        redeem.Request.Method = HttpMethods.Post;
        redeem.Request.Path = LocalNodeLanRoutePolicy.PairingRedeemPath + "/child";
        Assert.False(LocalNodeLanRoutePolicy.IsPairingRedeem(redeem.Request));
        redeem.Request.Path = LocalNodeLanRoutePolicy.PairingRedeemPath + "/";
        Assert.False(LocalNodeLanRoutePolicy.IsPairingRedeem(redeem.Request));
    }

    [Fact(DisplayName = "W4-C22: device scope refuses the operator identity")]
    public void DevicePrincipalScope_RefusesDesktopAuthorization()
    {
        // Ticket 205 deleted WebPlaneFencedAuthorizationContext — the blanket refusal over the ambient
        // IAuthorizationContext seam — because every route that resolved that seam now resolves one
        // AuthorizationGate decision keyed by the request's own principal. The claim W4-C22 makes has not
        // changed and is asserted on the site that carries it now: a device-plane request with no bound
        // principal must NOT be resolved as the desktop operator, whose grants are not the device's.
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();

        Assert.Equal(NodeCallerParty.OperatorParty, NodeCallerParty.Resolve(http));
        using (NodeCallerAttributionScope.EnterDevice("device-1", "tenant-a", "principal-1"))
        {
            Assert.Throws<NodeCallerAttributionRefusedException>(() => NodeCallerParty.Resolve(http));
        }
        Assert.Equal(NodeCallerParty.OperatorParty, NodeCallerParty.Resolve(http));
    }

    [Fact(DisplayName = "W4-C23: LAN source attempts and concurrent handshakes are capped")]
    public void LanConnectionLimiter_EnforcesThirtyAndTen()
    {
        var limiter = new LanConnectionRateLimiter(TimeProvider.System);
        var leases = new List<IDisposable>();
        for (var index = 0; index < 10; index++)
        {
            Assert.True(limiter.TryAcquire("198.51.100.9", out var lease));
            leases.Add(lease);
        }
        Assert.True(limiter.TryAcquire("198.51.100.10", out var otherSourceLease));
        otherSourceLease.Dispose();
        Assert.False(limiter.TryAcquire("198.51.100.9", out _));
        foreach (var lease in leases)
        {
            lease.Dispose();
        }

        for (var index = 0; index < 20; index++)
        {
            Assert.True(limiter.TryAcquire("198.51.100.9", out var lease));
            lease.Dispose();
        }
        Assert.False(limiter.TryAcquire("198.51.100.9", out _));
    }

    [Fact(DisplayName = "W4-C15: sealed endpoint snapshot denial set is refused before handlers")]
    public async Task SealedSnapshot_DenialSet_IsRefusedBeforeHandlers()
    {
        var routePaths = NormativeAllowlist.Concat(["/api/local-node/unlisted"]).ToArray();
        var (inventory, inventoryProvider) = NewSharedApp(new LocalNodeOptions(), "inventory-token");
        foreach (var path in routePaths)
        {
            inventory.MapApiRoutes(routes => routes.MapGet(path, () => Results.Ok()));
        }

        var gateHandlerHits = new Dictionary<string, int>(StringComparer.Ordinal);
        await inventory.StartAsync(CancellationToken.None);
        var denialSet = inventory.ExecutableEndpointRegistry.Current.Endpoints
            .Select(endpoint => endpoint.RoutePattern)
            .Where(path => !LocalNodeLanRoutePolicy.IsDataRoute(path) &&
                !path.Equals(LocalNodeLanRoutePolicy.PairingRedeemPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        await using var harness = await LanGateHarness.StartAsync(
            routePaths,
            gateHandlerHits,
            new FixedLanDeviceSessionAuthority("device-bearer"));
        foreach (var path in denialSet)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "device-bearer");
            using var response = await harness.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, gateHandlerHits.GetValueOrDefault(path));
        }

        await inventory.StopAsync(CancellationToken.None);
        await inventory.DisposeAsync();
        await inventoryProvider.DisposeAsync();
    }

    [Fact(DisplayName = "W4-C16/C17: desktop and non-redeem admission routes are LAN-unavailable")]
    public async Task DesktopAndAdmissionRoutes_AreRefused()
    {
        var paths = new[]
        {
            "/api/local-node/forms/probe", "/api/local-node/comms/probe",
            "/api/session/founder-bind", "/api/local-node/admission/invites",
            "/api/local-node/admission/identity", "/api/local-node/admission/join",
        };
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var harness = await LanGateHarness.StartAsync(
            paths, hits, new FixedLanDeviceSessionAuthority("device-bearer"));

        foreach (var path in paths)
        {
            using var request = new HttpRequestMessage(
                path.Contains("founder-bind", StringComparison.Ordinal) ||
                path.EndsWith("invites", StringComparison.Ordinal) ||
                path.EndsWith("join", StringComparison.Ordinal)
                    ? HttpMethod.Post
                    : HttpMethod.Get,
                path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "device-bearer");
            using var response = await harness.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, hits.GetValueOrDefault(path));
            Assert.Equal("lan.route.unavailable", await ReadErrorAsync(response));
        }
    }

    [Fact(DisplayName = "W4-C17: only exact POST admission redeem crosses the sessionless arm")]
    public async Task AdmissionRedeem_IsTheOnlySessionlessException()
    {
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        var paths = new[]
        {
            LocalNodeLanRoutePolicy.PairingRedeemPath,
            LocalNodeLanRoutePolicy.PairingRedeemPath + "/child",
            LocalNodeLanRoutePolicy.PairingRedeemPath + "/",
        };
        await using var harness = await LanGateHarness.StartAsync(
            paths.Take(2), hits, new FixedLanDeviceSessionAuthority("device-bearer"));

        using (var exact = new HttpRequestMessage(HttpMethod.Post, paths[0]))
        {
            using var response = await harness.Client.SendAsync(exact);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            Assert.Equal(1, hits[paths[0]]);
        }

        foreach (var path in paths.Skip(1))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            using var response = await harness.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, hits.GetValueOrDefault(path));
        }
    }

    [Fact(DisplayName = "W4-C24: cookie-bearing LAN requests are refused by middleware")]
    public async Task CookieBearingLanRequest_IsRefusedBeforeHandler()
    {
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var harness = await LanGateHarness.StartAsync(
            ["/api/local-node/status"], hits, new FixedLanDeviceSessionAuthority("device-bearer"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/local-node/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "device-bearer");
        request.Headers.Add("Cookie", "__Host-web_session=foreign");

        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("lan.route.unavailable", await ReadErrorAsync(response));
        Assert.Equal(0, hits["/api/local-node/status"]);
    }

    [Fact(DisplayName = "W4-C24: missing LAN bearer is refused before handler")]
    public async Task MissingLanBearer_IsRefusedBeforeHandler()
    {
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var harness = await LanGateHarness.StartAsync(
            ["/api/local-node/status"], hits, new FixedLanDeviceSessionAuthority("device-bearer"));

        using var response = await harness.Client.GetAsync("/api/local-node/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, hits["/api/local-node/status"]);
    }

    [Fact(DisplayName = "W4-C18: loopback per-boot bearer is not a LAN device session")]
    public async Task LoopbackBearer_OnLan_IsRejected()
    {
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var harness = await LanGateHarness.StartAsync(
            ["/api/local-node/status"], hits, new FixedLanDeviceSessionAuthority("device-bearer"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/local-node/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "loopback-per-boot-token");

        using var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, hits["/api/local-node/status"]);
    }

    [Fact(DisplayName = "W4-C15 canary: the runtime allowlist exactly matches the normative set")]
    public void Allowlist_IsExactlyTheNormativeSet()
    {
        Assert.Equal(
            NormativeAllowlist.Order(StringComparer.Ordinal),
            LocalNodeLanRoutePolicy.AllowlistedRoots.Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "W4-M1: authenticated LAN requests do not consume unauthenticated caps")]
    public async Task AuthenticatedLanRequests_AreUnmetered()
    {
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var harness = await LanGateHarness.StartAsync(
            ["/api/local-node/status"], hits, new FixedLanDeviceSessionAuthority("device-bearer"));

        for (var index = 0; index < LanConnectionRateLimiter.MaxAttemptsPerSourcePerMinute + 5; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/local-node/status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "device-bearer");
            using var response = await harness.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact(DisplayName = "W4-C16/C17: LAN route refusal has the stable opaque JSON shape")]
    public async Task LanRouteUnavailable_UsesStableJson()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await LanRouteUnavailable.RejectAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);
        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body);
        Assert.Equal("lan.route.unavailable", document.RootElement.GetProperty("error").GetString());
    }

    private static LocalNodeLanOptions NewOptions(string bindAddress, string advertisedHost, int port) => new()
    {
        Enabled = true,
        BindAddress = bindAddress,
        AdvertisedHost = advertisedHost,
        Port = port,
    };

    private static X509Certificate2 CreateCertificate(string dnsName, IPAddress address)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={dnsName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private string WritePfx(X509Certificate2 certificate)
    {
        Directory.CreateDirectory(_dataDirectory);
        var path = Path.Combine(_dataDirectory, $"w4-lan-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, "test-password"));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
    }

    private sealed class StubAuthorizationContext(bool allowed) : IAuthorizationContext
    {
        public bool HasPermission(string permission) => allowed;
    }

    private static (SharedHostedWebApp App, ServiceProvider Provider) NewSharedApp(
        LocalNodeOptions options,
        string? sessionToken = null,
        string? urlsOverride = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor());
        services.AddSingleton(new NodeCallerSessionToken(sessionToken));
        services.AddTestKernelClock();
        var provider = services.BuildServiceProvider();
        var app = new SharedHostedWebApp(
            provider,
            Options.Create(options),
            new LocalNodeExecutableEndpointRegistry(),
            provider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            provider.GetRequiredService<TimeProvider>(),
            urlsOverride);
        return (app, provider);
    }

    private static IPAddress FindAssignedIpv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .First(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any));

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private sealed class NoTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active => null;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed class FixedLanDeviceSessionAuthority(string acceptedBearer) : ILanDeviceSessionAuthority
    {
        public ValueTask<LanDevicePrincipal?> AuthenticateAsync(
            HttpContext context,
            string bearer,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<LanDevicePrincipal?>(
                string.Equals(bearer, acceptedBearer, StringComparison.Ordinal)
                    ? new LanDevicePrincipal("device-1", "tenant-a", "principal-1")
                    : null);
    }

    private sealed class LanGateHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private LanGateHarness(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        internal HttpClient Client { get; }

        internal static async Task<LanGateHarness> StartAsync(
            IEnumerable<string> paths,
            IDictionary<string, int> handlerHits,
            ILanDeviceSessionAuthority authority)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Environment.EnvironmentName = Environments.Development;
            var app = builder.Build();
            app.UseDeveloperExceptionPage();

            // Simulate Kestrel's per-listener connection feature. The production listener sets this
            // in ListenOptions.Use; the test then drives the real gate over HTTP.
            app.Use(async (context, next) =>
            {
                context.Features.Set(LanListenerRequestFeature.Instance);
                await next(context).ConfigureAwait(false);
            });
            SharedHostedWebApp.UseLanListenerGate(
                app,
                authority,
                new LanConnectionRateLimiter(TimeProvider.System));

            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                var routePath = path;
                handlerHits[routePath] = 0;
                app.MapGet(routePath, () =>
                {
                    handlerHits[routePath]++;
                    return Results.Ok();
                });
                app.MapPost(routePath, () =>
                {
                    handlerHits[routePath]++;
                    return Results.Ok();
                });
            }

            await app.StartAsync(CancellationToken.None);
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new LanGateHarness(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }
}
