using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost;

/// <summary>
/// DEV-ONLY calendar seeder (app-calendar demo data). On a development node it populates the
/// durable <c>NodeEf</c> calendar store with a visibly-populated current week — a Mon–Fri 9–5
/// availability window plus a mix of appointments / a meeting / a recurring stand-up / a blocking
/// lunch — for the <b>same</b> <c>(tenant, resource)</c> the Harborline App calendar route queries, so the
/// real Tauri Harborline App path (not just the browser-preview mock) shows a populated calendar.
/// </summary>
/// <remarks>
/// <para>
/// <b>The dev gate is AIRTIGHT — fail-safe OFF.</b> The seed runs ONLY when the environment is
/// affirmatively a development one: <see cref="HostEnvironmentEnvExtensions.IsDevelopment"/> is true
/// (<c>DOTNET_ENVIRONMENT</c> / <c>ASPNETCORE_ENVIRONMENT</c> == <c>Development</c>).
/// Otherwise (the default, and Production), the seeder does <b>nothing</b> — it
/// never runs in Production, so a real tenant's calendar is never polluted. The gate is a positive
/// allow-list: ambiguity / absence ⇒ no seed (it is impossible to seed by omission). A Production
/// host does not report <c>IsDevelopment()</c>, so the production path stays closed.
/// </para>
/// <para>
/// <b>Idempotent.</b> The seed runs only when the resolved tenant's calendar event store is EMPTY
/// (<c>ListAsync(tenant).Count == 0</c>). A node restart (or a re-run) over a non-empty store is a
/// no-op — no duplicate events accrue. (The availability record is keyed <c>(tenant, resource)</c>
/// and re-saving replaces it, so even the availability write is naturally upsert-safe.)
/// </para>
/// <para>
/// <b>Tenant + resource match the read path.</b> The tenant is the active-team-derived tenant
/// (<see cref="NodeTenant.Resolve"/>) — exactly what <c>CalendarRoutes</c> resolves server-side — and
/// the resource is <c>party:party-dr-smith</c>, the Harborline App route's default demo resource
/// (<c>MOCK_RESOURCE</c>). So the seeded events surface through the Harborline App's
/// <c>/api/local-node/calendar/occurrences</c> + <c>/free-busy</c> reads.
/// </para>
/// <para>
/// <b>Ordering.</b> Registered AFTER the SQLCipher encryption guard (which migrates the calendar
/// context) and AFTER <see cref="MultiTeamBootstrapHostedService"/> (which seeds the active team).
/// <see cref="IHostedService.StartAsync"/> runs in registration order, so by the time this fires the
/// calendar schema exists and an active team is resolvable. This only WRITES seed rows; it does not
/// touch the read endpoints or the clip/auth path.
/// </para>
/// </remarks>
public sealed class CalendarDevSeeder : IHostedService
{
    /// <summary>The Harborline App calendar route's default demo resource (mirrors the Harborline App <c>MOCK_RESOURCE</c>).</summary>
    private static readonly ParticipantRef DemoResource = ParticipantRef.Party("party-dr-smith");

    /// <summary>A stable IANA timezone for the demo (real DST-bearing zone, US Pacific).</summary>
    private const string DemoTimezone = "America/Los_Angeles";

    /// <summary>The deterministic dev seed actor (an opaque demo-data author id — not a real principal).</summary>
    private static readonly Guid SeedActor = new("5eed0000-0000-0000-0000-00000000ca1e");

    /// <summary>Env flags that explicitly opt a non-Development environment into the dev seed.</summary>
    private readonly ICalendarEventStore _eventStore;
    private readonly IResourceAvailabilityStore _availabilityStore;
    private readonly ICalendarStore _calendarStore;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<CalendarDevSeeder> _logger;

    public CalendarDevSeeder(
        ICalendarEventStore eventStore,
        IResourceAvailabilityStore availabilityStore,
        ICalendarStore calendarStore,
        IActiveTeamAccessor activeTeam,
        IHostEnvironment environment,
        ILogger<CalendarDevSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(availabilityStore);
        ArgumentNullException.ThrowIfNull(calendarStore);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        _eventStore = eventStore;
        _availabilityStore = availabilityStore;
        _calendarStore = calendarStore;
        _activeTeam = activeTeam;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// The airtight dev gate: true ONLY when the environment is affirmatively a development one
    /// (<see cref="HostEnvironmentEnvExtensions.IsDevelopment"/>) or an explicit dev-seed env flag is
    /// set to a truthy value. Fail-safe: absent/ambiguous ⇒ false (never seed). Production is never a
    /// development environment and (by convention) does not set the dev flags.
    /// </summary>
    public static bool ShouldSeed(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsDevelopment();
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // ── The airtight gate — fail-safe OFF. ──────────────────────────────────────────────────────
        if (!ShouldSeed(_environment))
        {
            // No log noise on the production path beyond a single debug line — the seed simply never runs.
            _logger.LogDebug(
                "CalendarDevSeeder: environment '{Environment}' is not a development environment — "
                + "skipping the dev calendar seed (production-safe).",
                _environment.EnvironmentName);
            return;
        }

        TenantId tenantId;
        try
        {
            tenantId = NodeTenant.Resolve(_activeTeam);
        }
        catch (InvalidOperationException ex)
        {
            // No active team yet (the multi-team bootstrap must precede this in registration order). Do
            // NOT crash the host over a dev-only convenience — skip and log.
            _logger.LogWarning(
                ex,
                "CalendarDevSeeder: no active team resolved — skipping the dev calendar seed. (Register "
                + "this hosted service AFTER MultiTeamBootstrapHostedService so an active team exists.)");
            return;
        }

        // ── Idempotency — seed ONLY when the tenant's calendar is empty. ─────────────────────────────
        var existing = await _eventStore.ListAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            _logger.LogDebug(
                "CalendarDevSeeder: tenant {TenantId} already has {Count} calendar event(s) — the dev "
                + "seed is a no-op (idempotent, no duplicates).",
                tenantId, existing.Count);
            return;
        }

        await SeedAsync(tenantId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "CalendarDevSeeder: seeded the dev calendar for tenant {TenantId} / resource {Resource} "
            + "(a Mon–Fri 9–5 availability + a populated current week).",
            tenantId, DemoResource);
    }

    /// <summary>
    /// Write the demo availability + the current week's events for <paramref name="tenantId"/> on the
    /// demo resource. UTC-correct: the dates are this week (relative to <see cref="DateTimeOffset.UtcNow"/>);
    /// the wall-clock times are interpreted in <see cref="DemoTimezone"/> by the block's DST-aware
    /// expansion (the Harborline App route returns UTC instants).
    /// </summary>
    private async Task SeedAsync(TenantId tenantId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var monday = MondayOf(today);

        // ── A Mon–Fri 09:00–17:00 bookable availability window (the bookable supply for free/busy). ──
        var availability = ResourceAvailability.Create(tenantId, DemoResource, DemoTimezone)
            .AddWindow(AvailabilityWindow.Create(
                anchorDate: monday,
                startTime: new TimeOnly(9, 0),
                endTime: new TimeOnly(17, 0),
                rrule: "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"));
        await _availabilityStore.SaveAsync(availability, ct).ConfigureAwait(false);

        // The tenant's default calendar (provisioned structurally by DefaultCalendarProvisioningService,
        // registered BEFORE this seeder) — so the dev events carry a non-null CalendarId (the C1
        // productization seam, #149). Defensive fallback to any calendar / null if none is found.
        var calendars = await _calendarStore.ListAsync(tenantId, ct).ConfigureAwait(false);
        var defaultCalendarId = (calendars.FirstOrDefault(c => c.IsDefault) ?? calendars.FirstOrDefault())?.Id;

        var tuesday = monday.AddDays(1);
        var wednesday = monday.AddDays(2);
        var thursday = monday.AddDays(3);
        var friday = monday.AddDays(4);

        // ── ~7 events over the current week — a mix that shows off the model. A couple land on "today"
        //    so the Harborline App's default day-agenda view (the current UTC day) is non-empty. ─────────────
        var events = new List<CalendarEvent>
        {
            // 1) A daily-recurring stand-up (RRULE) — weekday mornings, a bookable 15-minute slot.
            TimedEvent(tenantId, "Daily stand-up", monday, new TimeOnly(9, 0), new TimeOnly(9, 15),
                Occupancy.Bookable, rrule: "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"),

            // 2) A daily-recurring BLOCKING lunch (occupancy classification — busy but not bookable).
            TimedEvent(tenantId, "Lunch", monday, new TimeOnly(12, 0), new TimeOnly(13, 0),
                Occupancy.Blocking, rrule: "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR"),

            // 3) A bookable appointment on TODAY's weekday — so the default agenda shows a real booking.
            TimedEvent(tenantId, "New-patient consult", AnchorToday(today, monday, friday),
                new TimeOnly(10, 0), new TimeOnly(10, 45), Occupancy.Bookable),

            // 4) A Meeting on TODAY's weekday (Bookable occupancy; a "meeting" by title/role).
            TimedEvent(tenantId, "Care-team meeting", AnchorToday(today, monday, friday),
                new TimeOnly(14, 0), new TimeOnly(15, 0), Occupancy.Bookable),

            // 5–7) A spread of bookable appointments across the rest of the week.
            TimedEvent(tenantId, "Follow-up — A. Rivera", tuesday,
                new TimeOnly(11, 0), new TimeOnly(11, 30), Occupancy.Bookable),
            TimedEvent(tenantId, "Physical — J. Okafor", wednesday,
                new TimeOnly(15, 30), new TimeOnly(16, 15), Occupancy.Bookable),
            TimedEvent(tenantId, "Telehealth — M. Lindqvist", thursday,
                new TimeOnly(13, 30), new TimeOnly(14, 0), Occupancy.Bookable),
        };

        foreach (var ev in events)
        {
            // Assign each seeded event to the tenant's default calendar (populate the CalendarId seam,
            // #149 C1). When no calendar exists yet (defensive), the event stays standalone (null id).
            if (defaultCalendarId is { } cid)
            {
                ev.SetCalendarId(cid, SeedActor);
            }
            await _eventStore.SaveAsync(ev, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Build a timed event on <see cref="DemoResource"/>: <c>SetResource</c> makes the resource both
    /// the headline <c>ResourceRef</c> (free/busy reads it) AND a <c>Resource</c> participation (the
    /// agenda's <c>EventsFor</c> matches on participation) — the exact pairing the route tests use.
    /// </summary>
    private static CalendarEvent TimedEvent(
        TenantId tenantId,
        string title,
        DateOnly date,
        TimeOnly start,
        TimeOnly end,
        Occupancy occupancy,
        string? rrule = null)
    {
        var ev = CalendarEvent.Create(
            tenantId,
            title,
            start: date,
            end: date,
            createdBy: SeedActor,
            rrule: rrule,
            timezone: DemoTimezone,
            startTime: start,
            endTime: end,
            occupancy: occupancy);
        ev.SetResource(DemoResource, SeedActor);
        return ev;
    }

    /// <summary>The Monday of <paramref name="date"/>'s ISO week (clamps a weekend to its own week's Monday).</summary>
    private static DateOnly MondayOf(DateOnly date)
    {
        // DayOfWeek: Sunday == 0 … Saturday == 6. Map to an ISO Monday-based offset (Mon == 0 … Sun == 6).
        int isoOffset = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-isoOffset);
    }

    /// <summary>
    /// The weekday to anchor a "today" event on: today itself when today is a weekday, otherwise the
    /// nearest in-week weekday (Monday on a weekend) — so the demo always lands a couple of events on a
    /// day the agenda is likely viewing without scheduling on a weekend the availability excludes.
    /// </summary>
    private static DateOnly AnchorToday(DateOnly today, DateOnly monday, DateOnly friday)
    {
        // today is always within [monday, monday+6] (monday is MondayOf(today)); a weekday returns today,
        // a weekend (Sat/Sun, past friday) falls back to this week's Monday so the demo lands on a day the
        // Mon–Fri availability actually covers.
        return today <= friday ? today : monday;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
