using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Definitions;

namespace Harborline.Api.LocalNodeHost.Tests.Definitions;

/// <summary>Protects the definition cascade contract from block-tier coupling or wire drift.</summary>
public sealed class CascadeLayerLocationTests
{
    [Fact(DisplayName = "CascadeLayer is foundation-owned and keeps its Pack wire form")]
    public void CascadeLayer_IsFoundationOwned_AndKeepsPackWireForm()
    {
        Assert.Equal("Harborline.Api.Foundation", typeof(CascadeLayer).Assembly.GetName().Name);
        Assert.Equal("Harborline.Api.Foundation.Definitions", typeof(CascadeLayer).Namespace);
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<CascadeLayer>().Select(layer => (int)layer));

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new { CascadeLayer = CascadeLayer.Pack },
            options);

        Assert.Equal("{\"cascadeLayer\":\"Pack\"}", Encoding.UTF8.GetString(bytes));
    }
}
