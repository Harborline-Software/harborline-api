using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

public sealed partial class FormsRouteTests
{
    // T-974: a submission mints its form instance. The instance's wire identity is `instanceId`; a candidate
    // carrying that key is a caller-constructed record id, not form data. The form here has an OPEN schema, so
    // before the refusal the key was stored silently as a value and the submit answered 201.
    [Fact(DisplayName = "submit: a client-supplied instanceId is refused 400 with no write, and omission mints a server id (T-974)")]
    public async Task Submit_refuses_a_client_supplied_record_id()
    {
        const string openFormId = OpenFormId;
        await RegisterOpenFormAsync();
        var before = await CountTenantEntitiesAsync();

        var refused = await _client.PostAsJsonAsync($"{Base}/{openFormId}/submit",
            new { station = "Burj Khalifa", result = "PASS", instanceId = "forminst:client-constructed-id" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("request.record-id-not-accepted",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(before, await CountTenantEntitiesAsync());

        var created = await _client.PostAsJsonAsync($"{Base}/{openFormId}/submit",
            new { station = "Burj Khalifa", result = "PASS" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var minted = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("instanceId").GetString();
        Assert.StartsWith("forminst:", minted);
        Assert.NotEqual("forminst:client-constructed-id", minted);
    }

    [Fact(DisplayName = "selected submit: a client-supplied instanceId is refused 400 with no write, and omission mints a server id (T-974)")]
    public async Task Selected_submit_refuses_a_client_supplied_record_id()
    {
        await RegisterOpenFormAsync();
        _selected = new SelectedSessionRequestPrincipal("account", TenantA,
            new PrincipalUserId("form-holder"), new CanonicalPartyReference("party"),
            "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination");
        var before = await CountTenantEntitiesAsync();

        using var refusedRequest = SelectedSubmit();
        refusedRequest.RequestUri = new Uri($"/api/session/forms/{OpenFormId}/submit", UriKind.Relative);
        refusedRequest.Content = JsonContent.Create(
            new { station = "Station A", result = "PASS", instanceId = "forminst:client-constructed-id" });
        using var refused = await _client.SendAsync(refusedRequest);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("request.record-id-not-accepted",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(before, await CountTenantEntitiesAsync());

        using var createdRequest = SelectedSubmit();
        createdRequest.RequestUri = new Uri($"/api/session/forms/{OpenFormId}/submit", UriKind.Relative);
        createdRequest.Headers.Remove("Idempotency-Key");
        createdRequest.Headers.Add("Idempotency-Key", "selected-form-t974-minted");
        using var created = await _client.SendAsync(createdRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var minted = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("instanceId").GetString();
        Assert.StartsWith("forminst:", minted);
        Assert.NotEqual("forminst:client-constructed-id", minted);
    }

    private const string OpenFormId = "inspection.open.v1";

    // A published copy of the inspection form over an OPEN schema, so an unknown candidate key is not a schema error.
    private async Task RegisterOpenFormAsync()
    {
        const string openFormId = OpenFormId;
        var schema = await _app.Services.GetRequiredService<ISchemaRegistry>().RegisterAsync(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": { "station": { "type": "string" }, "result": { "type": "string" } },
              "required": ["station", "result"]
            }
            """);
        var store = _app.Services.GetRequiredService<IFormDefinitionStore>();
        var original = await store.GetCurrentPublishedAsync(new(TenantA, FormId));
        Assert.NotNull(original);
        var definition = original with
        {
            Id = new FormDefinitionId(openFormId),
            Status = FormDefinitionStatus.Draft,
            SchemaRef = schema.Id,
        };
        await store.RegisterAsync(definition);
        await store.PublishAsync(new DefinitionCoordinates(TenantA, openFormId, definition.Version.ToString()));
    }

    private async Task<int> CountTenantEntitiesAsync()
    {
        var count = 0;
        await foreach (var _ in _app.Services.GetRequiredService<IEntityStore>().QueryAsync(new EntityQuery(Tenant: TenantA)))
            count++;
        return count;
    }
}
