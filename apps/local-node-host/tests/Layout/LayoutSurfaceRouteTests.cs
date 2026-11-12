using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
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
    private const string SourcesRoot = "invoice-11";
    private const string RegistryRoot = "invoice-r1";
    private static readonly LayoutRecordReference OwnerRecord = new(SurfacePack.PartyType, "party-19");

    /// <summary>
    /// The only source of a <c>layout:open</c> grant in these tests. No seeded role or definition offers the operation
    /// (owner ruling of 2026-11-04), and it is not in the code operation catalogue, so definition admission refuses
    /// one. This feeds the real gate the derivation a grant would produce and leaves every other read to the seeded
    /// production reader. The gate addresses a layout:open target as <c>/records/{surface identity}</c>.
    /// </summary>
    private sealed class FixtureLayoutOpenGrants(
        IAuthorizationClosureSnapshotReader closure, IAuthorizationDefinitionAtomReader atoms)
        : IAuthorizationClosureSnapshotReader, IAuthorizationDefinitionAtomReader
    {
        private static readonly RoleReference SurfaceOpener = new(RoleVocabularies.Domain, "fixture-surface-opener");
        private static readonly AuthorizationOperation Open = AuthorizationOperation.Parse("layout:open");
        private static readonly Dictionary<ActorId, ScopeExpression> Holders = new()
        {
            [Viewer] = ScopeExpression.Parse($"/records/{SurfacePack.Invoice}"),
            [Member] = ScopeExpression.Parse("/"),
            [OpenerOnly] = ScopeExpression.Parse("/"),
        };

        public async ValueTask<AuthorizationClosureSnapshot> ReadAsync(AuthorizationGateRequest request, CancellationToken ct = default)
        {
            var snapshot = await closure.ReadAsync(request, ct);
            if (request.Act.Operation != Open || !Holders.TryGetValue(request.Principal, out var scope)) return snapshot;
            return new AuthorizationClosureSnapshot(
                [.. snapshot.Derivations, new AuthorizationAtomDerivation(new PermissionAtom(Open, scope), SurfaceOpener,
                    "fixture-grant", 1, "fixture-definition", scope, request.At.AddHours(-1), null)],
                snapshot.Excluded);
        }

        public async ValueTask<IReadOnlyList<PermissionAtom>> AtomsForRoleAsync(TenantId tenantId, RoleReference role, CancellationToken ct = default)
            => role == SurfaceOpener
                ? [new PermissionAtom(Open, ScopeExpression.Parse("/"))]
                : await atoms.AtomsForRoleAsync(tenantId, role, ct);
    }

    /// <summary>May open the invoice surface and read the root record only: both grants name exactly one target.</summary>
    private static readonly ActorId Viewer = new("viewer-735");

    /// <summary>May open every surface and read every record.</summary>
    private static readonly ActorId Member = new("member-735");

    /// <summary>Holds no grant.</summary>
    private static readonly ActorId Stranger = new("stranger-735");

    /// <summary>May read every record, and holds no layout:open.</summary>
    private static readonly ActorId ReaderOnly = new("reader-only-735");

    /// <summary>May open every surface, and holds no records:read.</summary>
    private static readonly ActorId OpenerOnly = new("opener-only-735");

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

    [Theory(DisplayName = "T-735/binding-source-closure T-1079: a served root records one RecordRead with the caller, root and optional correlation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_served_root_records_one_read_receipt(bool correlated)
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);
        var correlation = correlated ? Guid.Parse("73500000-0000-4000-8000-000000000001") : (Guid?)null;

        using var response = await h.GetAsync(new ActorId("member-735"), "invoice", "invoice-7", correlation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(await RowsAsync(h.Pipeline.Trail, h.Tenant));
        Assert.Equal("RecordRead", row.EventType.Value);
        Assert.Equal("member-735", row.Actor!.Value.Value);
        Assert.Equal("invoice-7", row.Target!.Value.RecordId);
        Assert.Equal("records:read", row.Act!.Value.Operation.Value);
        Assert.Equal("invoice-7", row.Payload.Payload.Body["recordId"]);
        Assert.NotNull(row.AuthoritySnapshot);
        Assert.Equal(row.AuditId.ToString("D"), Assert.Single(response.Headers.GetValues("X-Harborline-Audit-Id")));
        if (correlated)
        {
            Assert.Equal("73500000-0000-4000-8000-000000000001", row.Payload.Payload.Body["correlation_id"]);
            Assert.Equal("73500000-0000-4000-8000-000000000001",
                Assert.Single(response.Headers.GetValues("X-Harborline-Audit-Correlation")));
        }
        else
        {
            Assert.False(row.Payload.Payload.Body.ContainsKey("correlation_id"));
            Assert.False(response.Headers.Contains("X-Harborline-Audit-Correlation"));
        }
    }

    [Theory(DisplayName = "T-735/binding-source-closure layout-eng-31: a missing or denied root records no RecordRead")]
    [InlineData("member-735", "invoice-404", HttpStatusCode.NotFound)]
    [InlineData("opener-only-735", "invoice-7", HttpStatusCode.Forbidden)]
    [InlineData("opener-only-735", "invoice-404", HttpStatusCode.Forbidden)]
    [InlineData("reader-only-735", "invoice-7", HttpStatusCode.Forbidden)]
    public async Task A_missing_or_denied_root_records_no_read_receipt(string caller, string root, HttpStatusCode expectedStatus)
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        using var response = await h.GetAsync(new ActorId(caller), "invoice", root);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.DoesNotContain(await RowsAsync(h.Pipeline.Trail, h.Tenant), row => row.EventType.Value == "RecordRead");
        if (expectedStatus == HttpStatusCode.NotFound)
        {
            Assert.False(response.Headers.Contains("X-Harborline-Audit-Id"));
            Assert.False(response.Headers.Contains("X-Harborline-Audit-Correlation"));
        }
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

        // The realistic probe: a caller who may open the surface and read one record asks about one it may not read and
        // one that is absent. The opener with no read at all gets that same refusal.
        var scopedDenied = await h.ReadAsync(Viewer, SurfacePack.Invoice, MistypedRoot);
        var scopedUnknown = await h.ReadAsync(Viewer, SurfacePack.Invoice, UnknownRoot);
        var openerDenied = await h.ReadAsync(OpenerOnly, SurfacePack.Invoice, Root);
        var openerUnknown = await h.ReadAsync(OpenerOnly, SurfacePack.Invoice, UnknownRoot);

        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal(denied, unknown);
        Assert.Equal(denied, unknownSurface);
        Assert.Equal(HttpStatusCode.Forbidden, scopedDenied.Status);
        Assert.Equal(scopedDenied, scopedUnknown);
        Assert.Equal(scopedDenied, openerDenied);
        Assert.Equal(scopedDenied, openerUnknown);
        // The decision came before the lookup: the root store was never read for the stranger.
        Assert.Equal(0, h.Roots.Lookups);

        // The control: the root does not exist, and a caller who may read it is told so.
        using var authorized = await h.GetAsync(Member, SurfacePack.Invoice, UnknownRoot);
        Assert.Equal(HttpStatusCode.NotFound, authorized.StatusCode);
        Assert.Equal("layout.surface.root_unknown", await CodeAsync(authorized));
    }

    [Fact(DisplayName = "layout-eng-26 T-735: a caller who may read the root but holds no layout:open is refused before anything is looked up")]
    public async Task A_caller_without_layout_open_is_refused()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        using var response = await h.GetAsync(ReaderOnly, SurfacePack.Invoice, Root);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        Assert.Equal(("authorization.permission_required", "layout:open"),
            (body.RootElement.GetProperty("code").GetString(), body.RootElement.GetProperty("permission").GetString()));
        Assert.DoesNotContain("Fabrikam", text, StringComparison.Ordinal);
        Assert.Equal((0, 0), (h.Roots.Lookups, h.Related.Lookups));
    }

    [Fact(DisplayName = "layout-eng-26 T-735: the same reader holding layout:open is served the surface")]
    public async Task A_caller_with_layout_open_is_served()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        using var refused = await h.GetAsync(ReaderOnly, SurfacePack.Invoice, Root);
        using var served = await h.GetAsync(Member, SurfacePack.Invoice, Root);

        // ReaderOnly and Member hold the same records:read grant; layout:open is the only difference.
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        using var body = JsonDocument.Parse(await served.Content.ReadAsStringAsync());
        Assert.Equal("invoice", body.RootElement.GetProperty("surface").GetString());
        Assert.Contains("supplier=\"Fabrikam\"", Blocks(body.RootElement));
    }

    [Fact(DisplayName = "layout-eng-26 T-735: a layout:open grant on one surface does not open another")]
    public async Task A_grant_on_one_surface_does_not_open_another()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        using var granted = await h.GetAsync(Viewer, SurfacePack.Invoice, Root);
        using var other = await h.GetAsync(Viewer, SurfacePack.ApproversSurface, Root);

        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        using var body = JsonDocument.Parse(await other.Content.ReadAsStringAsync());
        Assert.Equal("layout:open", body.RootElement.GetProperty("permission").GetString());
    }

    [Fact(DisplayName = "layout-eng-26 T-735 (CWE-862, CWE-203): to a caller who cannot open the surface, a readable root, a denied root, an unknown root, an unknown surface and a missing read grant are the same bytes")]
    public async Task Callers_who_cannot_open_the_surface_cannot_tell_what_else_applies()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        var observed = new[]
        {
            await h.ReadAsync(ReaderOnly, SurfacePack.Invoice, Root),
            await h.ReadAsync(ReaderOnly, SurfacePack.Invoice, UnknownRoot),
            await h.ReadAsync(ReaderOnly, "no-such-surface", Root),
            await h.ReadAsync(Stranger, SurfacePack.Invoice, Root),
            await h.ReadAsync(Stranger, SurfacePack.Invoice, UnknownRoot),
            await h.ReadAsync(Stranger, "no-such-surface", UnknownRoot),
            // A grant on one surface: this other surface exists, this one does not.
            await h.ReadAsync(Viewer, SurfacePack.ApproversSurface, Root),
            await h.ReadAsync(Viewer, "no-such-surface", UnknownRoot),
        };

        Assert.Equal(HttpStatusCode.Forbidden, observed[0].Status);
        Assert.All(observed, each => Assert.Equal(observed[0], each));
        Assert.Equal((0, 0), (h.Roots.Lookups, h.Related.Lookups));
        // The control: holding layout:open changes the answer, so the equality above is not vacuous.
        Assert.Equal(HttpStatusCode.OK, (await h.ReadAsync(Member, SurfacePack.Invoice, Root)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await h.ReadAsync(Member, SurfacePack.Invoice, UnknownRoot)).Status);
    }

    [Fact(DisplayName = "T-735 owner ruling 2026-11-04: which roles hold layout:open is not yet ruled, so no seeded role offers it and it is not in the code operation catalogue")]
    public async Task No_seeded_role_offers_layout_open()
    {
        var tenant = NodeTenant.Resolve(NodeTestActiveTeam.Accessor);
        var services = new ServiceCollection();
        services.AddSingleton(TestAuthorization.AllowGate());
        services.AddAccessGrantModule();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<AccessGrantAuthorizationSeed>()
            .InstallAsync(tenant, AdmittedInstant.FromRecordedAct(At), AuthorizationSeedProfile.Production, TestDesktopOperator.Actor);
        var atoms = provider.GetRequiredService<IAuthorizationDefinitionAtomReader>();

        foreach (var role in new[]
                 {
                     AccessGrantAuthorizationSeed.MemberRole, AccessGrantAuthorizationSeed.NodeOperatorRole,
                     RoleReference.Administrator, RoleReference.Auditor,
                 })
            Assert.DoesNotContain(await atoms.AtomsForRoleAsync(tenant, role),
                atom => atom.Operation.Value == "layout:open");
        Assert.DoesNotContain(PermissionVocabulary.Operations, operation => operation.Value == "layout:open");
    }

    [Theory(DisplayName = "T-735 layout-ck-28: a related key with no single-target Records reference on the root's Record Type refuses")]
    [InlineData(SurfacePack.ApproversSurface, "layout.surface.relationship_many_target", "finance.invoice.approvers")]
    [InlineData(SurfacePack.PlainFieldSurface, "layout.surface.relationship_undeclared", "finance.invoice.supplier")]
    [InlineData(SurfacePack.UnknownFieldSurface, "layout.surface.relationship_undeclared", "finance.invoice.payee")]
    [InlineData(SurfacePack.NestedSurface, "layout.surface.relationship_undeclared", "finance.invoice.owner")]
    [InlineData(SurfacePack.RepeatedRelatedSurface, "layout.surface.relationship_undeclared", "finance.invoice.owner")]
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
        // No target was found, so the gate was never asked about one and no related denial was traced.
        await h.Pipeline.Appender.IdleAsync();
        Assert.Equal(["RecordRead", "RecordRead"],
            (await RowsAsync(h.Pipeline.Trail, h.Tenant)).Select(row => row.EventType.Value));
    }

    [Fact(DisplayName = "layout-eng-31 T-735: over HTTP, a missing and a denied related target give an unauthorized caller the same status and body bytes")]
    public async Task Missing_and_denied_related_targets_are_the_same_bytes_over_http()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        h.Related.Present = false;
        var missing = await h.ReadAsync(Viewer, SurfacePack.Invoice, Root);
        await h.Pipeline.Appender.IdleAsync();
        Assert.Equal("RecordRead", Assert.Single(await RowsAsync(h.Pipeline.Trail, h.Tenant)).EventType.Value);
        h.Related.Present = true;
        var denied = await h.ReadAsync(Viewer, SurfacePack.Invoice, Root);
        await h.Pipeline.Appender.IdleAsync();

        Assert.Equal(HttpStatusCode.OK, missing.Status);
        Assert.Equal(missing, denied);
        // The denied path is real: the target exists, the gate refused it and the trace recorded it.
        var rows = await RowsAsync(h.Pipeline.Trail, h.Tenant);
        Assert.Equal(2, rows.Count(row => row.EventType.Value == "RecordRead"));
        Assert.Equal("LayoutRelatedDenied", Assert.Single(rows, row => row.EventType.Value != "RecordRead").EventType.Value);
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
    /// <remarks>
    /// T-1195: the request leg before the route starts its floor and the response leg after it (the receipt, the
    /// serialization, Kestrel and the client) are the same on both paths but are not free, and on a fresh process
    /// they get cheaper and steadier only after about 150 requests at the floor. The calibration runs the denied
    /// path at no floor only, so without a warm-up the 60 pairs ran during that settling: on the hosted gate the
    /// time past the floor fell from about 1.0 ms to 0.65 ms across the pairs, and the paired differences spread
    /// to ±0.15 ms around a zero median. <see cref="RouteWarmUpPairs"/> discarded pairs at the floor run first.
    /// </remarks>
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
        }, h.Pipeline.Appender.IdleAsync, "route", output, RouteWarmUpPairs);
    }

    /// <summary>
    /// T-1195: 240 requests at the floor, both paths, before the first counted pair. On a 4-CPU Linux container the
    /// time past the floor stopped falling after about 150 requests, so this leaves a margin past it.
    /// </summary>
    private const int RouteWarmUpPairs = 120;

    [Fact(DisplayName = "layout-eng-31 T-735: the response floor counts from the start of the route, so the route's own lookups are inside the deadline")]
    public async Task The_floor_counts_the_routes_own_work()
    {
        await using var h = await Host.CreateAsync(TimeSpan.FromMilliseconds(200));
        Assert.Equal(HealthStatus.Healthy, (await h.Pipeline.HealthAsync()).Status);

        // The root lookup alone outruns the floor. Were the floor counted from the host, it would not overrun.
        h.Roots.Cost = TimeSpan.FromMilliseconds(300);
        using var response = await h.GetAsync(Member, SurfacePack.Invoice, Root);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var alert = await h.Pipeline.HealthAsync();
        Assert.Equal(HealthStatus.Degraded, alert.Status);
        Assert.Equal(1L, alert.Data["floorOverruns"]);
    }

    [Theory(DisplayName = "layout-eng-31 T-1195: every served path writes its one RecordRead receipt inside the response floor, so a receipt slower than the floor overruns it")]
    [InlineData("allowed")]
    [InlineData("denied")]
    [InlineData("missing")]
    public async Task Every_served_path_writes_its_receipt_inside_the_floor(string path)
    {
        await using var h = await Host.CreateAsync(TimeSpan.FromMilliseconds(200));
        h.Related.Present = path != "missing";
        var caller = path == "allowed" ? Member : Viewer;
        // A first request with a free receipt, so a cold process's own first-request work cannot be the overrun.
        using (await h.GetAsync(caller, SurfacePack.Invoice, Root)) { }
        await h.Pipeline.Appender.IdleAsync();
        var overruns = h.Pipeline.Alarms.FloorOverruns;
        var receipts = (await ReceiptsAsync(h)).Count;

        // The receipt alone outruns the floor. Were it written after the floor ended, nothing would overrun.
        h.Receipts.Cost = TimeSpan.FromMilliseconds(300);
        using var response = await h.GetAsync(caller, SurfacePack.Invoice, Root);
        await h.Pipeline.Appender.IdleAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(receipts + 1, (await ReceiptsAsync(h)).Count);
        Assert.Equal(overruns + 1, h.Pipeline.Alarms.FloorOverruns);
        Assert.Equal(HealthStatus.Degraded, (await h.Pipeline.HealthAsync()).Status);

        static async Task<List<AuditRecord>> ReceiptsAsync(Host h)
            => [.. (await RowsAsync(h.Pipeline.Trail, h.Tenant)).Where(row => row.EventType.Value == "RecordRead")];
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

    [Fact(DisplayName = "T-735 layout-ck-39: a surface binds through a Record Type its pack holds only in the signed dependency it pinned")]
    public async Task A_record_type_in_the_dependency_closure_binds()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        using var response = await h.GetAsync(Member, SurfacePack.DependencySurface, Root);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["owner-card=\"Owner\"", "owner-name=\"Contoso\"", "supplier=\"Fabrikam\""], Blocks(body.RootElement));
    }

    [Fact(DisplayName = "T-735 layout-ck-39: a closure that declares the root's Record Type twice has no one declaration, so the key refuses")]
    public async Task A_record_type_declared_twice_in_the_closure_refuses()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);
        await SurfacePack.InstallRedeclaringPackAsync(h.Packs, h.Tenant);

        using var response = await h.GetAsync(Member, SurfacePack.RedeclaredSurface, Root);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("layout.surface.relationship_undeclared", await CodeAsync(response));
    }

    [Theory(DisplayName = "T-735/binding-source-closure layout-ck-39: only an exposed dependency Record Type binds at the route")]
    [InlineData(false, HttpStatusCode.UnprocessableEntity)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task Only_an_exposed_dependency_record_type_binds(bool exposed, HttpStatusCode expectedStatus)
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero, exposeRecordTypes: exposed);

        using var response = await h.GetAsync(Member, "invoice-from-dependency", "invoice-7");

        Assert.Equal(expectedStatus, response.StatusCode);
        if (exposed)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(["owner-card=\"Owner\"", "owner-name=\"Contoso\"", "supplier=\"Fabrikam\""], Blocks(body.RootElement));
            Assert.Empty(body.RootElement.GetProperty("refusals").EnumerateArray());
            Assert.Equal(1, h.Related.Lookups);
        }
        else
        {
            Assert.Equal("layout.surface.relationship_undeclared", await CodeAsync(response));
            Assert.Equal(0, h.Related.Lookups);
        }
    }

    [Fact(DisplayName = "T-735: a surface identity two Active packs carry refuses with layout.surface.ambiguous and is not chosen between")]
    public async Task A_surface_two_active_packs_carry_refuses()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);
        await SurfacePack.InstallDuplicateSurfaceAsync(h.Packs, h.Tenant);

        using var response = await h.GetAsync(Member, SurfacePack.Invoice, Root);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("layout.surface.ambiguous", await CodeAsync(response));
    }

    [Fact(DisplayName = "T-735 layout-eng-17: a repeating block resolves per row, a dotted path reads into the record, and an undeclared field or a query with no host source refuses by block")]
    public async Task The_host_field_sources_resolve_rows_paths_and_refusals()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        using var response = await h.GetAsync(Member, SurfacePack.SourcesSurface, SourcesRoot);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            // The repeating container places itself with no value, then one child per row (layout-run-2).
            ["lines=null", "line-amount=5", "line-amount=7", "customer-name=\"Tailspin\"", "customer-missing=null"],
            Blocks(body.RootElement));
        Assert.Equal(
            [("undeclared", "not_a_field", "layout.binding.unresolvable"), ("view", "finance.open-invoices", "layout.binding.unresolvable")],
            body.RootElement.GetProperty("refusals").EnumerateArray().Select(refusal => (
                refusal.GetProperty("blockId").GetString(), refusal.GetProperty("name").GetString(), refusal.GetProperty("code").GetString())));
    }

    [Fact(DisplayName = "T-735: every response and refusal the route writes is marked no-store")]
    public async Task Every_response_is_no_store()
    {
        await using var h = await Host.CreateAsync(TimeSpan.Zero);

        using var served = await h.GetAsync(Member, SurfacePack.Invoice, Root);
        using var unknown = await h.GetAsync(Member, SurfacePack.Invoice, UnknownRoot);
        using var refused = await h.GetAsync(ReaderOnly, SurfacePack.Invoice, Root);

        Assert.All(new[] { served, unknown, refused }, each =>
            Assert.True(each.Headers.CacheControl?.NoStore, "the response must not be cached"));
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

    /// <summary>The accepted-act trail the route writes its receipts to, with a CPU cost per receipt (no timer).</summary>
    private sealed class CostedReceipts(IAuthorizedAuditTrail inner) : IAuthorizedAuditTrail
    {
        public TimeSpan Cost { get; set; }

        public ValueTask AppendAuthorizedAsync(AuditRecord record, AuthorizationDecision decision, CancellationToken ct = default,
            SeparationOfDutyDecision? approval = null)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < Cost) Thread.SpinWait(64);
            return inner.AppendAuthorizedAsync(record, decision, ct, approval);
        }

        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) => inner.AppendAsync(record, ct);

        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default) => inner.QueryAsync(query, ct);
    }

    private sealed class Host : IAsyncDisposable
    {
        private const string CallerHeader = "X-Test-Caller";
        internal const string DesktopHeader = "X-Test-Desktop-Plane";
        private readonly WebApplication _app;
        private readonly ServiceProvider _authorization;
        private readonly KeyPair _receiptKeys;

        private Host(WebApplication app, ServiceProvider authorization, Pipeline pipeline, LayoutSurfaceOptions options,
            FixtureRoots roots, FixtureRelatedRecords related, Harborline.Api.Foundation.Packs.Install.InMemoryPackInstallStore packs,
            CostedReceipts receipts, KeyPair receiptKeys)
        {
            Receipts = receipts;
            _receiptKeys = receiptKeys;
            Packs = packs;
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
        public Harborline.Api.Foundation.Packs.Install.InMemoryPackInstallStore Packs { get; }
        public TenantId Tenant { get; } = NodeTenant.Resolve(NodeTestActiveTeam.Accessor);
        public Pipeline Pipeline { get; }
        public LayoutSurfaceOptions Options { get; }
        public FixtureRoots Roots { get; }
        public FixtureRelatedRecords Related { get; }
        public CostedReceipts Receipts { get; }

        public static async Task<Host> CreateAsync(TimeSpan floor, ILayoutRelatedRecordSource? relatedSource = null,
            ILayoutRootRecordSource? rootSource = null, bool exposeRecordTypes = true)
        {
            var tenant = NodeTenant.Resolve(NodeTestActiveTeam.Accessor);
            var authorization = await SeededAuthorizationAsync(tenant);
            var grants = new FixtureLayoutOpenGrants(
                authorization.GetRequiredService<IAuthorizationClosureSnapshotReader>(),
                authorization.GetRequiredService<IAuthorizationDefinitionAtomReader>());
            var gate = new AuthorizationGate(grants, authorization.GetRequiredService<IRecordStandingResolver>(), grants);
            var pipeline = new Pipeline(new InMemoryAuditTrail());
            var options = new LayoutSurfaceOptions { ResponseFloor = floor };
            var time = new FixedTime(At);
            var surfaces = new LayoutSurfaceHost(gate, pipeline.Outbox, pipeline.Appender, pipeline.Alarms, time,
                NullLogger.Instance, options);
            var packs = await SurfacePack.InstallAsync(tenant, exposeRecordTypes);
            var roots = new FixtureRoots();
            roots.Add(Root, SurfacePack.InvoiceType,
                """{"supplier":"Fabrikam","owner":{"type":"finance.party","id":"party-19"}}""");
            roots.Add(SourcesRoot, SurfacePack.InvoiceType,
                """{"customer":{"name":"Tailspin"},"lines":[{"amount":5},{"amount":7}]}""");
            roots.Add(MistypedRoot, SurfacePack.InvoiceType,
                """{"supplier":"Fabrikam","owner":{"type":"finance.invoice","id":"party-19"}}""");
            var related = new FixtureRelatedRecords();
            related.Add(OwnerRecord, """{"name":"Contoso"}""");

            var builder = WebApplication.CreateBuilder();
            TestDesktopOperator.AddTestDesktopOperator(builder.Services);
            builder.Services.AddSingleton(gate);
            var receipts = new CostedReceipts((IAuthorizedAuditTrail)pipeline.Trail);
            var receiptKeys = KeyPair.Generate();
            builder.Services.AddSingleton(new AuthorizedActAudit(receipts, new Ed25519Signer(receiptKeys),
                NullLogger<AuthorizedActAudit>.Instance));
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
            LayoutSurfaceRoutes.Map(app.MapSelectedSessionProductGroup(), surfaces, packs, rootSource ?? roots, relatedSource ?? related,
                time, app.Services.GetRequiredService<AuthorizedActAudit>());
            LayoutDenialRoutes.Map(app.MapDeviceReachableProductDataGroup(),
                new LayoutDenialReader(pipeline.Trail, gate), NodeTestActiveTeam.Accessor, time);
            await app.StartAsync();
            return new Host(app, authorization, pipeline, options, roots, related, packs, receipts, receiptKeys);
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
            await grants.AppendAsync(tenant, Grant(tenant, ReaderOnly, member, "/"));
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
            _receiptKeys.Dispose();
        }
    }
}
