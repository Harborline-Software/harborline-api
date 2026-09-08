using System.Text.Json;
using Harborline.Api.Contracts;
using Harborline.Api.Testing;

namespace Harborline.Api.Tests;

public sealed class ApiContractTests
{
    [Fact]
    public async Task Fixture_is_tenant_isolated()
    {
        var client = new FixtureHarborlineApiClient();
        using var json = JsonDocument.Parse("{\"score\":42}");
        await client.SendAsync(new("PUT", "/inspections/1", new("tenant-a", "user-a"), json.RootElement));

        var sameTenant = await client.SendAsync(new("GET", "/inspections/1", new("tenant-a", "user-b")));
        var otherTenant = await client.SendAsync(new("GET", "/inspections/1", new("tenant-b", "user-a")));

        Assert.True(sameTenant.IsSuccess);
        Assert.Equal(404, otherTenant.Status);
    }

    [Fact]
    public void Production_rejects_development_adapter()
    {
        var adapter = new FixtureHarborlineApiClient();
        Assert.Throws<InvalidOperationException>(() =>
            HarborlineRuntimeGuard.EnsureEnvironmentAllows("Production", [adapter]));
    }
}
