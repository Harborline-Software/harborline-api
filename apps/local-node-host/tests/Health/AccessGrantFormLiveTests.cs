using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    [Fact]
    public async Task Access_form_submission_issues_a_grant_visible_in_holders()
    {
        await using var bootstrap = StartAccessHost();
        await bootstrap.AwaitReadinessAsync();
        await bootstrap.StopAsync();
        await GrantPackOperationToHostInstallerAsync(bootstrap, new string('1', 64), NodeCallerParty.OperatorParty.Value);
        await using var host = StartAccessHost(bootstrap.DataDirectory);
        using var client = await AccessClientAsync(host);
        using var before = JsonDocument.Parse(await client.GetStringAsync(AccessHoldersRead.Route));
        var existing = before.RootElement.GetProperty("holders").EnumerateArray()
            .Select(row => row.GetProperty("grantId").GetString()).ToHashSet();
        using var response = await client.PostAsJsonAsync("/api/local-node/forms/access.grant-a-role/submit", AccessCandidate());
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var holders = JsonDocument.Parse(await client.GetStringAsync(AccessHoldersRead.Route));
        var issued = Assert.Single(holders.RootElement.GetProperty("holders").EnumerateArray(), row =>
            !existing.Contains(row.GetProperty("grantId").GetString()));
        Assert.Equal("administrator", issued.GetProperty("role").GetProperty("name").GetString());
        Assert.Equal("/", issued.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Access_form_submission_without_a_holding_has_the_standard_audited_refusal()
    {
        await using var host = StartAccessHost();
        using var client = await AccessClientAsync(host);
        using var response = await client.PostAsJsonAsync("/api/local-node/forms/access.grant-a-role/submit", AccessCandidate());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var refusal = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "auditId", "code", "detail", "permission", "remediation", "title" },
            refusal.RootElement.EnumerateObject().Select(property => property.Name).Order());
        Assert.False(string.IsNullOrWhiteSpace(refusal.RootElement.GetProperty("auditId").GetString()));
    }

    [Fact]
    public async Task Access_form_invalid_submission_is_rejected_by_forms_validation()
    {
        await using var host = StartAccessHost();
        using var client = await AccessClientAsync(host);
        using var response = await client.PostAsJsonAsync("/api/local-node/forms/access.grant-a-role/submit", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Access_form_issued_grant_survives_restart()
    {
        await using var bootstrap = StartAccessHost();
        await bootstrap.AwaitReadinessAsync();
        await bootstrap.StopAsync();
        await GrantPackOperationToHostInstallerAsync(bootstrap, new string('1', 64), NodeCallerParty.OperatorParty.Value);
        await using var host = StartAccessHost(bootstrap.DataDirectory);
        string issuedGrantId;
        using (var client = await AccessClientAsync(host))
        {
            using var before = JsonDocument.Parse(await client.GetStringAsync(AccessHoldersRead.Route));
            var existing = before.RootElement.GetProperty("holders").EnumerateArray()
                .Select(row => row.GetProperty("grantId").GetString()).ToHashSet();
            using var submitted = await client.PostAsJsonAsync("/api/local-node/forms/access.grant-a-role/submit", AccessCandidate());
            Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
            using var issued = JsonDocument.Parse(await client.GetStringAsync(AccessHoldersRead.Route));
            issuedGrantId = Assert.Single(issued.RootElement.GetProperty("holders").EnumerateArray(), row =>
                !existing.Contains(row.GetProperty("grantId").GetString())).GetProperty("grantId").GetString()!;
        }
        await host.StopAsync();
        await using var restarted = StartAccessHost(bootstrap.DataDirectory);
        using var afterRestart = await AccessClientAsync(restarted);
        using var holders = JsonDocument.Parse(await afterRestart.GetStringAsync(AccessHoldersRead.Route));
        Assert.Contains(holders.RootElement.GetProperty("holders").EnumerateArray(), holder =>
            holder.GetProperty("grantId").GetString() == issuedGrantId &&
            holder.GetProperty("role").GetProperty("name").GetString() == "administrator");
    }

    private static object AccessCandidate() => new
    {
        person = "principal-access-recipient", role = "administrator", scope = "/", residency = "cache",
        effectiveFrom = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"), effectiveTo = "", reason = "manual",
    };

    private static ComposedHost StartAccessHost(string? directory = null) => ComposedHost.Start(
        "Access granting form", false, false, false, false, "Production",
        dataDirectoryOverride: directory, healthPort: 7335);

    private static async Task<HttpClient> AccessClientAsync(ComposedHost host)
    {
        var client = new HttpClient { BaseAddress = await host.AwaitReadinessAsync() };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "composed-host-smoke-token");
        return client;
    }
}
