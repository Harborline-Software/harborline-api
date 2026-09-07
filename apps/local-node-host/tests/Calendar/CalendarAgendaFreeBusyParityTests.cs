using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// REGRESSION (bug-NNNN): the agenda (<c>/calendar/occurrences</c>) and the free/busy
/// (<c>/calendar/free-busy</c>) endpoints must agree on the SAME window — every busy interval the
/// free/busy implies has a matching agenda occurrence. The original <c>ReferenceAppRoute_Returns_SeededEvents</c>
/// only covered a single NON-recurring, in-window, UTC-tz event over a WEEK window; this exercises the
/// real seed shape — a recurring DAILY event (whose master anchor precedes the window) + a same-day
/// non-recurring event, in <c>America/Los_Angeles</c>, on a SetResource'd resource — over a single
/// LOCAL day, the Harborline App's real query.
/// </summary>
public sealed class CalendarAgendaFreeBusyParityTests
{
    private static readonly TeamId Team = new(Guid.Parse("eeee0000-0000-0000-0000-0000000000e1"));
    private static readonly TenantId Tenant = ActiveTeamTenantContext.ProjectTenantId(Team);
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly ParticipantRef Doctor = ParticipantRef.Party("party-dr-smith");
    private const string Resource = "party:party-dr-smith";
    private const string LA = "America/Los_Angeles";

    [Fact(DisplayName = "parity: agenda returns exactly the events free/busy implies (recurring + resource-role, LA tz, single local day)")]
    public async Task Agenda_Matches_FreeBusy_For_Recurring_And_ResourceRole_LocalDay()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddBlocksCalendar();

        var app = builder.Build();
        try
        {
            var eventStore = app.Services.GetRequiredService<ICalendarEventStore>();
            var availStore = app.Services.GetRequiredService<IResourceAvailabilityStore>();

            // The window: a single LA-LOCAL day (the Harborline App views one day). Pick a fixed Wednesday well
            // inside PDT so the test is deterministic regardless of when it runs.
            var localDay = new DateOnly(2026, 6, 24); // a Wednesday, PDT (UTC-7)
            var tz = TimeZoneInfo.FindSystemTimeZoneById(LA);
            var fromUtc = new DateTimeOffset(localDay.ToDateTime(new TimeOnly(0, 0), DateTimeKind.Unspecified), tz.GetUtcOffset(localDay.ToDateTime(new TimeOnly(0, 0))));
            var toUtc = new DateTimeOffset(localDay.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Unspecified), tz.GetUtcOffset(localDay.ToDateTime(new TimeOnly(23, 59))));

            // A Mon–Fri 09:00–17:00 LA availability (the bookable supply), anchored on this week's Monday.
            var monday = localDay.AddDays(-(((int)localDay.DayOfWeek + 6) % 7));
            await availStore.SaveAsync(
                ResourceAvailability.Create(Tenant, Doctor, LA)
                    .AddWindow(AvailabilityWindow.Create(
                        monday, new TimeOnly(9, 0), new TimeOnly(17, 0),
                        rrule: "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR")));

            // 1) A DAILY-RECURRING stand-up whose master anchor (Monday) PRECEDES the window day (Wed).
            await eventStore.SaveAsync(TimedEvent(
                "Daily stand-up", monday, new TimeOnly(9, 0), new TimeOnly(9, 15),
                Occupancy.Bookable, rrule: "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"));
            // 2) A DAILY-RECURRING blocking lunch (also anchored Monday).
            await eventStore.SaveAsync(TimedEvent(
                "Lunch", monday, new TimeOnly(12, 0), new TimeOnly(13, 0),
                Occupancy.Blocking, rrule: "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"));
            // 3) A same-day NON-recurring appointment on the window day itself.
            await eventStore.SaveAsync(TimedEvent(
                "Consult", localDay, new TimeOnly(10, 0), new TimeOnly(10, 45), Occupancy.Bookable));

            var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(Team, "Dev Team"));
            app.Use(async (http, next) =>
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
                await next(http);
            });
            CalendarRoutes.Map(
                app.MapDeviceReachableProductDataGroup(),
                app.Services.GetRequiredService<ICalendarParticipantCalendarQuery>(),
                app.Services.GetRequiredService<ICalendarEventStore>(),
                app.Services.GetRequiredService<ICalendarEventExpansionService>(),
                app.Services.GetRequiredService<IFreeBusyService>(),
                activeTeam);

            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

            var occDoc = await client.GetFromJsonAsync<JsonElement>(Url("occurrences", fromUtc, toUtc));
            var fbDoc = await client.GetFromJsonAsync<JsonElement>(Url("free-busy", fromUtc, toUtc));

            var occStarts = occDoc.GetProperty("data").EnumerateArray()
                .Select(o => DateTimeOffset.Parse(o.GetProperty("startUtc").GetString()!).ToUniversalTime())
                .OrderBy(d => d)
                .ToList();
            var busyStarts = fbDoc.GetProperty("busyIntervals").EnumerateArray()
                .Select(b => DateTimeOffset.Parse(b.GetProperty("startUtc").GetString()!).ToUniversalTime())
                .OrderBy(d => d)
                .ToList();

            // Free/busy sees all three events (stand-up, lunch, consult) — assert it actually has busy.
            Assert.True(busyStarts.Count >= 3,
                $"expected free/busy to imply >= 3 busy intervals, got {busyStarts.Count}");

            // THE PARITY ASSERTION: every busy interval the free/busy implies has a matching agenda
            // occurrence at the same start instant. (Before the fix, the agenda is EMPTY for the recurring
            // masters / the resource-role event while free/busy has data.)
            foreach (var busy in busyStarts)
            {
                Assert.Contains(occStarts, s => s == busy);
            }
            Assert.True(occStarts.Count >= 3,
                $"expected the agenda to return >= 3 occurrences (it currently returns {occStarts.Count})");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact(DisplayName = "seeder: the seeded LA 9–5 window + events resolve to the CORRECT UTC instants (no double-shift)")]
    public async Task Seeded_LA_Window_Resolves_To_Correct_Utc_Instants()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddBlocksCalendar();

        var app = builder.Build();
        try
        {
            var eventStore = app.Services.GetRequiredService<ICalendarEventStore>();
            var availStore = app.Services.GetRequiredService<IResourceAvailabilityStore>();
            var calendarStore = app.Services.GetRequiredService<ICalendarStore>();
            var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(Team, "Dev Team"));

            // Run the REAL dev seeder (its 9–5 LA window + recurring/blocking events) for this tenant.
            var seeder = new Harborline.Api.LocalNodeHost.CalendarDevSeeder(
                eventStore, availStore, calendarStore, activeTeam,
                new FakeHostEnvironment(Environments.Development),
                NullLogger<Harborline.Api.LocalNodeHost.CalendarDevSeeder>.Instance);
            await seeder.StartAsync(CancellationToken.None);

            app.Use(async (http, next) =>
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
                await next(http);
            });
            CalendarRoutes.Map(
                app.MapDeviceReachableProductDataGroup(),
                app.Services.GetRequiredService<ICalendarParticipantCalendarQuery>(),
                app.Services.GetRequiredService<ICalendarEventStore>(),
                app.Services.GetRequiredService<ICalendarEventExpansionService>(),
                app.Services.GetRequiredService<IFreeBusyService>(),
                activeTeam);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

            // Query a broad UTC window covering the whole seeded week.
            var now = DateTimeOffset.UtcNow;
            var from = new DateTimeOffset(now.UtcDateTime.Date.AddDays(-7), TimeSpan.Zero);
            var to = new DateTimeOffset(now.UtcDateTime.Date.AddDays(7), TimeSpan.Zero);

            var fbDoc = await client.GetFromJsonAsync<JsonElement>(Url("free-busy", from, to));

            // The seeded 9 a.m. LA stand-up must resolve to 16:00 UTC (PDT, UTC-7) or 17:00 UTC (PST,
            // UTC-8) — never a double-shifted local-as-UTC value (e.g. 09:00 UTC). Assert every free-slot
            // / busy-interval start is at an LA-correct wall-clock minute: its LA-local hour is in [9,17].
            var tz = TimeZoneInfo.FindSystemTimeZoneById(LA);
            var allStartsUtc = fbDoc.GetProperty("busyIntervals").EnumerateArray()
                .Concat(fbDoc.GetProperty("freeSlots").EnumerateArray())
                .Select(x => DateTimeOffset.Parse(x.GetProperty("startUtc").GetString()!).ToUniversalTime())
                .ToList();
            Assert.NotEmpty(allStartsUtc);
            foreach (var startUtc in allStartsUtc)
            {
                var localHour = TimeZoneInfo.ConvertTime(startUtc, tz).Hour;
                Assert.InRange(localHour, 9, 17); // the 9–5 LA business window — NOT a double-shifted 0–8
            }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static CalendarEvent TimedEvent(
        string title, DateOnly date, TimeOnly start, TimeOnly end, Occupancy occupancy, string? rrule = null)
    {
        var ev = CalendarEvent.Create(
            Tenant, title, date, date, Actor,
            rrule: rrule, timezone: LA, startTime: start, endTime: end, occupancy: occupancy);
        ev.SetResource(Doctor, Actor);
        return ev;
    }

    private static string Url(string leaf, DateTimeOffset from, DateTimeOffset to) =>
        $"/api/local-node/calendar/{leaf}?resource={Uri.EscapeDataString(Resource)}"
        + $"&fromUtc={Uri.EscapeDataString(from.ToString("O"))}"
        + $"&toUtc={Uri.EscapeDataString(to.ToString("O"))}";

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

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName) => EnvironmentName = environmentName;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "Harborline.Api.LocalNodeHost.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new PhysicalFileProvider(AppContext.BaseDirectory);
    }
}
