using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

public sealed partial class ComposedHostBootSmokeTests
{
    [Fact]
    public async Task Access_form_is_published_and_listed_after_boot()
    {
        await using var first = StartAccessHost();
        using (var firstClient = await AccessClientAsync(first))
            Assert.Equal(HttpStatusCode.OK, (await firstClient.GetAsync("/api/local-node/forms/access.grant-a-role")).StatusCode);
        await first.StopAsync();
        await using var host = StartAccessHost(first.DataDirectory);
        using var client = await AccessClientAsync(host);
        using var response = await client.GetAsync("/api/local-node/forms/access.grant-a-role");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var form = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("access.grant-a-role", form.RootElement.GetProperty("formId").GetString());
        using var catalogue = JsonDocument.Parse(await client.GetStringAsync(FormDefinitionRoutes.RouteBase));
        Assert.Contains(catalogue.RootElement.EnumerateArray(), row =>
            row.GetProperty("formId").GetString() == "access.grant-a-role");
        using var workflow = await client.GetAsync("/api/local-node/workflows/definitions/access.privileged-grant-review");
        Assert.Equal(HttpStatusCode.OK, workflow.StatusCode);
    }

    [Fact]
    public async Task Access_form_select_options_and_required_flags_are_on_the_wire()
    {
        await using var host = StartAccessHost();
        using var client = await AccessClientAsync(host);
        using var form = JsonDocument.Parse(await client.GetStringAsync("/api/local-node/forms/access.grant-a-role"));
        var fields = form.RootElement.GetProperty("sections")[0].GetProperty("fields").EnumerateArray()
            .ToDictionary(field => field.GetProperty("name").GetString()!);
        Assert.Equal(new[] { "manual", "invitation", "workflow", "ticket" },
            fields["reason"].GetProperty("options").EnumerateArray().Select(option => option.GetString()));
        Assert.Equal(new[] { "cache", "online-only" },
            fields["residency"].GetProperty("options").EnumerateArray().Select(option => option.GetString()));
        Assert.True(fields["reason"].GetProperty("required").GetBoolean());
        Assert.True(fields["residency"].GetProperty("required").GetBoolean());
        Assert.False(fields["effectiveTo"].GetProperty("required").GetBoolean());
    }

    private static ComposedHost StartAccessHost(string? directory = null) => ComposedHost.Start(
        "Access granting form", false, false, false, false, "Production",
        dataDirectoryOverride: directory, healthPort: 7328);

    private static async Task<HttpClient> AccessClientAsync(ComposedHost host)
    {
        var client = new HttpClient { BaseAddress = await host.AwaitReadinessAsync() };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "composed-host-smoke-token");
        return client;
    }
}
