using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost;
using Harborline.Api.LocalNodeHost.Data.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Search;

/// <summary>
/// Functional tests for the Harborline App KG keyword-search demo (ONR survey
/// <c>onr-carrier-kg-search-calendar-demo-survey-2026-06-24</c>): the dev indexer projects seeded calendar
/// events into <c>calendar-event</c> <c>search_nodes</c> rows, and — WITH the principal's <c>ForRecords</c>
/// grant — the clipped <see cref="NodeSearchReadService.SearchAsync"/> returns those events for the demo
/// terms (<c>stand-up</c> / <c>Dr. Smith</c> / <c>consult</c>). THE LOAD-BEARING assertion: a principal
/// WITHOUT the grant (or a DIFFERENT tenant) gets ZERO hits (the fail-closed clip).
/// </summary>
public sealed class KgCalendarSearchDemoTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56); // 2026-ish

    private static readonly TenantId TenantA = TenantId.FromString("tenant-A");
    private static readonly TenantId TenantB = TenantId.FromString("tenant-B");

    // The principal the demo grant seed + the search route both key on (os:<user> form).
    private static readonly ActorId DemoPrincipal = new("os:demo-user");
    private static readonly ActorId UngrantedPrincipal = new("os:stranger");

    private static readonly Guid SeedActor = new("5eed0000-0000-0000-0000-00000000ca1e");
    private static readonly ParticipantRef DemoResource = ParticipantRef.Party("party-dr-smith");
    private const string DemoTimezone = "America/Los_Angeles";

    /// <summary>Builds a seeded-shape timed event on the demo resource (mirrors CalendarDevSeeder.TimedEvent).</summary>
    private static CalendarEvent TimedEvent(TenantId tenantId, string title, DateOnly date)
    {
        var ev = CalendarEvent.Create(
            tenantId,
            title,
            start: date,
            end: date,
            createdBy: SeedActor,
            rrule: null,
            timezone: DemoTimezone,
            startTime: new TimeOnly(9, 0),
            endTime: new TimeOnly(9, 30),
            occupancy: Occupancy.Bookable);
        ev.SetResource(DemoResource, SeedActor);
        return ev;
    }

    /// <summary>The three demo events whose titles + resource cover stand-up / Dr. Smith / consult.</summary>
    private static IReadOnlyList<CalendarEvent> DemoEvents(TenantId tenant)
    {
        var monday = new DateOnly(2026, 3, 2);
        return new[]
        {
            TimedEvent(tenant, "Daily stand-up", monday),
            TimedEvent(tenant, "New-patient consult", monday.AddDays(1)),
            TimedEvent(tenant, "Care-team meeting", monday.AddDays(2)),
        };
    }

    private static AccessGrant ForRecordGrant(
        TenantId tenant, ActorId principal, string recordId,
        GrantResidency residency = GrantResidency.Cache) =>
        TestSearchAuthorization.Grant(tenant, principal,
            ScopeExpression.Parse($"/records/{recordId}"), Now, residency);

    private static async Task AppendRecordGrantsAsync(
        InMemoryGrantStore grants, TenantId tenant, ActorId principal,
        IEnumerable<string> recordIds, GrantResidency residency = GrantResidency.Cache)
    {
        foreach (var recordId in recordIds)
            await grants.AppendAsync(tenant, ForRecordGrant(tenant, principal, recordId, residency));
    }

    /// <summary>
    /// Indexes the demo events into the store as <c>calendar-event</c> rows using the SAME body projection
    /// the production indexer (<see cref="KgCalendarDevIndexer.BuildSearchBody"/>) produces — one source of
    /// truth, so the test proves the real projection makes "Dr. Smith" searchable. Returns the record ids.
    /// </summary>
    private static async Task<IReadOnlyList<string>> IndexDemoEventsAsync(
        SearchTestStore store, TenantId tenant, IReadOnlyList<CalendarEvent> events)
    {
        var indexer = new NodeSearchIndexer(store.Factory);
        var ids = new List<string>(events.Count);
        foreach (var ev in events)
        {
            var recordId = ev.Id.ToString();
            ids.Add(recordId);
            await indexer.IndexNodeAsync(new SearchNodeRow
            {
                RecordId = recordId,
                TenantId = tenant.ToString(),
                NodeType = KgCalendarDevIndexer.CalendarEventNodeType,
                Title = ev.Title,
                Body = KgCalendarDevIndexer.BuildSearchBody(ev),
                Residency = SearchResidency.Cache,
            });
        }

        return ids;
    }

    [Fact(DisplayName = "Indexer: a seeded calendar event is projected as a calendar-event search node")]
    public async Task Indexer_Projects_CalendarEvent_As_Node()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var events = DemoEvents(TenantA);
        var ids = await IndexDemoEventsAsync(store, TenantA, events);

        // A whole-tenant grant so we read every indexed row back and assert the node_type + title.
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await AppendRecordGrantsAsync(grants, TenantA, DemoPrincipal, ids);

        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));
        var hits = await svc.SearchAsync(TenantA, DemoPrincipal, "stand-up", Now);

        var hit = Assert.Single(hits);
        Assert.Equal(KgCalendarDevIndexer.CalendarEventNodeType, hit.NodeType);
        Assert.Equal("Daily stand-up", hit.Title);
        Assert.Contains(hit.RecordId, ids);
    }

    [Theory(DisplayName = "Clip WITH grant: the demo terms return the seeded calendar events")]
    [InlineData("stand-up", "Daily stand-up")]
    [InlineData("consult", "New-patient consult")]
    [InlineData("Smith", null)]      // resource id substring (party-dr-smith) — any of the 3 events
    [InlineData("Dr. Smith", null)]  // the humanized resource display — the natural acceptance term
    public async Task Clip_With_Grant_Returns_Events_For_Demo_Terms(string term, string? expectTitle)
    {
        await using var store = await SearchTestStore.CreateAsync();
        var events = DemoEvents(TenantA);
        var ids = await IndexDemoEventsAsync(store, TenantA, events);

        // The demo grant: a ForRecords grant naming EXACTLY the seeded event ids (the load-bearing seed).
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await AppendRecordGrantsAsync(grants, TenantA, DemoPrincipal, ids);

        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));
        var hits = await svc.SearchAsync(TenantA, DemoPrincipal, term, Now);

        Assert.NotEmpty(hits);
        // Every hit is one of the seeded calendar-event records (clipped to the grant).
        Assert.All(hits, h => Assert.Contains(h.RecordId, ids));
        Assert.All(hits, h => Assert.Equal(KgCalendarDevIndexer.CalendarEventNodeType, h.NodeType));
        if (expectTitle is not null)
        {
            Assert.Contains(hits, h => h.Title == expectTitle);
        }
    }

    [Fact(DisplayName = "THE CLIP HOLDS: a principal WITHOUT the grant gets ZERO hits (fail-closed)")]
    public async Task Clip_Without_Grant_Returns_Nothing()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var events = DemoEvents(TenantA);
        var ids = await IndexDemoEventsAsync(store, TenantA, events);

        // The grant is seeded ONLY for DemoPrincipal — UngrantedPrincipal holds nothing.
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await AppendRecordGrantsAsync(grants, TenantA, DemoPrincipal, ids);

        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));

        // The SAME query that returns events for the granted principal returns NOTHING for the ungranted one.
        var granted = await svc.SearchAsync(TenantA, DemoPrincipal, "stand-up", Now);
        var ungranted = await svc.SearchAsync(TenantA, UngrantedPrincipal, "stand-up", Now);

        Assert.NotEmpty(granted);   // the grant authorizes the events…
        Assert.Empty(ungranted);    // …and a principal WITHOUT the grant sees zero (the fail-closed clip).
    }

    [Fact(DisplayName = "THE CLIP HOLDS: a DIFFERENT tenant's grant authorizes nothing in this tenant's index")]
    public async Task Clip_Different_Tenant_Returns_Nothing()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var events = DemoEvents(TenantA);
        var ids = await IndexDemoEventsAsync(store, TenantA, events);

        // The principal holds a grant naming the events, but homed at tenant-B (a different tenant). The
        // tenant-A search must authorize nothing — the grant store is queried for (tenant-A, principal) and
        // a tenant-B grant never surfaces.
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await AppendRecordGrantsAsync(grants, TenantB, DemoPrincipal, ids);

        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));
        var hits = await svc.SearchAsync(TenantA, DemoPrincipal, "stand-up", Now);

        Assert.Empty(hits);
    }

    [Fact(DisplayName = "Grant residency: an OnlineOnly grant authorizes NOTHING in the local index (G-3)")]
    public async Task Clip_OnlineOnly_Grant_Returns_Nothing()
    {
        await using var store = await SearchTestStore.CreateAsync();
        var events = DemoEvents(TenantA);
        var ids = await IndexDemoEventsAsync(store, TenantA, events);

        // The grant names the events but is OnlineOnly — the clip drops it (only cache-resident grants count).
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        await AppendRecordGrantsAsync(
            grants, TenantA, DemoPrincipal, ids, GrantResidency.OnlineOnly);

        var svc = new NodeSearchReadService(store.Factory, TestSearchAuthorization.Projection(grants));
        var hits = await svc.SearchAsync(TenantA, DemoPrincipal, "stand-up", Now);

        Assert.Empty(hits);
    }
}
