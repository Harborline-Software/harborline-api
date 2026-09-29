using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// T-981 (DES-0029 §9 K2): unknown kinds through POST /packs/install on the composed host. A restricting
/// kind the node does not know refuses the install by name, a permitting view kind stays inert, and a
/// content kind the node cannot classify is refused under its own code and pointer, not as not_verified.
/// </summary>
public sealed partial class ComposedHostBootSmokeTests
{
    private const string UnknownKindRootSeedHex =
        "9898989898989898989898989898989898989898989898989898989898989898";

    [Fact(DisplayName = "K2: route install refuses unknown policy and retention kinds and reports both")]
    public async Task Route_install_refuses_unknown_policy_and_retention_kinds_and_reports_both()
    {
        await using var host = StartUnknownKindHost("K2 unknown restricting kinds");
        using var client = await UnknownKindClientAsync(host);

        var bytes = await ExportUnknownKindPackAsync(client, host, "k2.restricting", new object[]
        {
            FormItem("k2.restricting.policy", new
            {
                title = "Future policy",
                policies = new[] { new { id = "policy.future", kind = "FutureEffect", triggers = new[] { "onWrite" } } },
            }),
            FormItem("k2.restricting.retention", new
            {
                title = "Future retention",
                audit = new { jurisdictionPreset = "future-preset" },
            }),
        });

        var (status, body) = await InstallUnknownKindPackAsync(client, host, bytes);
        Assert.True(status == HttpStatusCode.UnprocessableEntity, body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("pack.install.refused.admission", Strings(root.GetProperty("refusalCodes")));
        Assert.Equal(
            "/contents/0/contentBase64,/contents/1/contentBase64",
            string.Join(",", root.GetProperty("refusals").EnumerateArray()
                .Select(refusal => refusal.GetProperty("pointer").GetString()).Order(StringComparer.Ordinal)));
        var admission = root.GetProperty("preview").GetProperty("admissionRefusals").EnumerateArray().ToList();
        Assert.Equal(2, admission.Count);
        Assert.All(admission, refusal =>
            Assert.Equal("definition.restricting_kind_unknown", refusal.GetProperty("code").GetString()));
        Assert.Contains(admission, refusal => refusal.GetProperty("message").GetString()!.Contains("FutureEffect", StringComparison.Ordinal));
        Assert.Contains(admission, refusal => refusal.GetProperty("message").GetString()!.Contains("future-preset", StringComparison.Ordinal));
        await AssertNotInstalledAsync(client, host, "k2.restricting");
    }

    [Fact(DisplayName = "K2: route install admits an unknown view kind inert and silent")]
    public async Task Route_install_admits_an_unknown_view_kind_inert_and_silent()
    {
        await using var host = StartUnknownKindHost("K2 unknown view kind");
        using var client = await UnknownKindClientAsync(host);

        var bytes = await ExportUnknownKindPackAsync(client, host, "k2.view", new object[]
        {
            new
            {
                key = "k2.view.future",
                kind = "ViewDefinition",
                version = "1.0.0",
                content = new { viewKind = "views.future/not-installed", parameters = new { } },
            },
        });

        var (status, body) = await InstallUnknownKindPackAsync(client, host, bytes);
        Assert.True(status == HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        Assert.Empty(document.RootElement.GetProperty("refusalCodes").EnumerateArray());
        Assert.Empty(document.RootElement.GetProperty("refusals").EnumerateArray());
    }

    [Fact(DisplayName = "K2: route install names an unknown content kind and its pointer")]
    public async Task Install_reports_an_unknown_content_kind_by_name()
    {
        await using var host = StartUnknownKindHost("K2 unknown content kind");
        using var client = await UnknownKindClientAsync(host);

        var signed = await ExportUnknownKindPackAsync(client, host, "k2.content-kind", new object[]
        {
            FormItem("k2.content-kind.form", new { title = "Known form" }),
        });
        var file = JsonNode.Parse(signed)!.AsObject();
        file["contents"]![0]!["kind"] = 99;

        var (status, body) = await InstallUnknownKindPackAsync(
            client, host, Encoding.UTF8.GetBytes(file.ToJsonString()));
        Assert.True(status == HttpStatusCode.UnprocessableEntity, body);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("pack.install.refused.unknown_content_kind", Strings(document.RootElement.GetProperty("refusalCodes")));
        var refusal = Assert.Single(document.RootElement.GetProperty("refusals").EnumerateArray().ToList());
        Assert.Equal("pack.install.refused.unknown_content_kind", refusal.GetProperty("code").GetString());
        Assert.Equal("/contents/0/kind", refusal.GetProperty("pointer").GetString());
    }

    private static ComposedHost StartUnknownKindHost(string profile) => ComposedHost.Start(
        profile,
        webClient: false,
        llmProxy: false,
        schedulingDogfood: false,
        multiTeam: false,
        environment: "Production",
        rootSeedHex: UnknownKindRootSeedHex);

    private static async Task<HttpClient> UnknownKindClientAsync(ComposedHost host)
    {
        var client = new HttpClient { BaseAddress = await host.AwaitReadinessAsync(), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "composed-host-smoke-token");
        return client;
    }

    private static object FormItem(string key, object content) => new
    {
        key,
        kind = "FormDefinition",
        version = "1.0.0",
        content,
    };

    private static async Task<byte[]> ExportUnknownKindPackAsync(
        HttpClient client, ComposedHost host, string key, object[] contents)
    {
        using var export = await client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, new
        {
            key,
            version = "1.0.0",
            name = key,
            description = "K2 unknown-kind route proof",
            scopeTier = "Horizontal",
            contents,
            dependencies = Array.Empty<object>(),
        }, host.Deadline);
        var bytes = await export.Content.ReadAsByteArrayAsync(host.Deadline);
        Assert.True(export.IsSuccessStatusCode, Encoding.UTF8.GetString(bytes));
        return bytes;
    }

    private static async Task<(HttpStatusCode Status, string Body)> InstallUnknownKindPackAsync(
        HttpClient client, ComposedHost host, byte[] bytes)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var install = await client.PostAsync(PackInstallRoutes.InstallRoute, content, host.Deadline);
        return (install.StatusCode, await install.Content.ReadAsStringAsync(host.Deadline));
    }

    private static async Task AssertNotInstalledAsync(HttpClient client, ComposedHost host, string packKey)
    {
        using var installed = await client.GetAsync(PackInstallRoutes.ListInstalledRoute, host.Deadline);
        Assert.Equal(HttpStatusCode.OK, installed.StatusCode);
        using var document = JsonDocument.Parse(await installed.Content.ReadAsStringAsync(host.Deadline));
        Assert.DoesNotContain(document.RootElement.EnumerateArray(),
            pack => pack.GetProperty("packKey").GetString() == packKey);
    }

    private static string Strings(JsonElement array) => string.Join(",", array.EnumerateArray().Select(item => item.GetString()));
}
