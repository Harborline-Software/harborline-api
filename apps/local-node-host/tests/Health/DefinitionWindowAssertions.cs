using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

internal static class DefinitionWindowAssertions
{
    internal static readonly DateTimeOffset OpensAt = DateTimeOffset.Parse("2026-07-13T12:00:00Z");
    internal static readonly DateTimeOffset ClosesAt = OpensAt.AddHours(1);

    internal static JsonObject Window() => new()
    {
        ["opensAt"] = OpensAt,
        ["closesAt"] = ClosesAt,
    };

    internal static async Task RefusedAsync(HttpResponseMessage response, string id, DateTimeOffset observedAt)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(6, body.EnumerateObject().Count());
        Assert.Equal("kernel.definition-contract-window", body.GetProperty("code").GetString());
        Assert.Equal(422, body.GetProperty("statusCode").GetInt32());
        Assert.Equal(id, body.GetProperty("definitionId").GetString());
        Assert.Equal(OpensAt, body.GetProperty("opensAt").GetDateTimeOffset());
        Assert.Equal(ClosesAt, body.GetProperty("closesAt").GetDateTimeOffset());
        Assert.Equal(observedAt, body.GetProperty("observedAt").GetDateTimeOffset());
    }
}
