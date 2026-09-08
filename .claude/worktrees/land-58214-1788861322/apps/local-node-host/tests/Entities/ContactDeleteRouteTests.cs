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
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Identity;
using Harborline.Api.LocalNodeHost.Tests.Packs;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Route-level coverage for the contacts ARCHIVE (soft-delete) surface on the SHIPPED product API — the
/// gap the 3-way sync test found: <see cref="ContactRoutes"/> mapped create/list/get/update but NO delete,
/// so although the CRDT tombstone (<see cref="ContactCrdtProjection.ProjectDelete"/>) was proven to sync
/// 3-way, the product surface could not invoke it (live: <c>DELETE</c> → 405, <c>POST .../delete</c> → 404)
/// and delete-sync was not usable end-to-end.
/// </summary>
/// <remarks>
/// <para>
/// Hosts the SAME production route handlers (<see cref="ContactRoutes.Map"/>) on a real in-process Kestrel
/// listener over a temp SQLite store and a REAL <see cref="ContactCrdtProjection"/> (real
/// <see cref="YDotNetCrdtEngine"/> backend), driving them with a real <see cref="HttpClient"/>. Mirrors
/// <c>PropertyRouteTests</c> + <c>ContactCrdtConvergenceTests</c>.
/// </para>
/// <para>
/// <b>MVP semantics — ARCHIVE, not hard delete (CIC 2026-06-03).</b> A contact is a master/Party, so the
/// delete route SOFT-deletes: it stamps <c>DeletedAt</c> (a tombstone), leaves the row in place, and hides
/// the contact from list/get. The tombstone projects into the CRDT as a value-change that converges to a
/// peer — the property the harness proved 3-way, now reachable from the product route.
/// </para>
/// </remarks>
public sealed class ContactDeleteRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _baseUrl = null!;
    private string _dir = null!;
    private ContactCrdtProjection _crdt = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private IDbContextFactory<NodeLocalInstallationIdentityDbContext> _identityFactory = null!;
    private MutableAuthorizationContext _authorization = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _authorization = new MutableAuthorizationContext();
        builder.Services.AddSingleton<IAuthorizationContext>(_authorization);
        // Ticket 205 slice 4: the route guards resolve at the gate. It follows the SAME mutable holding
        // set this host already flips, so a test that narrows the caller's permissions narrows the decision.
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.Following(
            permission => _authorization.HasPermission(permission)));

        _dir = Path.Combine(Path.GetTempPath(), "harborline-contact-delete-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "local-node.db")};Pooling=False";

        builder.Services.AddSingleton<IHarborlineEntityModule, PeopleEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        builder.Services.AddDbContextFactory<Harborline.Api.LocalNodeHost.Data.Identity.NodeLocalInstallationIdentityDbContext>(
            opt => opt.UseSqlite(connectionString));
        // Real YDotNet backend — the tombstone convergence must be honest (the stub cannot test it).
        builder.Services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();

        _app = builder.Build();

        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        // Construct the projection + accessor exactly as the composition root does, then map the SAME
        // production routes (mirrors HostedContactApiEndpoint wiring — no [FromServices]).
        _crdt = new ContactCrdtProjection(
            _app.Services.GetRequiredService<ICrdtEngine>(), _factory, NullLogger<ContactCrdtProjection>.Instance);
        var accessor = new NodeEfPartyRepository(_factory, TimeProvider.System);
        _identityFactory = _app.Services.GetRequiredService<
            IDbContextFactory<NodeLocalInstallationIdentityDbContext>>();
        await using (var identityDb = await _identityFactory.CreateDbContextAsync())
            await identityDb.Database.MigrateAsync();
        await InstallationIdentityTestBootstrap.BootstrapAsync(_identityFactory);
        NodeMutationIdempotency.UseOnce(_app, TimeProvider.System);
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        ContactRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            accessor,
            _crdt,
            NodeTestActiveTeam.Accessor,
            _identityFactory,
            TimeProvider.System);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _baseUrl = addresses!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _crdt.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private const string Route = "/api/local-node/contacts";

    [Fact(DisplayName = "Contact route: a member without create permission gets 403 and the handler does not write")]
    public async Task Create_WithoutPermission_Returns403_AndDoesNotWrite()
    {
        _authorization.DenyAll();

        var response = await _client.PostAsJsonAsync(Route, new { displayName = "Denied Contact", kind = "person" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        Assert.Equal(Permission.ContactsCreate, body.GetProperty("permission").GetString());
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.Set<Party>().ToListAsync());
    }

    [Fact(DisplayName = "Contact route: narrowing and revocation take effect on the next read request")]
    public async Task Read_PermissionNarrowedOrRevoked_IsDeniedOnNextRequest()
    {
        var id = await CreateContactAsync("Visible Contact");
        _authorization.Allow(Permission.ContactsRead);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"{Route}/{id}")).StatusCode);

        _authorization.DenyAll();
        var narrowed = await _client.GetAsync($"{Route}/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, narrowed.StatusCode);
        var body = await narrowed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Permission.ContactsRead, body.GetProperty("permission").GetString());

        var revoked = await _client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    private async Task<string> CreateContactAsync(string displayName)
    {
        var resp = await _client.PostAsJsonAsync(Route, new { displayName, kind = "person" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("contactId").GetString()!;
    }

    private async Task<int> ListCountAsync()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        return doc.GetProperty("contacts").GetArrayLength();
    }

    [Fact(DisplayName = "Contact route: attach and detach a business role updates contact detail")]
    public async Task RoleLifecycle_UpdatesContactDetail()
    {
        var id = await CreateContactAsync("Northwind Clinic");
        var attached = await _client.PostAsJsonAsync($"{Route}/{id}/roles", new { roleName = "patient" });
        Assert.Equal(HttpStatusCode.OK, attached.StatusCode);
        var role = await attached.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("patient", role.GetProperty("roleName").GetString());

        var detail = await _client.GetFromJsonAsync<JsonElement>($"{Route}/{id}");
        Assert.Equal(1, detail.GetProperty("roles").GetArrayLength());

        var detached = await _client.DeleteAsync($"{Route}/{id}/roles/{role.GetProperty("roleId").GetString()}");
        Assert.Equal(HttpStatusCode.NoContent, detached.StatusCode);
        detail = await _client.GetFromJsonAsync<JsonElement>($"{Route}/{id}");
        Assert.Equal(0, detail.GetProperty("roles").GetArrayLength());
    }

    [Fact(DisplayName = "Contact route: role writes are tenant-scoped and unknown parties stay opaque")]
    public async Task AttachRole_UnknownParty_Returns404()
    {
        var response = await _client.PostAsJsonAsync($"{Route}/does-not-exist/roles", new { roleName = "vendor" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── POST .../{id}/delete → soft-delete (hidden from get/list) + tombstone delta ──────────────────────

    [Fact(DisplayName = "Contact route: POST .../{id}/delete archives (hidden from get/list) AND emits a CRDT tombstone")]
    public async Task PostDelete_SoftDeletes_AndEmitsTombstone()
    {
        var id = await CreateContactAsync("Ada Lovelace");
        Assert.Equal(1, await ListCountAsync());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"{Route}/{id}")).StatusCode);

        var del = await _client.PostAsync($"{Route}/{id}/delete", content: null);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // Soft-delete: hidden from get (404) and excluded from list.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Route}/{id}")).StatusCode);
        Assert.Equal(0, await ListCountAsync());

        // The row is ARCHIVED, not hard-deleted — the tombstone is still in the store (DeletedAt stamped).
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var row = await ctx.Set<Party>().IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == new PartyId(id));
            Assert.NotNull(row);
            Assert.NotNull(row!.DeletedAt);
        }

        // The delete emitted the tombstone into the CRDT (Deleted=true) — the value-change that syncs.
        var state = _crdt.GetState(id);
        Assert.NotNull(state);
        Assert.True(state!.Deleted);
    }

    // ── DELETE /{id} verb maps to the same handler ───────────────────────────────────────────────────────

    [Fact(DisplayName = "Contact route: DELETE /{id} archives the contact (no longer 405)")]
    public async Task DeleteVerb_SoftDeletes()
    {
        var id = await CreateContactAsync("Grace Hopper");

        var del = await _client.DeleteAsync($"{Route}/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Route}/{id}")).StatusCode);
        Assert.True(_crdt.GetState(id)!.Deleted);
    }

    // ── delete on a missing / already-archived contact → 404 (opaque) ────────────────────────────────────

    [Fact(DisplayName = "Contact route: delete of an unknown contact → 404")]
    public async Task Delete_Unknown_Returns404()
    {
        var resp = await _client.PostAsync($"{Route}/does-not-exist/delete", content: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact(DisplayName = "Contact route: a second delete of an already-archived contact → 404 (idempotent-hidden)")]
    public async Task Delete_AlreadyArchived_Returns404()
    {
        var id = await CreateContactAsync("Temp Contact");
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{Route}/{id}")).StatusCode);

        // The row is now tombstoned → GetByIdAsync returns null → the route reports 404.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"{Route}/{id}")).StatusCode);
    }

    [Fact(DisplayName = "Contact route: an audit fault rolls back the party write")]
    public async Task Create_AuditFault_RollsBackParty()
    {
        await using (var identity = await _identityFactory.CreateDbContextAsync())
        {
            var head = await identity.AuditHeads.SingleAsync();
            identity.AuditHeads.Remove(head);
            await identity.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync(
            Route,
            new { displayName = "Should Roll Back", kind = "person" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await ListCountAsync());
    }

    // ── delete-sync: the tombstone from the PRODUCT ROUTE converges to a peer (mirrors the harness) ───────

    [Fact(DisplayName = "Contact route: a delete via the product route's tombstone delta hides the contact on a peer")]
    public async Task RouteDelete_Tombstone_ConvergesToPeer()
    {
        // A peer replica with its own engine + SQLite store + projection (in-proc, mirrors the convergence
        // harness — the live TCP daemon path is proven by MultiDeviceContactSyncTests).
        var peerDir = Path.Combine(Path.GetTempPath(), "harborline-contact-delete-peer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(peerDir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHarborlineEntityModule, PeopleEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(peerDir, "local-node.db")};Pooling=False"));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        await using var peerSp = services.BuildServiceProvider();
        var peerFactory = peerSp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await peerFactory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();
        await using var peer = new ContactCrdtProjection(
            peerSp.GetRequiredService<ICrdtEngine>(), peerFactory, NullLogger<ContactCrdtProjection>.Instance);

        // 1. Create via the product route on the local node, sync the create to the peer (key-add).
        var id = await CreateContactAsync("Doomed Contact");
        await SyncAsync(_crdt, peer);
        Assert.Equal("Doomed Contact", await ReadDisplayNameAsync(peerFactory, id));

        // 2. DELETE via the product route — the route stamps the tombstone AND projects it into the CRDT.
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"{Route}/{id}")).StatusCode);

        // 3. Sync the tombstone delta to the peer over the live Changed→reconcile trigger.
        await SyncAsync(_crdt, peer);

        // The tombstone converged: the contact is hidden from the peer's readable store.
        Assert.Null(await ReadDisplayNameAsync(peerFactory, id));
        Assert.True(peer.GetState(id)!.Deleted);

        try { Directory.Delete(peerDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>One direction of a sync round over the LIVE trigger (mirrors ContactCrdtConvergenceTests).</summary>
    private static async Task SyncAsync(ContactCrdtProjection src, ContactCrdtProjection dst)
    {
        var delta = await src.EncodeOutboundDeltaAsync(
            ContactCrdtProjection.DocumentId, dst.VectorClock, CancellationToken.None);
        Assert.NotNull(delta);
        await dst.ApplyInboundDeltaAsync(ContactCrdtProjection.DocumentId, 1, delta!.Value, CancellationToken.None);
        await dst.DrainPendingReconcilesAsync();
    }

    private static async Task<string?> ReadDisplayNameAsync(IDbContextFactory<LocalNodeDbContext> factory, string id)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var p = await ctx.Set<Party>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == new PartyId(id));
        return p?.DeletedAt is null ? p?.DisplayName : null;
    }
}
