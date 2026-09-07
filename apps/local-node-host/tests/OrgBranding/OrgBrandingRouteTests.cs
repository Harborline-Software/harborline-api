using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.OrgBranding;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.OrgBranding;

/// <summary>
/// Tenant-branding slice T1 gate — route-level tests for the node-local org-branding API. Hosts the SAME
/// production route handlers <see cref="HostedOrgBrandingApiEndpoint"/> registers
/// (<see cref="OrgBrandingRoutes.Map"/> — the single source of truth, no test/prod wire drift) on a real
/// in-process Kestrel listener, and drives them with a real <see cref="HttpClient"/>. Mirrors
/// <c>CalendarRouteTests</c>.
/// </summary>
/// <remarks>
/// Proves the gate: the node STORES + SERVES a profile (write name/accent, upload+serve logo); an invalid
/// logo (disallowed type / oversized) is rejected 400; an accent that cannot hit AA is rejected (400) or
/// clamped; the tenant is server-side (a different active team is isolated).
/// </remarks>
public sealed class OrgBrandingRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000000a1"));
    private static readonly TeamId TeamB = new(Guid.Parse("bbbb0000-0000-0000-0000-0000000000b1"));

    private const string Base = "/api/local-node/org-branding";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        var store = new FakeOrgBrandingStore();
        var blobs = new FakeBlobStore();
        _activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));
        var resolver = new OrgBrandingResolver(store, _activeTeam);

        // Map the SAME production routes (mirrors HostedOrgBrandingApiEndpoint wiring — no [FromServices]).
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        OrgBrandingRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            store,
            resolver,
            blobs,
            _activeTeam,
            TimeProvider.System);

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

    // ── resolve + fallback ladder ─────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "initially resolves to the platform default (pre-setup)")]
    public async Task Resolve_Initially_PlatformDefault()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Base);
        Assert.False(doc.GetProperty("hasOrgIdentity").GetBoolean());
        Assert.Equal("platformDefault", doc.GetProperty("logoSource").GetString());
    }

    [Fact(DisplayName = "PUT a name -> resolves to the org-name wordmark")]
    public async Task PutName_Then_Resolve_Wordmark()
    {
        var put = await _client.PutAsJsonAsync(Base, new { displayName = "Acme Property Co." });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var doc = await _client.GetFromJsonAsync<JsonElement>(Base);
        Assert.True(doc.GetProperty("hasOrgIdentity").GetBoolean());
        Assert.Equal("Acme Property Co.", doc.GetProperty("displayName").GetString());
        Assert.Equal("orgNameWordmark", doc.GetProperty("logoSource").GetString());
    }

    // ── accent AA gate ────────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "PUT an in-band accent -> accepted, not clamped; resolves with a foreground")]
    public async Task PutAccent_InBand_NotClamped()
    {
        var put = await _client.PutAsJsonAsync(Base, new { displayName = "Acme", accentColor = "#7A7A7A" });
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False(body.GetProperty("accentClamped").GetBoolean());

        var doc = await _client.GetFromJsonAsync<JsonElement>(Base);
        Assert.Equal("#7A7A7A", doc.GetProperty("accentColor").GetString());
        Assert.False(string.IsNullOrEmpty(doc.GetProperty("accentForeground").GetString()));
    }

    [Fact(DisplayName = "PUT a too-light accent -> clamped (never shipped failing)")]
    public async Task PutAccent_TooLight_Clamped()
    {
        var put = await _client.PutAsJsonAsync(Base, new { displayName = "Acme", accentColor = "#FFFF00" });
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True(body.GetProperty("accentClamped").GetBoolean());
    }

    [Fact(DisplayName = "PUT an invalid accent -> 400")]
    public async Task PutAccent_Invalid_400()
    {
        var put = await _client.PutAsJsonAsync(Base, new { displayName = "Acme", accentColor = "not-a-color" });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    // ── logo store + serve + validation ───────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "POST a valid PNG -> stored; GET logo serves it; resolves to org logo")]
    public async Task PostValidPng_Stored_Served_Resolves()
    {
        var post = await PostLogo(Png(200, 100), "image/png");
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var meta = await post.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("png", meta.GetProperty("format").GetString());
        Assert.Equal(200, meta.GetProperty("width").GetInt32());

        var logo = await _client.GetAsync($"{Base}/logo");
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/png", logo.Content.Headers.ContentType!.MediaType);
        Assert.NotEmpty(await logo.Content.ReadAsByteArrayAsync());

        var doc = await _client.GetFromJsonAsync<JsonElement>(Base);
        Assert.Equal("orgLogo", doc.GetProperty("logoSource").GetString());
        Assert.True(doc.GetProperty("hasLightLogo").GetBoolean());
    }

    [Fact(DisplayName = "POST a disallowed type (GIF) -> 400")]
    public async Task PostGif_Rejected_400()
    {
        var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x10, 0x00, 0x10, 0x00 };
        var post = await PostLogo(gif, "image/gif");
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact(DisplayName = "POST an oversized asset -> 400")]
    public async Task PostOversized_Rejected_400()
    {
        var big = new byte[OrgBrandingDefaults.MaxLogoBytes + 1024];
        var post = await PostLogo(big, "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact(DisplayName = "GET logo when none is set -> 404 (client falls back to the wordmark)")]
    public async Task GetLogo_Unset_404()
    {
        var logo = await _client.GetAsync($"{Base}/logo");
        Assert.Equal(HttpStatusCode.NotFound, logo.StatusCode);
    }

    // ── server-side tenant ────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "tenant is server-side: switching the active team isolates branding")]
    public async Task Tenant_IsServerSide_Isolated()
    {
        await _client.PutAsJsonAsync(Base, new { displayName = "Team A Co." });

        _activeTeam.Active = TeamContextFor(TeamB, "Team B");
        var docB = await _client.GetFromJsonAsync<JsonElement>(Base);
        Assert.False(docB.GetProperty("hasOrgIdentity").GetBoolean()); // B never wrote — isolated

        _activeTeam.Active = TeamContextFor(TeamA, "Team A");
        var docA = await _client.GetFromJsonAsync<JsonElement>(Base);
        Assert.Equal("Team A Co.", docA.GetProperty("displayName").GetString());
    }

    // ── helpers + fakes ───────────────────────────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> PostLogo(byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return _client.PostAsync($"{Base}/logo", content);
    }

    private static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Array.Copy(sig, b, 8);
        b[11] = 0x0D;
        b[12] = (byte)'I'; b[13] = (byte)'H'; b[14] = (byte)'D'; b[15] = (byte)'R';
        b[16] = (byte)((w >> 24) & 0xFF); b[17] = (byte)((w >> 16) & 0xFF);
        b[18] = (byte)((w >> 8) & 0xFF); b[19] = (byte)(w & 0xFF);
        b[20] = (byte)((h >> 24) & 0xFF); b[21] = (byte)((h >> 16) & 0xFF);
        b[22] = (byte)((h >> 8) & 0xFF); b[23] = (byte)(h & 0xFF);
        return b;
    }

    private static TeamContext TeamContextFor(TeamId teamId, string name)
        => new(teamId, name, new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        public MutableActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; set; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }

    private sealed class FakeOrgBrandingStore : IOrgBrandingStore
    {
        private readonly Dictionary<string, OrgBrandingProfile> _profiles = new(StringComparer.Ordinal);

        public Task<OrgBrandingProfile?> GetAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult(_profiles.TryGetValue(tenant.Value, out var p) ? p : null);

        public Task UpsertAsync(OrgBrandingProfile profile, CancellationToken ct = default)
        {
            _profiles[profile.TenantId] = profile;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBlobStore : IBlobStore
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

        public ValueTask<Cid> PutAsync(ReadOnlyMemory<byte> content, CancellationToken ct = default)
        {
            var cid = Cid.FromBytes(content.Span);
            _blobs[cid.Value] = content.ToArray();
            return ValueTask.FromResult(cid);
        }

        public ValueTask<ReadOnlyMemory<byte>?> GetAsync(Cid cid, CancellationToken ct = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                _blobs.TryGetValue(cid.Value, out var b) ? b : null);

        public ValueTask<bool> ExistsLocallyAsync(Cid cid, CancellationToken ct = default) =>
            ValueTask.FromResult(_blobs.ContainsKey(cid.Value));

        public ValueTask PinAsync(Cid cid, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask UnpinAsync(Cid cid, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
