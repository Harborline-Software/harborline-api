using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.LocalNodeHost.Tests.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

public sealed partial class FormDefinitionRouteTests
{
    public static IEnumerable<object[]> FormWindowCases()
    {
        foreach (var restore in new[] { false, true })
        foreach (var minute in new[] { -1, 0, 30, 60, 61 })
            yield return [restore, minute];
    }

    [Theory]
    [MemberData(nameof(FormWindowCases))]
    public async Task Form_route_enforces_contract_window(bool restore, int minute)
    {
        var body = await PrepareWindowFormAsync(restore);
        body["contractWindow"] = DefinitionWindowAssertions.Window();
        _windowClock.Now = DefinitionWindowAssertions.OpensAt.AddMinutes(minute);

        using var response = await SendWindowFormAsync(restore, body);

        var admitted = minute is >= 0 and < 60;
        if (admitted)
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        else
            await DefinitionWindowAssertions.RefusedAsync(response, FormId, _windowClock.Now);
        await AssertFormRevisionCountAsync((restore ? 1 : 0) + (admitted ? 1 : 0));
        if (admitted && !restore)
        {
            var stored = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}");
            Assert.Equal(DefinitionWindowAssertions.ClosesAt,
                stored.GetProperty("contractWindow").GetProperty("closesAt").GetDateTimeOffset());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Form_window_does_not_change_authorization_denial(bool restore)
    {
        var body = await PrepareWindowFormAsync(restore);
        _authorization.Allow(Harborline.Api.Foundation.IdentityAtlas.TeamRolePermissions.RecordsWrite);
        using var baseline = await SendWindowFormAsync(restore, body);
        body["contractWindow"] = DefinitionWindowAssertions.Window();
        _windowClock.Now = DefinitionWindowAssertions.ClosesAt;
        using var windowed = await SendWindowFormAsync(restore, body);
        Assert.Equal(HttpStatusCode.Forbidden, windowed.StatusCode);
        Assert.Equal(await baseline.Content.ReadAsStringAsync(), await windowed.Content.ReadAsStringAsync());
        await AssertFormRevisionCountAsync(restore ? 1 : 0);
    }

    public static IEnumerable<object[]> FormMalformedWindows()
    {
        foreach (var restore in new[] { false, true })
        foreach (var window in new[] { "null", "{}", "{\"opensAt\":\"bad\",\"closesAt\":\"bad\"}",
                     "{\"opensAt\":\"2026-07-13T13:00:00Z\",\"closesAt\":\"2026-07-13T12:00:00Z\"}" })
            yield return [restore, window];
    }

    [Theory]
    [MemberData(nameof(FormMalformedWindows))]
    public async Task Form_route_refuses_malformed_window(bool restore, string window)
    {
        var body = await PrepareWindowFormAsync(restore);
        body["contractWindow"] = JsonNode.Parse(window);
        using var response = await SendWindowFormAsync(restore, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var refusal = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("definition.invalid-contract-window", refusal.GetProperty("code").GetString());
        await AssertFormRevisionCountAsync(restore ? 1 : 0);
    }

    public static IEnumerable<object[]> FormStoredWindows()
    {
        foreach (var restore in new[] { false, true })
        foreach (var draft in new[] { false, true })
        foreach (var widen in new[] { false, true })
            yield return [restore, draft, widen];
    }

    [Theory]
    [MemberData(nameof(FormStoredWindows))]
    public async Task Form_stored_window_blocks_replacement_and_restore(bool restore, bool draft, bool widen)
    {
        // An unwindowed historical source must not bypass the current head or latest draft.
        using var first = await SendWindowFormAsync(false, JsonSerializer.SerializeToNode(SaveBody())!.AsObject());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = JsonSerializer.SerializeToNode(SaveBody())!.AsObject();
        body["contractWindow"] = DefinitionWindowAssertions.Window();
        body["draft"] = draft;
        using var second = await SendWindowFormAsync(false, body);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        _windowClock.Now = DefinitionWindowAssertions.ClosesAt;
        var replacement = restore ? new JsonObject { ["version"] = "1.0.0" }
            : JsonSerializer.SerializeToNode(SaveBody())!.AsObject();
        if (widen)
        {
            replacement["contractWindow"] = DefinitionWindowAssertions.Window();
            replacement["contractWindow"]!["closesAt"] = DefinitionWindowAssertions.ClosesAt.AddDays(1);
        }
        using var response = await SendWindowFormAsync(restore, replacement);
        await DefinitionWindowAssertions.RefusedAsync(response, FormId, _windowClock.Now);
        await AssertFormRevisionCountAsync(2);
    }

    [Fact]
    public async Task Form_restore_checks_source_window_and_retains_it_on_the_new_draft()
    {
        var body = JsonSerializer.SerializeToNode(SaveBody())!.AsObject();
        body["contractWindow"] = DefinitionWindowAssertions.Window();
        using var first = await SendWindowFormAsync(false, body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var restored = await SendWindowFormAsync(true, new JsonObject { ["version"] = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var source = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}/versions/1.0.1");
        Assert.Equal(DefinitionWindowAssertions.ClosesAt,
            source.GetProperty("contractWindow").GetProperty("closesAt").GetDateTimeOffset());
        // Clear both current heads while the source's window is open, then restore that source after close.
        using var unwindowed = await SendWindowFormAsync(false, JsonSerializer.SerializeToNode(SaveBody())!.AsObject());
        Assert.Equal(HttpStatusCode.OK, unwindowed.StatusCode);
        _windowClock.Now = DefinitionWindowAssertions.ClosesAt;
        using var denied = await SendWindowFormAsync(true, new JsonObject { ["version"] = "1.0.0" });
        await DefinitionWindowAssertions.RefusedAsync(denied, FormId, _windowClock.Now);
        await AssertFormRevisionCountAsync(3);
    }

    private async Task<JsonObject> PrepareWindowFormAsync(bool restore)
    {
        if (!restore) return JsonSerializer.SerializeToNode(SaveBody())!.AsObject();
        using var seed = await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        return new JsonObject { ["version"] = "1.0.0" };
    }

    private Task<HttpResponseMessage> SendWindowFormAsync(bool restore, JsonObject body) => restore
        ? _client.PostAsJsonAsync($"{DefBase}/{FormId}/restore", body)
        : _client.PutAsJsonAsync($"{DefBase}/{FormId}", body);

    private async Task AssertFormRevisionCountAsync(int expected)
    {
        var versions = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}/versions");
        Assert.Equal(expected, versions.GetArrayLength());
    }

    private sealed class WindowClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DefinitionWindowAssertions.OpensAt;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
