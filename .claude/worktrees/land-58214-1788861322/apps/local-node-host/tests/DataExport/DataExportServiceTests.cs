using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.LocalFirst;
using Harborline.Api.Foundation.MultiTenancy;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.DataExport;

public sealed class DataExportServiceTests
{
    [Fact]
    public async Task Single_tenant_export_round_trips_only_that_tenants_data()
    {
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddHarborlineLocalFirst();
        await using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IOfflineStore>();
        await store.WriteAsync(
            "tenants/tenant-a/forms/contact-1",
            Encoding.UTF8.GetBytes("{\"name\":\"Ada\"}"));
        await store.WriteAsync(
            "tenants/tenant-b/forms/contact-2",
            Encoding.UTF8.GetBytes("{\"name\":\"Grace\"}"));

        var exporter = provider.GetRequiredService<IDataExportService>();
        var handle = await exporter.StartExportAsync(new ExportRequest
        {
            Tenant = TenantSelection.Of(new TenantId("tenant-a")),
        });

        var status = await exporter.GetStatusAsync(handle.ExportId);
        Assert.Equal(ExportState.Completed, status.State);
        Assert.Equal(100, status.ProgressPercent);

        await using var download = await exporter.OpenDownloadAsync(handle.ExportId);
        using var package = await JsonDocument.ParseAsync(download);

        Assert.Equal("harborline.portability.v1", package.RootElement.GetProperty("schema").GetString());
        var contributor = Assert.Single(package.RootElement.GetProperty("contributors").EnumerateArray());
        Assert.Equal("offline-store", contributor.GetProperty("key").GetString());

        var entry = Assert.Single(contributor.GetProperty("entries").EnumerateArray());
        Assert.Equal("tenants/tenant-a/forms/contact-1", entry.GetProperty("key").GetString());
        Assert.Equal(
            "{\"name\":\"Ada\"}",
            Encoding.UTF8.GetString(entry.GetProperty("payloadBase64").GetBytesFromBase64()));
    }

    [Fact]
    public async Task All_accessible_export_applies_scope_within_each_tenant()
    {
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddHarborlineLocalFirst();
        await using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IOfflineStore>();
        await store.WriteAsync("tenants/tenant-a/forms/contact-1", [1]);
        await store.WriteAsync("tenants/tenant-b/forms/contact-2", [2]);
        await store.WriteAsync("tenants/tenant-b/accounting/invoice-1", [3]);

        var exporter = provider.GetRequiredService<IDataExportService>();
        var handle = await exporter.StartExportAsync(new ExportRequest
        {
            Tenant = TenantSelection.All,
            IncludeScopes = ["forms"],
        });

        await using var download = await exporter.OpenDownloadAsync(handle.ExportId);
        using var package = await JsonDocument.ParseAsync(download);
        var contributor = Assert.Single(package.RootElement.GetProperty("contributors").EnumerateArray());
        var keys = contributor.GetProperty("entries").EnumerateArray()
            .Select(static entry => entry.GetProperty("key").GetString()!)
            .ToArray();

        Assert.Equal(
            ["tenants/tenant-a/forms/contact-1", "tenants/tenant-b/forms/contact-2"],
            keys);
    }
}
