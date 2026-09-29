using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

public sealed partial class FormsRouteTests
{
    [Fact(DisplayName = "submit: an undeclared field is one error at its own pointer (T-303, platform schema validation)")]
    public async Task Submit_UndeclaredField_IsOneFieldAddressedError()
    {
        var resp = await _client.PostAsJsonAsync($"{Base}/{FormId}/submit",
            new { station = "S-1", result = "PASS", extra = 1 });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var error = Assert.Single(body.GetProperty("errors").EnumerateArray().ToArray());
        Assert.Equal("/extra", error.GetProperty("jsonPointer").GetString());
        Assert.Equal("additional-properties", error.GetProperty("code").GetString());
    }
}
