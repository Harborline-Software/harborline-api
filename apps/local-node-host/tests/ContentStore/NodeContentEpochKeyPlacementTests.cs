using System.Security.Cryptography;
using System.Text;
using Harborline.Api.LocalNodeHost.Data.ContentStore;
using Harborline.Foundation.Assets.Common;
using Harborline.Foundation.ContentStore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ContentStore;

// M11 ruling of 2026-11-13 (1): epoch keys live in the tenant-key store's directory, the install footprint's tenant-keys/,
// even when LocalNode:DataDirectory points elsewhere. Runs the real Program wiring.
// Oracle: the legacy footprint layout a fresh root claims, written as literals (LocalNode on Windows and macOS, local-node on
// Linux), the record's directory names (tenant-keys, content-epochs) and the tenant directory rule (SHA-256 hex of the id).
public sealed class NodeContentEpochKeyPlacementTests
{
    [Fact]
    public async Task Epoch_keys_land_under_the_footprint_tenant_keys_when_the_data_directory_is_configured_elsewhere()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"epoch-placement-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(scratch, "configured-data");
        var footprintRoot = Path.Combine(scratch, "footprint");
        var tenant = new TenantId("placement-tenant");
        var epochId = Guid.NewGuid();
        try
        {
            await Assert.ThrowsAsync<ProbeComplete>(() => global::LocalNodeHostComposition.RunAsync(
                ["--LocalNode:RootSeedHex=" + new string('2', 64), "--urls=http://127.0.0.1:7330"],
                sessionTokenOverride: "epoch-placement-probe", dataDirectory: dataDirectory,
                installFootprintRootOverride: footprintRoot,
                finalServiceProviderProbe: (services, factory) =>
                {
                    var provider = factory.CreateServiceProvider(factory.CreateBuilder(services));
                    using var lifetime = Assert.IsAssignableFrom<IDisposable>(provider);
                    var storage = provider.GetRequiredService<NodeContentStoreStorage>();
                    storage.Epochs.CreateAsync(tenant, new ContentEpochKey(epochId, new byte[32], new byte[12], new byte[16]))
                        .AsTask().GetAwaiter().GetResult();
                    throw new ProbeComplete();
                }));

            var footprintData = Path.Combine(footprintRoot, OperatingSystem.IsLinux() ? "local-node" : "LocalNode");
            var tenantHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("placement-tenant")));
            Assert.True(File.Exists(Path.Combine(footprintData, "tenant-keys", "content-epochs", tenantHash, epochId.ToString("N") + ".epoch")));
            Assert.False(Directory.Exists(Path.Combine(dataDirectory, "tenant-keys")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }

    private sealed class ProbeComplete : Exception;
}
