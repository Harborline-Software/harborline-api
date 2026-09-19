using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Scheduling;

public sealed partial class SchedulingDefinitionRouteTests
{
    private static readonly string[] WindowRoutes = ["draft", "restore", "validate"];
    private static readonly string[] InstanceRoutes = ["appointment", "event", "availability"];
    private const string WindowDefinitionId = "windowed-schedule";

    public static IEnumerable<object[]> SchedulingWindowCases()
    {
        foreach (var route in WindowRoutes)
        foreach (var minute in new[] { -1, 0, 30, 60, 61, 999 })
            yield return [route, minute];
    }

    [Theory]
    [MemberData(nameof(SchedulingWindowCases))]
    public async Task Scheduling_route_enforces_contract_window(string route, int minute)
    {
        var request = await PrepareWindowScheduleAsync(route);
        if (minute != 999) WindowTarget(request.Body)["contractWindow"] = DefinitionWindowAssertions.Window();
        _clock.Now = DefinitionWindowAssertions.OpensAt.AddMinutes(minute);
        var before = await WindowStoreSnapshotAsync();

        using var response = await SendWindowScheduleAsync(request);

        if (minute is >= 0 and < 60 or 999)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            if (route == "validate")
                Assert.Equal(before, await WindowStoreSnapshotAsync());
            else
                Assert.NotEqual(before, await WindowStoreSnapshotAsync());
        }
        else
        {
            await DefinitionWindowAssertions.RefusedAsync(response, request.Identity, _clock.Now);
            Assert.Equal(before, await WindowStoreSnapshotAsync());
        }
    }

    public static IEnumerable<object[]> SchedulingDeniedWindows()
    {
        foreach (var route in WindowRoutes)
        foreach (var malformed in new[] { false, true })
            yield return [route, malformed];
    }

    [Theory]
    [MemberData(nameof(SchedulingDeniedWindows))]
    public async Task Scheduling_window_does_not_change_authorization_denial(string route, bool malformed)
    {
        var request = await PrepareWindowScheduleAsync(route);
        _principal.Clear();
        var before = await WindowStoreSnapshotAsync();
        using var baseline = await SendWindowScheduleAsync(request);
        WindowTarget(request.Body)["contractWindow"] = malformed ? null : DefinitionWindowAssertions.Window();
        _clock.Now = DefinitionWindowAssertions.ClosesAt;
        using var response = await SendWindowScheduleAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(await baseline.Content.ReadAsStringAsync(), await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await WindowStoreSnapshotAsync());
    }

    public static IEnumerable<object[]> SchedulingMalformedWindows()
    {
        foreach (var route in WindowRoutes)
        foreach (var window in new[] { "null", "{}", "{\"opensAt\":\"bad\",\"closesAt\":\"bad\"}",
                     "{\"opensAt\":\"2026-07-13T13:00:00Z\",\"closesAt\":\"2026-07-13T12:00:00Z\"}" })
            yield return [route, window];
    }

    [Theory]
    [MemberData(nameof(SchedulingMalformedWindows))]
    public async Task Scheduling_route_refuses_malformed_window(string route, string window)
    {
        var request = await PrepareWindowScheduleAsync(route);
        WindowTarget(request.Body)["contractWindow"] = JsonNode.Parse(window);
        var before = await WindowStoreSnapshotAsync();
        using var response = await SendWindowScheduleAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var refusal = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("definition.invalid-contract-window", refusal.GetProperty("code").GetString());
        Assert.Equal(before, await WindowStoreSnapshotAsync());
    }

    [Theory]
    [InlineData("draft", false)]
    [InlineData("draft", true)]
    [InlineData("restore", false)]
    [InlineData("restore", true)]
    public async Task Scheduling_stored_window_cannot_be_bypassed(string route, bool widen)
    {
        var request = await PrepareWindowScheduleAsync(route);
        // Restore already has an unwindowed revision 1; the others start without a definition.
        var expectedRevision = route == "restore" ? 1 : 0;
        _principal.Grant(Permission.SchedulingAuthor);
        var definition = JsonNode.Parse(Definition("Windowed").GetRawText())!.AsObject();
        definition["contractWindow"] = DefinitionWindowAssertions.Window();
        using var seed = await SaveAsync(WindowDefinitionId, expectedRevision,
            JsonSerializer.SerializeToElement(definition));
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        if (route == "draft") request.Body["expectedRevision"] = 1;
        if (widen)
        {
            WindowTarget(request.Body)["contractWindow"] = DefinitionWindowAssertions.Window();
            WindowTarget(request.Body)["contractWindow"]!["closesAt"] = DefinitionWindowAssertions.ClosesAt.AddDays(1);
        }
        _clock.Now = DefinitionWindowAssertions.ClosesAt;
        var before = await WindowStoreSnapshotAsync();
        using var response = await SendWindowScheduleAsync(request);
        await DefinitionWindowAssertions.RefusedAsync(response, WindowDefinitionId, _clock.Now);
        Assert.Equal(before, await WindowStoreSnapshotAsync());
    }

    [Fact]
    public async Task Scheduling_restore_checks_source_window_even_with_an_unwindowed_head()
    {
        _principal.Grant(Permission.SchedulingAuthor);
        var definition = JsonNode.Parse(Definition("Windowed").GetRawText())!.AsObject();
        definition["contractWindow"] = DefinitionWindowAssertions.Window();
        using var first = await SaveAsync(WindowDefinitionId, 0, JsonSerializer.SerializeToElement(definition));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await SaveAsync(WindowDefinitionId, 1, Definition("Unwindowed"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        _clock.Now = DefinitionWindowAssertions.ClosesAt;
        var before = await WindowStoreSnapshotAsync();
        using var response = await _client.PostAsJsonAsync(
            $"{SchedulingDefinitionRoutes.RouteBase}/{WindowDefinitionId}/restore", new { revision = 1 });
        await DefinitionWindowAssertions.RefusedAsync(response, WindowDefinitionId, _clock.Now);
        Assert.Equal(before, await WindowStoreSnapshotAsync());
    }

    public static IEnumerable<object[]> SchedulingInstanceRoutes() =>
        InstanceRoutes.Select(route => new object[] { route });

    [Theory]
    [MemberData(nameof(SchedulingInstanceRoutes))]
    public async Task Scheduling_instance_route_ignores_a_declared_window(string route)
    {
        // The contract window is a definition-plane admission gate. Booking an appointment, creating an
        // event and setting availability are instance-plane acts, so a window must never refuse them.
        var request = await PrepareWindowScheduleAsync(route);
        WindowTarget(request.Body)["contractWindow"] = DefinitionWindowAssertions.Window();
        _clock.Now = DefinitionWindowAssertions.ClosesAt.AddHours(1);
        var before = await WindowStoreSnapshotAsync();

        using var response = await SendWindowScheduleAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(before, await WindowStoreSnapshotAsync());
    }

    [Fact]
    public async Task Scheduling_appointment_ignores_the_window_authored_on_the_definition_it_names()
    {
        var request = await PrepareWindowScheduleAsync("appointment");
        _principal.Grant(Permission.SchedulingAuthor);
        var definition = JsonNode.Parse(Definition("Windowed").GetRawText())!.AsObject();
        definition["contractWindow"] = DefinitionWindowAssertions.Window();
        using var seed = await SaveAsync(WindowDefinitionId, 0,
            JsonSerializer.SerializeToElement(definition));
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        request.Body["definitionId"] = WindowDefinitionId;
        _principal.Clear();
        _principal.Grant(Permission.SchedulingOperate);
        _clock.Now = DefinitionWindowAssertions.ClosesAt.AddHours(1);

        using var response = await SendWindowScheduleAsync(request);

        // Booking may still be refused on its own terms (lead time, availability); it may not be refused
        // 422 for a closed definition window, which is the only 422 this route can produce.
        Assert.NotEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private async Task<WindowScheduleRequest> PrepareWindowScheduleAsync(string route)
    {
        // Grant only the permission this route already requires.
        _principal.Grant(route is "draft" or "restore" or "validate"
            ? Permission.SchedulingAuthor : Permission.SchedulingOperate);
        if (route == "restore")
        {
            using var seed = await SaveAsync(WindowDefinitionId, 0, Definition("Source"));
            Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        }
        var resource = "party:window-resource";
        if (route is "appointment" or "availability")
        {
            var party = await _parties.CreateAsync(NodeTenant.Resolve(_activeTeam), PartyKind.Person,
                "Window resource", new PartyId("server-actor"), _clock.Now);
            resource = $"party:{party.Id.Value}";
            if (route == "appointment")
            {
                using var seed = await _client.PostAsJsonAsync(SchedulingDefinitionRoutes.ResourceAvailabilityRoute,
                    new { resource, timezone = "UTC" });
                Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
            }
        }
        var definition = JsonNode.Parse(Definition("Windowed").GetRawText());
        return route switch
        {
            "draft" => new($"{SchedulingDefinitionRoutes.RouteBase}/{WindowDefinitionId}/draft", WindowDefinitionId,
                new JsonObject { ["expectedRevision"] = 0, ["definition"] = definition }, true),
            "restore" => new($"{SchedulingDefinitionRoutes.RouteBase}/{WindowDefinitionId}/restore", WindowDefinitionId,
                new JsonObject { ["revision"] = 1 }),
            "validate" => new(SchedulingDefinitionRoutes.RouteBase + "/validate", SchedulingDefinitionRoutes.RouteBase + "/validate",
                new JsonObject { ["definition"] = definition }),
            "appointment" => new(SchedulingDefinitionRoutes.AppointmentRoute, SchedulingDefinitionRoutes.AppointmentRoute,
                JsonSerializer.SerializeToNode(new { subjectId = "subject", resource, title = "Visit",
                    startUtc = "2026-07-13T15:00:00Z", endUtc = "2026-07-13T15:30:00Z" })!.AsObject()),
            "event" => new(SchedulingDefinitionRoutes.EventRoute, SchedulingDefinitionRoutes.EventRoute,
                JsonSerializer.SerializeToNode(new { resource, title = "Event", allDay = true,
                    startDate = "2026-07-13", endDate = "2026-07-13" })!.AsObject()),
            "availability" => new(SchedulingDefinitionRoutes.ResourceAvailabilityRoute, SchedulingDefinitionRoutes.ResourceAvailabilityRoute,
                JsonSerializer.SerializeToNode(new { resource, timezone = "UTC" })!.AsObject()),
            _ => throw new ArgumentOutOfRangeException(nameof(route)),
        };
    }

    private Task<HttpResponseMessage> SendWindowScheduleAsync(WindowScheduleRequest request) => request.Put
        ? _client.PutAsJsonAsync(request.Path, request.Body) : _client.PostAsJsonAsync(request.Path, request.Body);

    private static JsonObject WindowTarget(JsonObject body) => body["definition"]?.AsObject() ?? body;

    private async Task<string> WindowStoreSnapshotAsync()
    {
        await using var calendar = await _calendarFactory.CreateDbContextAsync();
        return JsonSerializer.Serialize(new
        {
            drafts = await DraftsAsync(), audits = await AuditsAsync(),
            events = await calendar.CalendarEvents.AsNoTracking().ToListAsync(),
            availability = await calendar.ResourceAvailability.AsNoTracking().ToListAsync(),
        });
    }

    private sealed record WindowScheduleRequest(string Path, string Identity, JsonObject Body, bool Put = false);
}
