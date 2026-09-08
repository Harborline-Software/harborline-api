using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Calendar.DependencyInjection;
using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Calendar;

/// <summary>
/// ONR app-calendar survey 2026-06-24, inc-1 binary gate — route-level tests for the node-local
/// READ-ONLY calendar API. Hosts the SAME production route handlers
/// <see cref="HostedCalendarApiEndpoint"/> registers (<see cref="CalendarRoutes.Map"/> — the single
/// source of truth shared with these tests, no test/prod wire drift) on a real in-process Kestrel
/// listener, and drives them with a real <see cref="HttpClient"/>. Mirrors <c>PropertyRouteTests</c>.
/// </summary>
/// <remarks>
/// Proves: the agenda returns UTC instants; free/busy returns free + busy UTC intervals; the tenant is
/// server-side (the active-team-derived tenant, the Harborline App sends none); cross-tenant isolation (a
/// different active team sees a different — empty — calendar); and the input validation (400 on a
/// malformed resource / window). Uses the block's in-memory stores (the route contract is store-
/// agnostic; the durable store is covered by <c>NodeEfCalendarStoreTests</c>).
/// </remarks>
public sealed class CalendarRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000000a1"));
    private static readonly TeamId TeamB = new(Guid.Parse("bbbb0000-0000-0000-0000-0000000000b1"));
    private static readonly TenantId TenantA = ActiveTeamTenantContext.ProjectTenantId(TeamA);
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly ParticipantRef Doctor = ParticipantRef.Party("party-dr-smith");
    private static readonly CalendarId PersonalCalendar = CalendarId.NewId();

    private const string Base = "/api/local-node/calendar";
    private static readonly DateOnly Day = new(2026, 3, 4); // a Wednesday

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // The calendar block (in-memory stores + the query / expansion / free-busy services).
        builder.Services.AddBlocksCalendar();

        _app = builder.Build();

        // Seed via the resolved stores BEFORE mapping (a 9-5 availability + a Blocking lunch + a
        // Bookable appointment on Dr. Smith, tenant A).
        var eventStore = _app.Services.GetRequiredService<ICalendarEventStore>();
        var availStore = _app.Services.GetRequiredService<IResourceAvailabilityStore>();

        await availStore.SaveAsync(
            ResourceAvailability.Create(TenantA, Doctor, "UTC")
                .AddWindow(AvailabilityWindow.Create(Day, new TimeOnly(9, 0), new TimeOnly(17, 0))));

        await eventStore.SaveAsync(TimedEvent("Lunch", new TimeOnly(12, 0), new TimeOnly(13, 0), Occupancy.Blocking));
        await eventStore.SaveAsync(TimedEvent("Checkup", new TimeOnly(10, 0), new TimeOnly(10, 30), Occupancy.Bookable));
        var personalEvent = CalendarEvent.Create(TenantA, "Planning", Day, Day, Actor,
            timezone: "UTC", startTime: new TimeOnly(14, 0), endTime: new TimeOnly(15, 0));
        personalEvent.SetCalendarId(PersonalCalendar, Actor);
        await eventStore.SaveAsync(personalEvent);

        // The active team accessor — defaults to team A; tests can switch it to prove server-side tenant.
        _activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));

        // Map the SAME production routes (mirrors HostedCalendarApiEndpoint wiring — no [FromServices]).
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        CalendarRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            _app.Services.GetRequiredService<ICalendarParticipantCalendarQuery>(),
            _app.Services.GetRequiredService<ICalendarEventStore>(),
            _app.Services.GetRequiredService<ICalendarEventExpansionService>(),
            _app.Services.GetRequiredService<IFreeBusyService>(),
            _activeTeam);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static CalendarEvent TimedEvent(string title, TimeOnly start, TimeOnly end, Occupancy occupancy)
    {
        var ev = CalendarEvent.Create(TenantA, title, Day, Day, Actor,
            timezone: "UTC", startTime: start, endTime: end, occupancy: occupancy);
        // SetResource makes the resource both the headline ResourceRef (free/busy reads this) AND a
        // Resource participation (the agenda's EventsFor matches on participation).
        ev.SetResource(Doctor, Actor);
        return ev;
    }

    private static string FromUtc => new DateTimeOffset(Day.Year, Day.Month, Day.Day, 0, 0, 0, TimeSpan.Zero)
        .ToString("O");
    private static string ToUtc => new DateTimeOffset(Day.Year, Day.Month, Day.Day, 23, 0, 0, TimeSpan.Zero)
        .ToString("O");

    private static string Q(string path, string resource) =>
        $"{path}?resource={Uri.EscapeDataString(resource)}&fromUtc={Uri.EscapeDataString(FromUtc)}&toUtc={Uri.EscapeDataString(ToUtc)}";

    private static string CalendarQ(string path, CalendarId calendarId) =>
        $"{path}?calendarId={calendarId.Value:D}&fromUtc={Uri.EscapeDataString(FromUtc)}&toUtc={Uri.EscapeDataString(ToUtc)}";

    // ── agenda ────────────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "agenda: returns the resource's occurrences as UTC instants")]
    public async Task Agenda_Returns_UtcInstants()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Q($"{Base}/occurrences", "party:party-dr-smith"));
        var data = doc.GetProperty("data");
        Assert.Equal(2, data.GetArrayLength()); // Lunch + Checkup

        // Every occurrence carries a UTC instant pair (Z-suffixed / +00:00 offset, round-trippable).
        foreach (var o in data.EnumerateArray())
        {
            var start = o.GetProperty("startUtc").GetString()!;
            var end = o.GetProperty("endUtc").GetString()!;
            Assert.True(DateTimeOffset.TryParse(start, out var s));
            Assert.True(DateTimeOffset.TryParse(end, out var e));
            Assert.Equal(TimeSpan.Zero, s.Offset); // serialized as UTC
            Assert.True(e > s);
        }

        // Ordered by start: Checkup (10:00) before Lunch (12:00).
        var titles = data.EnumerateArray().Select(o => o.GetProperty("title").GetString()).ToList();
        Assert.Equal(new[] { "Checkup", "Lunch" }, titles);
    }

    [Fact(DisplayName = "agenda: an unknown resource returns an empty agenda (no existence leak)")]
    public async Task Agenda_UnknownResource_Empty()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Q($"{Base}/occurrences", "party:nobody"));
        Assert.Equal(0, doc.GetProperty("data").GetArrayLength());
    }

    [Fact(DisplayName = "agenda: a personal calendar returns its owned occurrences")]
    [Trait("PlanCard", "2083")]
    public async Task Agenda_PersonalCalendar_ReturnsOwnedOccurrences()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(
            CalendarQ($"{Base}/occurrences", PersonalCalendar));
        var occurrence = Assert.Single(doc.GetProperty("data").EnumerateArray());
        Assert.Equal("Planning", occurrence.GetProperty("title").GetString());
    }

    [Fact(DisplayName = "agenda: an unknown calendar returns an empty agenda")]
    [Trait("PlanCard", "2083")]
    public async Task Agenda_UnknownCalendar_Empty()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(
            CalendarQ($"{Base}/occurrences", CalendarId.NewId()));
        Assert.Equal(0, doc.GetProperty("data").GetArrayLength());
    }

    // ── free/busy ───────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "free-busy: returns free slots + busy intervals as UTC")]
    public async Task FreeBusy_Returns_FreeAndBusy()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Q($"{Base}/free-busy", "party:party-dr-smith"));

        Assert.Equal("party:party-dr-smith", doc.GetProperty("resource").GetString());

        var free = doc.GetProperty("freeSlots");
        var busy = doc.GetProperty("busyIntervals");

        // Busy = the Checkup (10-10:30) + the Lunch (12-13) — both occupy the resource.
        Assert.Equal(2, busy.GetArrayLength());
        // Free = the gaps in 9-17 around those two busy spans (9-10, 10:30-12, 13-17).
        Assert.True(free.GetArrayLength() >= 1);

        // All intervals are UTC.
        foreach (var i in free.EnumerateArray().Concat(busy.EnumerateArray()))
        {
            Assert.True(DateTimeOffset.TryParse(i.GetProperty("startUtc").GetString(), out var s));
            Assert.Equal(TimeSpan.Zero, s.Offset);
        }
    }

    [Fact(DisplayName = "free-busy: a resource with no availability has no free slots")]
    public async Task FreeBusy_NoAvailability_NoFreeSlots()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Q($"{Base}/free-busy", "asset:room-7"));
        Assert.Equal(0, doc.GetProperty("freeSlots").GetArrayLength());
    }

    // ── server-side tenant + cross-tenant isolation ────────────────────────────────────────────────

    [Fact(DisplayName = "tenant: switching the active team yields a different (empty) calendar — server-side tenant")]
    public async Task Tenant_IsServerSide_CrossTenantIsolated()
    {
        // The Harborline App never sends a tenant id; the route resolves it from the active team. Switch the
        // active team to B → the same resource query reads tenant B's (empty) calendar.
        _activeTeam.Active = TeamContextFor(TeamB, "Team B");

        var agenda = await _client.GetFromJsonAsync<JsonElement>(Q($"{Base}/occurrences", "party:party-dr-smith"));
        Assert.Equal(0, agenda.GetProperty("data").GetArrayLength());

        var fb = await _client.GetFromJsonAsync<JsonElement>(Q($"{Base}/free-busy", "party:party-dr-smith"));
        Assert.Equal(0, fb.GetProperty("freeSlots").GetArrayLength());

        // Restore for any later assertion ordering independence.
        _activeTeam.Active = TeamContextFor(TeamA, "Team A");
    }

    // ── input validation ───────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "validation: a malformed resource ref is a 400")]
    public async Task Validation_MalformedResource_400()
    {
        var resp = await _client.GetAsync(Q($"{Base}/occurrences", "not-a-ref"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "validation: a missing window is a 400")]
    public async Task Validation_MissingWindow_400()
    {
        var resp = await _client.GetAsync($"{Base}/free-busy?resource=party:party-dr-smith");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "validation: an inverted window (to <= from) is a 400")]
    public async Task Validation_InvertedWindow_400()
    {
        var inverted =
            $"{Base}/occurrences?resource=party:party-dr-smith&fromUtc={Uri.EscapeDataString(ToUtc)}&toUtc={Uri.EscapeDataString(FromUtc)}";
        var resp = await _client.GetAsync(inverted);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── test doubles ───────────────────────────────────────────────────────────────────────────────

    private static TeamContext TeamContextFor(TeamId teamId, string name)
        => new(teamId, name, new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    /// <summary>A mutable active-team accessor so a test can switch the active team mid-flight.</summary>
    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        public MutableActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; set; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
