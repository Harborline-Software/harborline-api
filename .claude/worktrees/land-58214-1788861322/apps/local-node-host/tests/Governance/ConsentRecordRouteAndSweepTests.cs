using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Governance.Consent;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Governance;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Entities;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Governance;

/// <summary>
/// Ticket 213 slice 2 / L646 — the consent record's four transitions and its effective read get PRODUCT
/// callers: gated routes, decided by the real gate over the real seed, and a scheduled expiry sweep.
/// </summary>
/// <remarks>
/// The routes are hosted from the same <see cref="ConsentRecordRoutes.Map"/> the production
/// <c>HostedConsentRecordApiEndpoint</c> registers, over a real Kestrel listener and a real
/// <see cref="FileTenantConsentStore"/> on a real temp directory, and every request is decided by the REAL
/// <see cref="AuthorizationGate"/> over the definition-joined closure the REAL platform seed installs. So
/// what a caller may do here is decided by the consent capability's actual binding, not by a test double.
/// </remarks>
public sealed class ConsentRecordRouteAndSweepTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-06T12:00:00Z");

    [Fact(DisplayName = "The offered role records a consent through the routes and reads it back effective")]
    public async Task The_offered_role_may_use_every_consent_route()
    {
        await using var h = await Host.CreateAsync(offerConsentToTheCaller: true);

        var created = await h.Client.PostAsJsonAsync(ConsentRecordRoutes.RouteBase, new
        {
            subject = "subject-1",
            purpose = "care-coordination",
            scope = "/records/42",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var activated = await h.Client.PostAsync($"{ConsentRecordRoutes.RouteBase}/{id}/activate", null);
        var read = await h.Client.GetAsync($"{ConsentRecordRoutes.RouteBase}/{id}");
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var wire = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", wire.GetProperty("state").GetString());
        Assert.Equal("Active", wire.GetProperty("effective_state").GetString());

        // The act's instant comes from the record the route wrote: the route dated the activation with its
        // own kernel-clock read, and the decision below is taken a minute after that.
        var activeFrom = wire.GetProperty("effective_from").GetDateTimeOffset().AddMinutes(1);
        // The record the routes wrote is the record the ROUTE-LESS gate predicate reads (slice 1's posture:
        // a subject-consent act is not a route act). A fresh gate over the same directory, no HttpContext.
        using var store = FileTenantConsentStore.InDirectory(h.Directory);
        var decision = await new TenantConsentGate(store, new NullAuditLog()).DecideAsync(
            new ConsentRequest(
                h.Tenant, new SubjectId("subject-1"), "care-coordination",
                ScopeExpression.Parse("/records/42"), activeFrom));
        Assert.True(decision.Allowed);

        var revoked = await h.Client.PostAsync($"{ConsentRecordRoutes.RouteBase}/{id}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        // Revoked is terminal: the same move again is refused by the pure record, before any write.
        var again = await h.Client.PostAsync($"{ConsentRecordRoutes.RouteBase}/{id}/revoke", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact(DisplayName =
        "The offered role expires an active record whose window has closed, and the expiry is audited")]
    public async Task The_offered_role_expires_a_past_due_record_through_the_route()
    {
        await using var h = await Host.CreateAsync(offerConsentToTheCaller: true);

        var created = await h.Client.PostAsJsonAsync(ConsentRecordRoutes.RouteBase, new
        {
            subject = "subject-1",
            purpose = "care-coordination",
            scope = "/records/42",
            effective_until = At.AddHours(2),
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK,
            (await h.Client.PostAsync($"{ConsentRecordRoutes.RouteBase}/{id}/activate", null)).StatusCode);

        // The window closes. The STORED state is still Active and the EFFECTIVE reading is already Expired:
        // that disagreement is exactly what the expire route (and the sweep) exists to settle.
        h.Clock.MoveTo(At.AddHours(3));
        var stale = await (await h.Client.GetAsync($"{ConsentRecordRoutes.RouteBase}/{id}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Active", stale.GetProperty("state").GetString());
        Assert.Equal("Expired", stale.GetProperty("effective_state").GetString());

        h.Audit.Rows.Clear();
        var expired = await h.Client.PostAsync($"{ConsentRecordRoutes.RouteBase}/{id}/expire", null);
        Assert.Equal(HttpStatusCode.OK, expired.StatusCode);
        // EXPIRED, not revoked: the three transition routes differ only in the delegate they pass, so this
        // is the one assertion that catches an expire route wired to RevokeAsync.
        var wire = await expired.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Expired", wire.GetProperty("state").GetString());
        // Expired is terminal, and it is terminal as EXPIRED: revoking afterwards is refused, not accepted.
        Assert.Equal(HttpStatusCode.Conflict,
            (await h.Client.PostAsync($"{ConsentRecordRoutes.RouteBase}/{id}/revoke", null)).StatusCode);

        var row = Assert.Single(h.Audit.Rows);
        Assert.Equal("consent expired", row.Justification);
        Assert.Equal(id, row.EntityId.LocalPart);
        Assert.NotEqual(ConsentExpirySweepDaemon.SweepPrincipal, row.Actor.Value);
    }

    [Fact(DisplayName = "Offered to another role only, every consent route refuses the caller fail-closed")]
    public async Task Another_role_holding_the_capability_refuses_this_caller()
    {
        await using var h = await Host.CreateAsync(offerConsentToTheCaller: false);

        var created = await h.Client.PostAsJsonAsync(ConsentRecordRoutes.RouteBase, new
        {
            subject = "subject-1",
            purpose = "care-coordination",
            scope = "/records/42",
        });
        var read = await h.Client.GetAsync($"{ConsentRecordRoutes.RouteBase}/seeded-record");
        var activate = await h.Client.PostAsync($"{ConsentRecordRoutes.RouteBase}/seeded-record/activate", null);
        var expire = await h.Client.PostAsync($"{ConsentRecordRoutes.RouteBase}/seeded-record/expire", null);
        var revoke = await h.Client.PostAsync($"{ConsentRecordRoutes.RouteBase}/seeded-record/revoke", null);

        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, activate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, expire.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);

        // Fail-closed means nothing was written either: the refused request created no record.
        using var store = FileTenantConsentStore.InDirectory(h.Directory);
        Assert.Empty(await store.ReadAsync(h.Tenant));
    }

    [Fact(DisplayName = "The sweep expires exactly the past-due active records and nothing else")]
    public async Task The_sweep_expires_only_past_due_active_records()
    {
        using var fixture = new SweepFixture(now: At.AddHours(10));
        var pastDue = await fixture.ActiveAsync("past-due", until: At.AddHours(5));
        var stillOpen = await fixture.ActiveAsync("still-open", until: At.AddHours(20));
        var openEnded = await fixture.ActiveAsync("open-ended", until: null);
        var neverActivated = await fixture.RequestedAsync("never-activated", until: At.AddHours(5));

        Assert.Equal(1, await fixture.Daemon.SweepAsync());

        var after = await fixture.StateByIdAsync();
        Assert.Equal(ConsentLifecycleState.Expired, after[pastDue]);
        Assert.Equal(ConsentLifecycleState.Active, after[stillOpen]);
        Assert.Equal(ConsentLifecycleState.Active, after[openEnded]);
        // A requested record whose window has passed is NOT expired by the sweep: it never became active,
        // and requested -> expired is not a move the record permits.
        Assert.Equal(ConsentLifecycleState.Requested, after[neverActivated]);
        // Every expiry the sweep performs is audited, one row per record, as the sweep's own principal.
        var row = Assert.Single(fixture.Audit.Rows);
        Assert.Equal(ConsentExpirySweepDaemon.SweepPrincipal, row.Actor.Value);
        Assert.Equal("consent expired", row.Justification);
        Assert.Equal(pastDue, row.EntityId.LocalPart);
    }

    [Fact(DisplayName = "A second sweep over the same store expires nothing and audits nothing")]
    public async Task The_sweep_is_idempotent()
    {
        using var fixture = new SweepFixture(now: At.AddHours(10));
        await fixture.ActiveAsync("past-due", until: At.AddHours(5));

        Assert.Equal(1, await fixture.Daemon.SweepAsync());
        Assert.Equal(0, await fixture.Daemon.SweepAsync());
        Assert.Single(fixture.Audit.Rows);
    }

    /// <summary>The sweep under a frozen clock, over a real file store and the real gate.</summary>
    private sealed class SweepFixture : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "harborline-consent-sweep-" + Guid.NewGuid().ToString("N"));
        private readonly FileTenantConsentStore _store;
        private readonly TenantConsentGate _gate;

        public SweepFixture(DateTimeOffset now)
        {
            Directory.CreateDirectory(_dir);
            _store = FileTenantConsentStore.InDirectory(_dir);
            _gate = new TenantConsentGate(_store, Audit);
            Daemon = new ConsentExpirySweepDaemon(
                _gate, _store, new FixedTimeProvider(now),
                NullLogger<ConsentExpirySweepDaemon>.Instance);
        }

        public ConsentExpirySweepDaemon Daemon { get; }

        public RecordingAuditLog Audit { get; } = new();

        public TenantId Tenant { get; } = new("consent-sweep-tenant");

        public async Task<string> RequestedAsync(string id, DateTimeOffset? until)
        {
            await _gate.RequestAsync(
                TenantConsentRecord.Request(
                    id, Tenant, new SubjectId("subject-" + id), "purpose",
                    ScopeExpression.Parse("/records/1"), At, until),
                new ActorId("tester"));
            Audit.Rows.Clear();
            return id;
        }

        public async Task<string> ActiveAsync(string id, DateTimeOffset? until)
        {
            await RequestedAsync(id, until);
            var record = (await _store.ReadAsync(Tenant)).Single(r => r.Id == id);
            await _gate.ActivateAsync(record, new ActorId("tester"), At);
            Audit.Rows.Clear();
            return id;
        }

        public async Task<Dictionary<string, ConsentLifecycleState>> StateByIdAsync() =>
            (await _store.ReadAsync(Tenant)).ToDictionary(r => r.Id, r => r.State, StringComparer.Ordinal);

        public void Dispose()
        {
            _store.Dispose();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ServiceProvider _authorization;
        private readonly FileTenantConsentStore _store;

        private Host(
            WebApplication app, ServiceProvider authorization, FileTenantConsentStore store,
            string dir, TenantId tenant, HttpClient client)
        {
            _app = app;
            _authorization = authorization;
            _store = store;
            Directory = dir;
            Tenant = tenant;
            Client = client;
        }

        public HttpClient Client { get; }

        public string Directory { get; }

        public TenantId Tenant { get; }

        /// <summary>The node's kernel clock, frozen so the window a record is judged against is ours to move.</summary>
        public required MovableTimeProvider Clock { get; init; }

        /// <summary>Every row the routes' gate appended — the routes' own audit trail, not a double's.</summary>
        public required RecordingAuditLog Audit { get; init; }

        public static async Task<Host> CreateAsync(bool offerConsentToTheCaller)
        {
            var tenant = NodeTenant.Resolve(NodeTestActiveTeam.Accessor);
            var authorization = await SeededAuthorizationAsync(tenant, offerConsentToTheCaller);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var clock = new MovableTimeProvider(At);
            builder.Services.AddFrozenKernelClock(clock);
            builder.Services.AddSingleton(RealGate(authorization));

            var dir = Path.Combine(
                Path.GetTempPath(), "harborline-consent-routes-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            var store = FileTenantConsentStore.InDirectory(dir);

            var audit = new RecordingAuditLog();
            var app = builder.Build();
            app.Use(async (http, next) =>
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
                await next(http);
            });
            ConsentRecordRoutes.Map(
                app.MapDesktopPlaneOnlyGroup(),
                new TenantConsentGate(store, audit),
                store,
                NodeTestActiveTeam.Accessor);
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            return new Host(app, authorization, store, dir, tenant,
                new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) })
            {
                Clock = clock,
                Audit = audit,
            };
        }

        /// <summary>The production closure gate over the real seed — the consent routes' verdicts here are
        /// the consent capability's real binding deciding.</summary>
        private static AuthorizationGate RealGate(ServiceProvider authorization) => new(
            authorization.GetRequiredService<IAuthorizationClosureSnapshotReader>(),
            authorization.GetRequiredService<IRecordStandingResolver>(),
            authorization.GetRequiredService<IAuthorizationDefinitionAtomReader>());

        private static async Task<ServiceProvider> SeededAuthorizationAsync(
            TenantId tenant, bool offerConsentToTheCaller)
        {
            var services = new ServiceCollection();
            services.AddSingleton(TestAuthorization.AllowGate());
            services.AddAccessGrantModule();
            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<AccessGrantAuthorizationSeed>()
                .InstallAsync(tenant, At, AuthorizationSeedProfile.Production);
            if (!offerConsentToTheCaller)
            {
                // The caller keeps every other holding the seed gives it; the two consent definitions are
                // narrowed to Administrator ALONE — a role this desktop caller does not hold. So a refusal
                // below is the consent capability being held by somebody else, not a caller who happens to
                // hold nothing at all.
                var catalogue = provider.GetRequiredService<IAuthorizationDefinitionCatalogueReader>();
                var rows = await catalogue.ListAsync(tenant);
                var writer = provider.GetRequiredService<AuthorizationDefinitionWriter>();
                foreach (var operation in new[] { Permission.ConsentRead, Permission.ConsentWrite })
                {
                    var row = Assert.Single(rows, item => item.Definition.Operation.Value == operation);
                    await writer.WriteAsync(new NarrowCapabilityRoleBinding(
                        tenant, row.Definition.DefinitionId,
                        RoleBindingSet.From([RoleReference.Administrator]),
                        new ActorId("tenant-admin"), At, new BindingChangeReason("administrator-only")));
                }
            }

            return provider;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            await _authorization.DisposeAsync();
            _store.Dispose();
            try { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A frozen kernel clock the test may step forward, so a consent window can actually close.</summary>
    internal sealed class MovableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void MoveTo(DateTimeOffset at) => _now = at;
    }

    private sealed class NullAuditLog : IAuditLog
    {
        public Task<AuditId> AppendAsync(AuditAppend append, CancellationToken ct = default) =>
            Task.FromResult(new AuditId(1));

        public async IAsyncEnumerable<AuditRecord> QueryAsync(
            AuditQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> VerifyChainAsync(EntityId entity, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditAppend> Rows { get; } = [];

        public Task<AuditId> AppendAsync(AuditAppend append, CancellationToken ct = default)
        {
            Rows.Add(append);
            return Task.FromResult(new AuditId(Rows.Count));
        }

        public async IAsyncEnumerable<AuditRecord> QueryAsync(
            AuditQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> VerifyChainAsync(EntityId entity, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}
