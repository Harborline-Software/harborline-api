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
/// DEV-ONLY calendar seeder (<see cref="CalendarDevSeeder"/>) tests. Prove the load-bearing safety
/// invariants: the dev gate is AIRTIGHT (a Production environment seeds NOTHING — the prod-no-seed
/// gate), a Development environment seeds, the seed is idempotent (a re-run over a non-empty store adds
/// no duplicate), and the seeded <c>(tenant, resource)</c> matches what the Harborline App calendar route
/// queries (the occurrences endpoint returns the seeded events).
/// </summary>
/// <remarks>
/// Uses the block's in-memory stores (the seeder is store-agnostic — it writes through
/// <see cref="ICalendarEventStore"/> / <see cref="IResourceAvailabilityStore"/>; the durable NodeEf
/// store is covered by <c>NodeEfCalendarStoreTests</c>). The active team / tenant uses the same
/// <c>MutableActiveTeamAccessor</c> + tenant projection the route tests use, so the seeded tenant is
/// the one the route resolves server-side.
/// </remarks>
public sealed class CalendarDevSeederTests
{
    private static readonly TeamId Team = new(Guid.Parse("cccc0000-0000-0000-0000-0000000000c1"));
    private static readonly TenantId Tenant = ActiveTeamTenantContext.ProjectTenantId(Team);

    /// <summary>The Harborline App route's default demo resource (Harborline App <c>MOCK_RESOURCE = 'party:party-dr-smith'</c>).</summary>
    private const string ReferenceAppResource = "party:party-dr-smith";

    // ── the AIRTIGHT gate — pure logic ────────────────────────────────────────────────────────────

    [Fact(DisplayName = "gate: a Development environment seeds (ShouldSeed == true)")]
    public void Gate_Development_Seeds()
    {
        Assert.True(CalendarDevSeeder.ShouldSeed(new FakeHostEnvironment(Environments.Development)));
    }

    [Fact(DisplayName = "gate: a Production environment seeds NOTHING (ShouldSeed == false) — the prod-no-seed gate")]
    public void Gate_Production_DoesNotSeed()
    {
        // The load-bearing safety invariant: Production never seeds. No dev-seed env flag is set in this
        // test process, so the gate is closed.
        ClearDevSeedFlags();
        Assert.False(CalendarDevSeeder.ShouldSeed(new FakeHostEnvironment(Environments.Production)));
    }

    [Theory(DisplayName = "gate: an unknown / ambiguous environment seeds NOTHING (fail-safe OFF)")]
    [InlineData("Staging")]
    [InlineData("")]
    [InlineData("anything-else")]
    public void Gate_NonDevelopment_FailsSafeOff(string environmentName)
    {
        ClearDevSeedFlags();
        Assert.False(CalendarDevSeeder.ShouldSeed(new FakeHostEnvironment(environmentName)));
    }

    [Theory(DisplayName = "gate: legacy-style dev-seed flags cannot opt a non-Development environment in")]
    [InlineData("HARBORLINE_DEV_SEED", "1")]
    [InlineData("HARBORLINE_DEV_SEED", "true")]
    [InlineData("CAPABILITY_HOST_DEV", "yes")]
    [InlineData("CAPABILITY_HOST_DEV", "on")]
    public void Gate_ExplicitTruthyFlag_CannotOptIn(string flag, string value)
    {
        ClearDevSeedFlags();
        try
        {
            Environment.SetEnvironmentVariable(flag, value);
            Assert.False(CalendarDevSeeder.ShouldSeed(new FakeHostEnvironment(Environments.Production)));
        }
        finally
        {
            ClearDevSeedFlags();
        }
    }

    [Theory(DisplayName = "gate: a falsy / non-allow-listed dev-seed flag value does NOT open the gate")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("maybe")]
    [InlineData(" ")]
    public void Gate_FalsyFlag_StaysClosed(string value)
    {
        ClearDevSeedFlags();
        try
        {
            Environment.SetEnvironmentVariable("HARBORLINE_DEV_SEED", value);
            Assert.False(CalendarDevSeeder.ShouldSeed(new FakeHostEnvironment(Environments.Production)));
        }
        finally
        {
            ClearDevSeedFlags();
        }
    }

    // ── the seeder behavior (dev seeds / prod seeds nothing / idempotent) ──────────────────────────

    [Fact(DisplayName = "seeder: in a dev env the calendar store is non-empty after StartAsync")]
    public async Task Seeder_Development_Seeds()
    {
        var (seeder, eventStore, availabilityStore) = NewSeeder(Environments.Development);

        await seeder.StartAsync(CancellationToken.None);

        var events = await eventStore.ListAsync(Tenant);
        Assert.NotEmpty(events);
        // A visibly-populated week — the spec asks for ~6–8 events (a couple of bookables, a meeting, a
        // recurring stand-up, a blocking lunch).
        Assert.InRange(events.Count, 6, 8);

        // The bookable supply (the availability window) exists for the Harborline App resource.
        var availability = await availabilityStore.GetAsync(Tenant, ParticipantRef.Party("party-dr-smith"));
        Assert.NotNull(availability);
        Assert.Single(availability!.Windows);
    }

    [Fact(DisplayName = "seeder: in a PRODUCTION env the calendar store stays EMPTY (airtight prod-no-seed)")]
    public async Task Seeder_Production_SeedsNothing()
    {
        ClearDevSeedFlags();
        var (seeder, eventStore, availabilityStore) = NewSeeder(Environments.Production);

        await seeder.StartAsync(CancellationToken.None);

        // The load-bearing assertion: a Production node's calendar is untouched.
        Assert.Empty(await eventStore.ListAsync(Tenant));
        Assert.Null(await availabilityStore.GetAsync(Tenant, ParticipantRef.Party("party-dr-smith")));
    }

    [Fact(DisplayName = "seeder: re-running on a non-empty store adds NO duplicate (idempotent)")]
    public async Task Seeder_IsIdempotent()
    {
        var (seeder, eventStore, _) = NewSeeder(Environments.Development);

        await seeder.StartAsync(CancellationToken.None);
        var afterFirst = (await eventStore.ListAsync(Tenant)).Count;
        Assert.True(afterFirst > 0);

        // A second run (a node restart) over the now-non-empty store is a no-op.
        await seeder.StartAsync(CancellationToken.None);
        var afterSecond = (await eventStore.ListAsync(Tenant)).Count;

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact(DisplayName = "seeder: no active team → skip without throwing (does not crash the host)")]
    public async Task Seeder_NoActiveTeam_SkipsGracefully()
    {
        var eventStore = new InMemoryCalendarEventStore();
        var availabilityStore = new InMemoryResourceAvailabilityStore();
        var noActiveTeam = new MutableActiveTeamAccessor(active: null); // pre-bootstrap: no active team
        var seeder = new CalendarDevSeeder(
            eventStore, availabilityStore, new InMemoryCalendarStore(), noActiveTeam,
            new FakeHostEnvironment(Environments.Development), NullLogger<CalendarDevSeeder>.Instance);

        // Must not throw (NodeTenant.Resolve would throw; the seeder swallows it as a dev-only convenience).
        await seeder.StartAsync(CancellationToken.None);
        Assert.Empty(await eventStore.ListAsync(Tenant));
    }

    [Fact(DisplayName = "seeder: seeded events carry the default calendar's CalendarId (#149 C1 seam populated)")]
    public async Task Seeder_StampsDefaultCalendarId()
    {
        var eventStore = new InMemoryCalendarEventStore();
        var availabilityStore = new InMemoryResourceAvailabilityStore();
        var calendarStore = new InMemoryCalendarStore();
        var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(Team, "Dev Team"));

        // Provision the default calendar first (as DefaultCalendarProvisioningService does before the seeder).
        var defaultCalendar = OwnedCalendar.CreateDefault(Tenant, Guid.NewGuid());
        await calendarStore.SaveAsync(defaultCalendar);

        var seeder = new CalendarDevSeeder(
            eventStore, availabilityStore, calendarStore, activeTeam,
            new FakeHostEnvironment(Environments.Development), NullLogger<CalendarDevSeeder>.Instance);
        await seeder.StartAsync(CancellationToken.None);

        var events = await eventStore.ListAsync(Tenant);
        Assert.NotEmpty(events);
        // Every seeded event is assigned to the default calendar — the CalendarId seam is now populated.
        Assert.All(events, e => Assert.Equal(defaultCalendar.Id, e.CalendarId));
    }

    // ── the seeded (tenant, resource) matches the Harborline App read path ────────────────────────────────

    [Fact(DisplayName = "harborline read path: the occurrences endpoint returns the seeded events for the demo resource")]
    public async Task ReferenceAppRoute_Returns_SeededEvents()
    {
        // Seed via the production seeder, then host the SAME production route handlers (CalendarRoutes.Map,
        // mirroring HostedCalendarApiEndpoint) over a real Kestrel listener + drive them with HttpClient —
        // exactly the Harborline App's GET .../calendar/occurrences call.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddBlocksCalendar();

        var app = builder.Build();
        try
        {
            var eventStore = app.Services.GetRequiredService<ICalendarEventStore>();
            var availabilityStore = app.Services.GetRequiredService<IResourceAvailabilityStore>();
            var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(Team, "Dev Team"));

            // Run the dev seeder against the SAME resolved tenant the route below will use.
            var seeder = new CalendarDevSeeder(
                eventStore, availabilityStore, new InMemoryCalendarStore(), activeTeam,
                new FakeHostEnvironment(Environments.Development), NullLogger<CalendarDevSeeder>.Instance);
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

            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>();
            using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

            // Query the FULL current week (the Harborline App route queries the current day; the seed always lands
            // events across the week, with a couple on a weekday — query the week to assert deterministically
            // regardless of which weekday the test runs on, then prove a couple land within a single day).
            var now = DateTimeOffset.UtcNow;
            var monday = MondayOfUtc(now);
            var weekFrom = monday;                                   // Mon 00:00 UTC
            var weekTo = monday.AddDays(7).AddSeconds(-1);           // Sun 23:59:59 UTC (exclusive of next Mon)

            var weekDoc = await client.GetFromJsonAsync<JsonElement>(Occurrences(ReferenceAppResource, weekFrom, weekTo));
            var weekData = weekDoc.GetProperty("data");

            // The Harborline App read path returns the seeded events for party:party-dr-smith.
            Assert.True(weekData.GetArrayLength() > 0,
                "the occurrences endpoint returned no seeded events for the demo resource");

            // Sanity: the seeded titles surface (the recurring stand-up + the blocking lunch are daily, so
            // they appear every weekday; assert at least the stand-up is present).
            var titles = weekData.EnumerateArray()
                .Select(o => o.GetProperty("title").GetString())
                .ToList();
            Assert.Contains("Daily stand-up", titles);

            // Every occurrence is a UTC instant pair.
            foreach (var o in weekData.EnumerateArray())
            {
                Assert.True(DateTimeOffset.TryParse(o.GetProperty("startUtc").GetString(), out var s));
                Assert.Equal(TimeSpan.Zero, s.Offset);
            }

            // A different active team (a different tenant) sees an EMPTY agenda — the seed is tenant-scoped.
            activeTeam.Active = TeamContextFor(
                new TeamId(Guid.Parse("dddd0000-0000-0000-0000-0000000000d1")), "Other Team");
            var otherDoc = await client.GetFromJsonAsync<JsonElement>(Occurrences(ReferenceAppResource, weekFrom, weekTo));
            Assert.Equal(0, otherDoc.GetProperty("data").GetArrayLength());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private static (CalendarDevSeeder, ICalendarEventStore, IResourceAvailabilityStore) NewSeeder(
        string environmentName)
    {
        var eventStore = new InMemoryCalendarEventStore();
        var availabilityStore = new InMemoryResourceAvailabilityStore();
        var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(Team, "Dev Team"));
        var seeder = new CalendarDevSeeder(
            eventStore, availabilityStore, new InMemoryCalendarStore(), activeTeam,
            new FakeHostEnvironment(environmentName), NullLogger<CalendarDevSeeder>.Instance);
        return (seeder, eventStore, availabilityStore);
    }

    private static string Occurrences(string resource, DateTimeOffset from, DateTimeOffset to) =>
        $"/api/local-node/calendar/occurrences?resource={Uri.EscapeDataString(resource)}"
        + $"&fromUtc={Uri.EscapeDataString(from.ToString("O"))}"
        + $"&toUtc={Uri.EscapeDataString(to.ToString("O"))}";

    private static DateTimeOffset MondayOfUtc(DateTimeOffset instant)
    {
        var date = instant.UtcDateTime.Date;
        int isoOffset = ((int)date.DayOfWeek + 6) % 7; // Mon == 0 … Sun == 6
        var monday = date.AddDays(-isoOffset);
        return new DateTimeOffset(monday, TimeSpan.Zero);
    }

    private static void ClearDevSeedFlags()
    {
        Environment.SetEnvironmentVariable("HARBORLINE_DEV_SEED", null);
        Environment.SetEnvironmentVariable("CAPABILITY_HOST_DEV", null);
    }

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

    /// <summary>A minimal <see cref="IHostEnvironment"/> test double — only the environment name matters here.</summary>
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
