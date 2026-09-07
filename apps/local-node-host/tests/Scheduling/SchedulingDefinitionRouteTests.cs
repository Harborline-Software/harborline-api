using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Calendar;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Data.Scheduling;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.TestDoubles;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Scheduling;

/// <summary>HTTP-contract coverage for the production scheduling draft route map.</summary>
public sealed class SchedulingDefinitionRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-00000000aa01"));
    private static readonly TeamId TeamB = new(Guid.Parse("bbbb0000-0000-0000-0000-00000000bb02"));
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"scheduling-routes-{Guid.NewGuid():N}.db");
    private readonly string _calendarPath = Path.Combine(
        Path.GetTempPath(), $"scheduling-calendars-{Guid.NewGuid():N}.db");
    private readonly string _peoplePath = Path.Combine(Path.GetTempPath(), $"scheduling-subjects-{Guid.NewGuid():N}.db");
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private TestDbFactory _factory = null!;
    private IDbContextFactory<NodeLocalCalendarDbContext> _calendarFactory = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;
    private MutablePrincipal _principal = null!;
    private NodeEfPartyRepository _parties = null!;
    private readonly MutableTimeProvider _clock = new(DateTimeOffset.Parse("2026-07-13T12:00:00Z"));

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<NodeLocalSchedulingDbContext>()
            .UseSqlite(SqliteTestDatabase.ConnectionString(_path), sqlite =>
                sqlite.MigrationsHistoryTable(NodeLocalSchedulingDbContext.MigrationsHistoryTableName))
            .Options;
        _factory = new TestDbFactory(options);
        await using (var db = await _factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAuthorizationContext>(_ => _principal);
        // Ticket 205 slice 4: the route guards resolve at the gate. It follows the SAME mutable holding
        // set this host already flips, so a test that narrows the caller's permissions narrows the decision.
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.Following(
            permission => _principal.HasPermission(permission)));
        builder.Services.AddSingleton<IHarborlineEntityModule, PeopleEntityModule>();
        builder.Services.AddDbContextFactory<NodeLocalCalendarDbContext>(db =>
            db.UseSqlite($"Data Source={_calendarPath};Pooling=False", sqlite =>
                sqlite.MigrationsHistoryTable(NodeLocalCalendarDbContext.MigrationsHistoryTableName)));
        builder.Services.AddNodeCalendar();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(db =>
            db.UseSqlite($"Data Source={_peoplePath};Pooling=False"));
        _app = builder.Build();
        _calendarFactory = _app.Services.GetRequiredService<IDbContextFactory<NodeLocalCalendarDbContext>>();
        await using (var db = await _calendarFactory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var peopleFactory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var db = await peopleFactory.CreateDbContextAsync())
            await db.Database.EnsureCreatedAsync();
        _activeTeam = new MutableActiveTeamAccessor(Context(TeamA));
        _principal = new MutablePrincipal("server-actor");
        var store = new NodeSchedulingDraftStore(_factory, TimeProvider.System);
        _parties = new NodeEfPartyRepository(peopleFactory, TimeProvider.System);
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        SchedulingDefinitionRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            store, new SchedulingDraftValidator(), _parties, _activeTeam, _principal,
            _app.Services.GetRequiredService<IBookingService>(),
            _app.Services.GetRequiredService<ICalendarEventStore>(),
            _app.Services.GetRequiredService<ICalendarStore>(),
            _app.Services.GetRequiredService<IResourceAvailabilityStore>(),
            _app.Services.GetRequiredService<IFreeBusyService>(), _clock);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteTestDatabase.Delete(_path, _calendarPath, _peoplePath);
    }

    [Fact]
    public async Task Read_routes_require_scheduling_read_and_return_stable_denial()
    {
        var response = await _client.GetAsync(SchedulingDefinitionRoutes.RouteBase);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        Assert.Equal(Permission.SchedulingRead, body.GetProperty("permission").GetString());
    }

    [Fact]
    public async Task Author_routes_require_scheduling_author_and_return_stable_denial()
    {
        _principal.Grant(Permission.SchedulingRead);
        var response = await SaveAsync("protocol-a", 0, Definition("Protocol A"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        Assert.Equal(Permission.SchedulingAuthor, body.GetProperty("permission").GetString());
        Assert.Empty(await DraftsAsync());
    }

    [Fact]
    public async Task Versions_lists_every_retained_revision_newest_first_and_404s_on_unknown_ids()
    {
        _principal.Grant(Permission.SchedulingAuthor, Permission.SchedulingRead);
        await SaveAsync("protocol-a", 0, Definition("Protocol A"));
        await SaveAsync("protocol-a", 1, Definition("Protocol A v2"));
        await SaveAsync("protocol-a", 2, Definition("Protocol A v3"));

        var versions = await _client.GetFromJsonAsync<JsonElement>(
            $"{SchedulingDefinitionRoutes.RouteBase}/protocol-a/versions");

        Assert.Equal(3, versions.GetArrayLength());
        Assert.Equal(3, versions[0].GetProperty("revision").GetInt32());
        Assert.Equal(1, versions[2].GetProperty("revision").GetInt32());
        Assert.Equal("Protocol A v3", versions[0].GetProperty("title").GetString());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _client.GetAsync($"{SchedulingDefinitionRoutes.RouteBase}/unknown/versions")).StatusCode);
    }

    [Fact]
    public async Task One_revision_is_an_exact_read_and_absent_revisions_404_by_stable_code()
    {
        _principal.Grant(Permission.SchedulingAuthor, Permission.SchedulingRead);
        await SaveAsync("protocol-a", 0, Definition("Protocol A"));
        await SaveAsync("protocol-a", 1, Definition("Protocol A v2"));

        var one = await _client.GetFromJsonAsync<JsonElement>(
            $"{SchedulingDefinitionRoutes.RouteBase}/protocol-a/versions/1");
        Assert.Equal("Protocol A", one.GetProperty("definition").GetProperty("title").GetString());

        var missing = await _client.GetAsync($"{SchedulingDefinitionRoutes.RouteBase}/protocol-a/versions/9");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var body = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("scheduling.draft.revision_not_found", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Restore_appends_the_old_body_as_the_new_head_and_history_never_shrinks()
    {
        _principal.Grant(Permission.SchedulingAuthor, Permission.SchedulingRead);
        await SaveAsync("protocol-a", 0, Definition("Protocol A"));
        await SaveAsync("protocol-a", 1, Definition("Protocol A v2"));

        var restore = await _client.PostAsJsonAsync(
            $"{SchedulingDefinitionRoutes.RouteBase}/protocol-a/restore", new { revision = 1 });

        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var result = await restore.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, result.GetProperty("revision").GetInt32());
        Assert.Equal(1, result.GetProperty("restoredFrom").GetInt32());

        // The restored body IS the new head (no Draft/Published split in this store)...
        var head = await _client.GetFromJsonAsync<JsonElement>(
            $"{SchedulingDefinitionRoutes.RouteBase}/protocol-a");
        Assert.Equal(3, head.GetProperty("revision").GetInt32());
        Assert.Equal("Protocol A", head.GetProperty("definition").GetProperty("title").GetString());

        // ...and history is append-only: all three revisions remain readable.
        var versions = await _client.GetFromJsonAsync<JsonElement>(
            $"{SchedulingDefinitionRoutes.RouteBase}/protocol-a/versions");
        Assert.Equal(3, versions.GetArrayLength());
    }

    [Fact]
    public async Task Restore_requires_author_permission_and_404s_on_an_absent_source_revision()
    {
        _principal.Grant(Permission.SchedulingRead);
        var denied = await _client.PostAsJsonAsync(
            $"{SchedulingDefinitionRoutes.RouteBase}/protocol-a/restore", new { revision = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        _principal.Grant(Permission.SchedulingAuthor);
        await SaveAsync("protocol-a", 0, Definition("Protocol A"));
        var missing = await _client.PostAsJsonAsync(
            $"{SchedulingDefinitionRoutes.RouteBase}/protocol-a/restore", new { revision = 7 });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Author_can_save_and_reader_receives_the_document_and_list_summary_shape()
    {
        _principal.Grant(Permission.SchedulingAuthor, Permission.SchedulingRead);
        var save = await SaveAsync("protocol-a", 0, Definition("Protocol A"));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var loaded = await _client.GetFromJsonAsync<JsonElement>(
            $"{SchedulingDefinitionRoutes.RouteBase}/protocol-a");
        Assert.Equal("protocol-a", loaded.GetProperty("id").GetString());
        Assert.Equal(1, loaded.GetProperty("revision").GetInt32());
        Assert.Equal("Protocol A", loaded.GetProperty("definition").GetProperty("title").GetString());

        var list = await _client.GetFromJsonAsync<JsonElement>(SchedulingDefinitionRoutes.RouteBase);
        var entry = Assert.Single(list.EnumerateArray());
        Assert.Equal("protocol-a", entry.GetProperty("id").GetString());
        Assert.Equal(1, entry.GetProperty("revision").GetInt32());
        Assert.Equal("Protocol A", entry.GetProperty("title").GetString());
        Assert.Equal("server-actor", entry.GetProperty("updatedBy").GetString());
        Assert.Equal(JsonValueKind.String, entry.GetProperty("updatedAt").ValueKind);
    }

    [Fact]
    public async Task Permission_narrowing_and_revocation_deny_the_next_mutation()
    {
        _principal.Grant(Permission.SchedulingAuthor);
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync("narrowed", 0, Definition("Allowed"))).StatusCode);

        _principal.Clear();
        var narrowed = await SaveAsync("narrowed", 1, Definition("Denied"));
        Assert.Equal(HttpStatusCode.Forbidden, narrowed.StatusCode);
        var body = await narrowed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        Assert.Equal(Permission.SchedulingAuthor, body.GetProperty("permission").GetString());

        var revoked = await SaveAsync("revoked", 0, Definition("Denied"));
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Single(await DraftsAsync());
    }

    [Fact]
    public async Task Tenant_and_actor_are_server_derived_and_observable_in_audit()
    {
        _principal.Grant(Permission.SchedulingAuthor, Permission.SchedulingRead);
        _principal.UserId = "authenticated-operator";
        await SaveAsync("shared-id", 0, Definition("Team A"));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var audit = Assert.Single(await db.DraftAudit.ToListAsync());
            Assert.Equal(NodeTenant.Resolve(_activeTeam).Value, audit.TenantId);
            Assert.Equal("authenticated-operator", audit.ActorId);
        }

        _activeTeam.Active = Context(TeamB);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"{SchedulingDefinitionRoutes.RouteBase}/shared-id")).StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>(SchedulingDefinitionRoutes.RouteBase))
            .EnumerateArray());
    }

    [Fact]
    public async Task Missing_definition_returns_stable_404_code()
    {
        _principal.Grant(Permission.SchedulingRead);
        var response = await _client.GetAsync($"{SchedulingDefinitionRoutes.RouteBase}/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("scheduling.draft.not_found",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Every_404_carries_a_machine_code_and_no_English()
    {
        // Ticket 094 acceptance 4: scheduling is the fifth family on the envelope. It has always
        // answered {code}, but no test asserted the retired 'error' member is absent, so a prose
        // body could have been reintroduced without a single failure.
        _principal.Grant(Permission.SchedulingAuthor, Permission.SchedulingRead);
        await SaveAsync("protocol-a", 0, Definition("Protocol A"));

        var unknownKey = await ReadBodyAsync($"{SchedulingDefinitionRoutes.RouteBase}/missing");
        var unknownVersion = await ReadBodyAsync(
            $"{SchedulingDefinitionRoutes.RouteBase}/protocol-a/versions/9");

        Assert.Equal("scheduling.draft.not_found", unknownKey.GetProperty("code").GetString());
        Assert.Equal("scheduling.draft.revision_not_found", unknownVersion.GetProperty("code").GetString());
        foreach (var body in new[] { unknownKey, unknownVersion })
        {
            Assert.False(body.TryGetProperty("error", out _),
                "the retired 'error' field is still on the wire — both fields cannot coexist or clients will decode whichever they were written against");
            Assert.Single(body.EnumerateObject());
        }
    }

    /// <summary>Reads a refusal body as JSON.</summary>
    /// <param name="route">The route to call.</param>
    private async Task<JsonElement> ReadBodyAsync(string route)
    {
        var response = await _client.GetAsync(route);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Stale_revision_returns_stable_409_and_does_not_mutate()
    {
        _principal.Grant(Permission.SchedulingAuthor);
        await SaveAsync("protocol-a", 0, Definition("First"));
        var response = await SaveAsync("protocol-a", 0, Definition("Stale"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("scheduling.draft.revision_conflict", body.GetProperty("code").GetString());
        Assert.Equal(1, body.GetProperty("currentRevision").GetInt32());
        Assert.Single(await DraftsAsync());
        Assert.Single(await AuditsAsync());
    }

    [Fact]
    public async Task Invalid_definition_shape_returns_stable_400_and_does_not_mutate()
    {
        _principal.Grant(Permission.SchedulingAuthor);
        var response = await SaveAsync("protocol-a", 0, Json("[]"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("scheduling.draft.object_required",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Empty(await DraftsAsync());
        Assert.Empty(await AuditsAsync());
    }

    [Fact]
    public async Task Validate_reports_issues_without_writing_draft_or_audit()
    {
        _principal.Grant(Permission.SchedulingAuthor);
        var response = await _client.PostAsJsonAsync(
            $"{SchedulingDefinitionRoutes.RouteBase}/validate", new { definition = new { } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("valid").GetBoolean());
        Assert.NotEmpty(body.GetProperty("issues").EnumerateArray());
        Assert.Empty(await DraftsAsync());
        Assert.Empty(await AuditsAsync());
    }

    [Fact]
    public async Task Validate_rejects_negative_or_oversized_appointment_policy_durations()
    {
        _principal.Grant(Permission.SchedulingAuthor);
        var response = await _client.PostAsJsonAsync(
            $"{SchedulingDefinitionRoutes.RouteBase}/validate",
            new { definition = Definition("Invalid policy", bufferBeforeMinutes: -1) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("valid").GetBoolean());
        var issue = Assert.Single(body.GetProperty("issues").EnumerateArray(), item =>
            item.GetProperty("path").GetString() == "bufferBeforeMinutes");
        Assert.Equal("scheduling.validation.policy_duration_invalid", issue.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Subject_picker_requires_scheduling_operate()
    {
        _principal.Grant(Permission.SchedulingRead);
        var response = await _client.GetAsync(SchedulingDefinitionRoutes.SubjectRoute);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        Assert.Equal(Permission.SchedulingOperate, body.GetProperty("permission").GetString());
    }

    [Fact]
    public async Task Subject_picker_with_operate_returns_minimum_wire_shape()
    {
        _principal.Grant(Permission.SchedulingOperate);
        var response = await _client.GetAsync(SchedulingDefinitionRoutes.SubjectRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.GetProperty("subjects").ValueKind);
        Assert.Empty(body.GetProperty("subjects").EnumerateArray());
        Assert.Single(body.EnumerateObject());
    }

    [Fact]
    public async Task Appointment_booking_requires_scheduling_operate()
    {
        var response = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.AppointmentRoute,
            new { subjectId = "subject-1", resource = "party:resource-1", title = "Visit",
                startUtc = "2026-07-13T13:00:00Z", endUtc = "2026-07-13T13:30:00Z" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Appointment_booking_rejects_an_invalid_resource_without_writing()
    {
        _principal.Grant(Permission.SchedulingOperate);
        var response = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.AppointmentRoute,
            new { subjectId = "subject-1", resource = "unknown:resource-1", title = "Visit",
                startUtc = "2026-07-13T13:00:00Z", endUtc = "2026-07-13T13:30:00Z" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("scheduling.appointment.resource_invalid", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task All_day_event_persists_explicit_civil_dates_across_dst_boundary()
    {
        _principal.Grant(Permission.SchedulingOperate);
        var response = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.EventRoute, new
        {
            resource = "party:resource-1",
            title = "DST weekend",
            timezone = "America/New_York",
            allDay = true,
            startDate = "2026-03-08",
            endDate = "2026-03-09",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = Assert.Single(await _app.Services.GetRequiredService<ICalendarEventStore>()
            .ListAsync(NodeTenant.Resolve(_activeTeam)));
        Assert.True(saved.AllDay);
        Assert.Equal(new DateOnly(2026, 3, 8), saved.Start);
        Assert.Equal(new DateOnly(2026, 3, 9), saved.End);
        Assert.Equal("UTC", saved.Timezone);
        Assert.Equal(TimeOnly.MinValue, saved.StartTime);
        Assert.Equal(TimeOnly.MinValue, saved.EndTime);
    }

    [Fact]
    public async Task All_day_event_rejects_time_bearing_fields_without_writing()
    {
        _principal.Grant(Permission.SchedulingOperate);
        var response = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.EventRoute, new
        {
            resource = "party:resource-1",
            title = "Invalid all-day event",
            allDay = true,
            startDate = "2026-03-08",
            endDate = "2026-03-09",
            startUtc = "2026-03-08T05:00:00Z",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("scheduling.event.all_day_time_fields_forbidden",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Empty(await _app.Services.GetRequiredService<ICalendarEventStore>()
            .ListAsync(NodeTenant.Resolve(_activeTeam)));
    }

    [Fact]
    public async Task All_day_event_rejects_timestamp_shaped_civil_date_without_writing()
    {
        _principal.Grant(Permission.SchedulingOperate);
        var response = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.EventRoute, new
        {
            resource = "party:resource-1",
            title = "Invalid date",
            allDay = true,
            startDate = "2026-03-08T00:00:00Z",
            endDate = "2026-03-09",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("scheduling.event.all_day_date_invalid",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Empty(await _app.Services.GetRequiredService<ICalendarEventStore>()
            .ListAsync(NodeTenant.Resolve(_activeTeam)));
    }

    [Fact]
    public async Task Timed_event_remains_explicitly_not_all_day()
    {
        _principal.Grant(Permission.SchedulingOperate);
        var response = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.EventRoute, new
        {
            resource = "party:resource-1",
            title = "Timed event",
            timezone = "America/New_York",
            startUtc = "2026-03-08T13:00:00Z",
            endUtc = "2026-03-08T14:00:00Z",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = Assert.Single(await _app.Services.GetRequiredService<ICalendarEventStore>()
            .ListAsync(NodeTenant.Resolve(_activeTeam)));
        Assert.False(saved.AllDay);
        Assert.Equal(new TimeOnly(9, 0), saved.StartTime);
        Assert.Equal(new TimeOnly(10, 0), saved.EndTime);
    }

    [Theory]
    [InlineData(CalendarKind.Personal)]
    [InlineData(CalendarKind.Team)]
    [InlineData(CalendarKind.Resource)]
    [Trait("PlanCard", "2083")]
    public async Task Event_targets_every_owned_calendar_kind_and_derives_resource_from_the_store(
        CalendarKind kind)
    {
        _principal.Grant(Permission.SchedulingOperate);
        var tenant = NodeTenant.Resolve(_activeTeam);
        var expectedResource = kind == CalendarKind.Resource
            ? ParticipantRef.Party("resource-1")
            : null;
        var calendar = OwnedCalendar.Create(
            tenant, $"{kind} calendar", kind, Guid.NewGuid(), resourceRef: expectedResource);
        await _app.Services.GetRequiredService<ICalendarStore>().SaveAsync(calendar);

        var response = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.EventRoute, new
        {
            calendarId = calendar.Id.Value,
            // A client-supplied resource must never override the stored calendar's resource lens.
            resource = "party:untrusted-client-value",
            title = "Calendar-owned event",
            timezone = "America/New_York",
            startUtc = "2026-03-08T13:00:00Z",
            endUtc = "2026-03-08T14:00:00Z",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = Assert.Single(await _app.Services.GetRequiredService<ICalendarEventStore>()
            .ListAsync(tenant));
        Assert.Equal(calendar.Id, saved.CalendarId);
        Assert.Equal(expectedResource, saved.ResourceRef);
    }

    [Fact]
    [Trait("PlanCard", "2252")]
    public async Task Event_refuses_a_calendar_owned_by_another_tenant_without_writing()
    {
        _principal.Grant(Permission.SchedulingOperate);
        var calendarStore = Assert.IsType<NodeEfCalendarStore>(
            _app.Services.GetRequiredService<ICalendarStore>());
        var eventStore = Assert.IsType<NodeEfCalendarEventStore>(
            _app.Services.GetRequiredService<ICalendarEventStore>());

        _activeTeam.Active = Context(TeamB);
        var tenantB = NodeTenant.Resolve(_activeTeam);
        var calendar = OwnedCalendar.Create(
            tenantB, "Team B calendar", CalendarKind.Team, Guid.NewGuid());
        await calendarStore.SaveAsync(calendar);

        _activeTeam.Active = Context(TeamA);
        var tenantA = NodeTenant.Resolve(_activeTeam);
        Assert.NotEqual(tenantB, tenantA);

        var missingCalendarId = Guid.NewGuid();
        await using (var freshContext = await _calendarFactory.CreateDbContextAsync())
        {
            var seeded = Assert.Single(await freshContext.Calendars.AsNoTracking()
                .Where(row => row.Id == calendar.Id.Value.ToString())
                .ToListAsync());
            Assert.Equal(tenantB.Value, seeded.TenantId);
            Assert.Empty(await freshContext.Calendars.AsNoTracking()
                .Where(row => row.TenantId == tenantA.Value)
                .ToListAsync());
            Assert.DoesNotContain(await freshContext.Calendars.AsNoTracking().ToListAsync(),
                row => row.Id == missingCalendarId.ToString());
            Assert.Empty(await freshContext.CalendarEvents.AsNoTracking().ToListAsync());
        }

        using var foreignResponse = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.EventRoute, new
        {
            calendarId = calendar.Id.Value,
            title = "Cross-tenant event",
            timezone = "America/New_York",
            startUtc = "2026-03-08T13:00:00Z",
            endUtc = "2026-03-08T14:00:00Z",
        });

        using var missingResponse = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.EventRoute, new
        {
            calendarId = missingCalendarId,
            title = "Cross-tenant event",
            timezone = "America/New_York",
            startUtc = "2026-03-08T13:00:00Z",
            endUtc = "2026-03-08T14:00:00Z",
        });

        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
        Assert.Equal(foreignResponse.StatusCode, missingResponse.StatusCode);
        Assert.Equal(foreignResponse.Content.Headers.ContentType, missingResponse.Content.Headers.ContentType);
        var foreignBody = await foreignResponse.Content.ReadAsStringAsync();
        Assert.Equal(foreignBody, await missingResponse.Content.ReadAsStringAsync());
        var refusal = JsonDocument.Parse(foreignBody).RootElement;
        var property = Assert.Single(refusal.EnumerateObject());
        Assert.Equal("code", property.Name);
        Assert.Equal("scheduling.event.calendar_not_found", property.Value.GetString());

        Assert.Empty(await eventStore.ListAsync(tenantA));
        Assert.Empty(await eventStore.ListAsync(tenantB));
        await using (var freshContext = await _calendarFactory.CreateDbContextAsync())
        {
            Assert.Empty(await freshContext.CalendarEvents.AsNoTracking()
                .Where(row => row.TenantId == tenantA.Value || row.TenantId == tenantB.Value)
                .ToListAsync());
            Assert.Single(await freshContext.Calendars.AsNoTracking()
                .Where(row => row.TenantId == tenantB.Value && row.Id == calendar.Id.Value.ToString())
                .ToListAsync());
            Assert.Empty(await freshContext.Calendars.AsNoTracking()
                .Where(row => row.TenantId == tenantA.Value)
                .ToListAsync());
        }
    }

    [Fact]
    [Trait("PlanCard", "2083")]
    public async Task Event_refuses_an_unknown_calendar_without_writing()
    {
        _principal.Grant(Permission.SchedulingOperate);
        var response = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.EventRoute, new
        {
            calendarId = Guid.NewGuid(),
            title = "Nowhere",
            timezone = "America/New_York",
            startUtc = "2026-03-08T13:00:00Z",
            endUtc = "2026-03-08T14:00:00Z",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("scheduling.event.calendar_not_found",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Empty(await _app.Services.GetRequiredService<ICalendarEventStore>()
            .ListAsync(NodeTenant.Resolve(_activeTeam)));
    }

    [Fact]
    public async Task Resource_setup_creates_weekday_availability_for_a_tenant_party()
    {
        _principal.Grant(Permission.SchedulingOperate);
        var party = await _parties.CreateAsync(
            NodeTenant.Resolve(_activeTeam), PartyKind.Person, "Jordan Lee", new PartyId("server-actor"), new Instant(System.TimeProvider.System.GetUtcNow()).Value);

        var response = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.ResourceAvailabilityRoute,
            new { resource = $"party:{party.Id.Value}", timezone = "America/New_York" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await _app.Services.GetRequiredService<IResourceAvailabilityStore>()
            .GetAsync(NodeTenant.Resolve(_activeTeam), ParticipantRef.Party(party.Id.Value));
        Assert.NotNull(saved);
        Assert.Single(saved.Windows);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", saved.Windows[0].Rrule);
    }

    [Fact]
    public async Task Appointment_type_lead_floor_and_buffers_are_enforced_server_side()
    {
        _principal.Grant(Permission.SchedulingAuthor, Permission.SchedulingOperate);
        await SaveAsync("buffered-visit", 0, Definition(
            "Buffered visit", durationMinutes: 30, bufferBeforeMinutes: 10,
            bufferAfterMinutes: 15, minimumLeadTimeMinutes: 120));
        var party = await _parties.CreateAsync(
            NodeTenant.Resolve(_activeTeam), PartyKind.Person, "Bookable staff", new PartyId("server-actor"), new Instant(System.TimeProvider.System.GetUtcNow()).Value);
        var resource = $"party:{party.Id.Value}";
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync(
            SchedulingDefinitionRoutes.ResourceAvailabilityRoute,
            new { resource, timezone = "America/New_York" })).StatusCode);

        var tooSoon = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.AppointmentRoute,
            new { subjectId = "subject-1", definitionId = "buffered-visit", resource, title = "Visit",
                startUtc = "2026-07-13T13:59:00Z", endUtc = "2026-07-13T18:00:00Z" });
        Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);
        Assert.Equal("scheduling.appointment.minimum_lead_time",
            (await tooSoon.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var first = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.AppointmentRoute,
            new { subjectId = "subject-1", definitionId = "buffered-visit", resource, title = "Visit",
                startUtc = "2026-07-13T15:00:00Z", endUtc = "2026-07-13T15:05:00Z" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var saved = Assert.Single(await _app.Services.GetRequiredService<ICalendarEventStore>()
            .ListAsync(NodeTenant.Resolve(_activeTeam)));
        Assert.Equal(new TimeOnly(11, 0), saved.StartTime);
        Assert.Equal(new TimeOnly(11, 30), saved.EndTime);
        Assert.Equal(TimeSpan.FromMinutes(10), saved.Padding.Pre);
        Assert.Equal(TimeSpan.FromMinutes(15), saved.Padding.Post);

        var overlap = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.AppointmentRoute,
            new { subjectId = "subject-2", definitionId = "buffered-visit", resource, title = "Next visit",
                startUtc = "2026-07-13T15:40:00Z", endUtc = "2026-07-13T16:10:00Z" });
        Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
        Assert.Equal("scheduling.appointment.slot_conflict",
            (await overlap.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Single(await _app.Services.GetRequiredService<ICalendarEventStore>()
            .ListAsync(NodeTenant.Resolve(_activeTeam)));

        var spillsPastAvailability = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.AppointmentRoute,
            new { subjectId = "subject-3", definitionId = "buffered-visit", resource, title = "Late visit",
                startUtc = "2026-07-13T21:30:00Z", endUtc = "2026-07-13T22:00:00Z" });
        Assert.Equal(HttpStatusCode.Conflict, spillsPastAvailability.StatusCode);
        Assert.Equal("scheduling.appointment.no_availability",
            (await spillsPastAvailability.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Single(await _app.Services.GetRequiredService<ICalendarEventStore>()
            .ListAsync(NodeTenant.Resolve(_activeTeam)));
    }

    [Fact]
    public void Scheduling_authoring_is_off_by_default_and_explicitly_on_in_dogfood_overlay()
    {
        var productionDefault = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var dogfoodOverlay = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindHarborlineRoot(), "tooling", "preview-host",
                "dogfood-appsettings.Production.json"), optional: false)
            .Build();

        Assert.False(productionDefault.GetValue<bool>("LocalNode:SchedulingDogfood:Enabled"));
        Assert.True(dogfoodOverlay.GetValue<bool>("LocalNode:SchedulingDogfood:Enabled"));
    }

    private Task<HttpResponseMessage> SaveAsync(string id, int expectedRevision, JsonElement definition) =>
        _client.PutAsJsonAsync($"{SchedulingDefinitionRoutes.RouteBase}/{id}/draft",
            new { expectedRevision, definition });

    private static JsonElement Definition(
        string title,
        int durationMinutes = 30,
        int bufferBeforeMinutes = 0,
        int bufferAfterMinutes = 0,
        int minimumLeadTimeMinutes = 0) => Json($$"""
        {
          "schema":"harborline.scheduling-definition-draft/v0",
          "title":"{{title}}",
          "timezone":"America/New_York",
          "activities":[{"id":"appointment","durationMinutes":{{durationMinutes}}}],
          "bufferBeforeMinutes":{{bufferBeforeMinutes}},
          "bufferAfterMinutes":{{bufferAfterMinutes}},
          "minimumLeadTimeMinutes":{{minimumLeadTimeMinutes}},
          "resourceRequirements":[]
        }
        """);
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
    private async Task<List<NodeSchedulingDraftRow>> DraftsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.Drafts.ToListAsync();
    }

    private async Task<List<NodeSchedulingDraftAuditRow>> AuditsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.DraftAudit.ToListAsync();
    }

    private static TeamContext Context(TeamId id) =>
        new(id, id.Value.ToString("D"), new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private static string FindHarborlineRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tooling", "preview-host",
                    "dogfood-appsettings.Production.json")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Could not locate the Harborline root from " + AppContext.BaseDirectory);
    }

    private sealed class MutableActiveTeamAccessor(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; set; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void KeepEvent() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }

    private sealed class MutablePrincipal(string userId) : IAuthorizationContext, ICurrentUser
    {
        private readonly HashSet<string> _permissions = new(StringComparer.Ordinal);
        public string UserId { get; set; } = userId;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public bool HasPermission(string permission) => _permissions.Contains(permission);
        public void Grant(params string[] permissions) => _permissions.UnionWith(permissions);
        public void Clear() => _permissions.Clear();
    }

    private sealed class TestDbFactory(DbContextOptions<NodeLocalSchedulingDbContext> options)
        : IDbContextFactory<NodeLocalSchedulingDbContext>
    {
        public NodeLocalSchedulingDbContext CreateDbContext() => new(options);
        public Task<NodeLocalSchedulingDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
