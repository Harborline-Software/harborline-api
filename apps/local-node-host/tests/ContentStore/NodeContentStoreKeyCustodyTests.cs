using System.Data.Common;
using System.Text;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.LocalNodeHost.Data.ContentStore;
using Harborline.Foundation.Assets.Common;
using Harborline.Foundation.ContentStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ContentStore;

// T-521/content-store-epoch-key-store acceptance 1 and 6. Oracles: ADR 0116 decision 4 (the content store's tenant key is
// the destroyable version-2 stored key, never a version-1 derived one) and decision 6 (no epoch key leaves the node
// through sync or re-host, which carry only rows of local-node.db), checked against the raw rows and files.
public sealed partial class NodeContentStoreTests
{
    private static readonly byte[] HierarchyRoot = Enumerable.Repeat((byte)0x61, 32).ToArray();

    // A version-1 provider that fails the test if the content store ever reaches for a root-derived key.
    private sealed class NoLegacyKeys : ITenantKeyProvider
    {
        public Task<ReadOnlyMemory<byte>> DeriveKeyAsync(TenantId tenant, string purpose, CancellationToken ct) =>
            throw new InvalidOperationException("The content store asked for a version-1 derived key.");

        public Task<ReadOnlyMemory<byte>> DeriveSubjectKeyAsync(TenantId tenant, SubjectId subject, string purpose, ISubjectErasureRegistry erasure, CancellationToken ct) =>
            throw new InvalidOperationException("The content store asked for a version-1 derived subject key.");
    }

    private (StoredTenantKeyProvider Provider, string Directory) StoredKeys()
    {
        var directory = Path.Combine(_node.DataDirectory, "tenant-keys");
        return (new StoredTenantKeyProvider(new FileSystemTenantKeyStore(directory), HierarchyRoot, new NoLegacyKeys()), directory);
    }

    private InMemoryContentStore StoreOver(ITenantContentKeyProvider keys) =>
        new(new(300_000, 600_000, 600_000, Expiry), keys, _clock, _node.Storage);

    [Fact]
    public void Epoch_clause_1_the_content_store_key_is_the_version_2_stored_key_in_tenant_keys()
    {
        var (provider, directory) = StoredKeys();
        var keys = new NodeTenantContentKeyProvider(provider);

        var key = keys.GetKey(Tenant);

        Assert.Equal(32, key.Length);
        Assert.Equal(key, keys.GetKey(Tenant));
        static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        var expected = Path.Combine(directory, "tenant", Hash(Tenant.Value), Hash("content-store"), "v2.key");
        Assert.Equal([expected], Directory.EnumerateFiles(directory, "*.key", SearchOption.AllDirectories).ToArray());
    }

    [Fact]
    public async Task Epoch_clause_1_destroying_the_tenant_keys_leaves_committed_content_unreadable()
    {
        var (provider, _) = StoredKeys();
        var store = StoreOver(new NodeTenantContentKeyProvider(provider));
        var reference = await store.CommitReferenceAsync(Tenant, await store.PutAsync(Tenant, new MemoryStream("secret"u8.ToArray()), "text/plain", null, Write()),
            new("record", "one"), "x.txt", Write());
        await using (var body = (await store.ReadAsync(Tenant, reference, true, Reader)).Body) Assert.Equal(6, await body.ReadAsync(new byte[16]));

        await provider.DestroyTenantKeysAsync(Tenant, CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var body = (await store.ReadAsync(Tenant, reference, true, Reader)).Body;
            await body.CopyToAsync(Stream.Null);
        });
    }

    [Fact]
    public void Epoch_clause_1_the_node_composition_gives_the_store_the_version_2_provider()
    {
        var services = new ServiceCollection();
        services.ConfigureStoredTenantKeys(Path.Combine(_node.DataDirectory, "tenant-keys"), HierarchyRoot, new NoLegacyKeys());
        services.AddNodeContentStore(_node.DataDirectory);
        using var provider = services.BuildServiceProvider();

        var keys = Assert.IsType<NodeTenantContentKeyProvider>(provider.GetRequiredService<ITenantContentKeyProvider>());

        Assert.Equal(new NodeTenantContentKeyProvider(provider.GetRequiredService<StoredTenantKeyProvider>()).GetKey(Tenant), keys.GetKey(Tenant));
    }

    [Fact]
    public async Task Epoch_clause_6_no_row_of_local_node_db_holds_an_epoch_key_so_sync_and_rehost_carry_none()
    {
        var store = Store();
        await Commit(store, await Put(store, Text(64, 'k')));
        await Commit(store, await Put(store, Text(64, 'm')), element: "two");
        var epoch = (await _node.Storage.Epochs.GetCurrentAsync(Tenant))!;
        var needles = new[] { epoch.WrappedKey, Encoding.ASCII.GetBytes(Convert.ToBase64String(epoch.WrappedKey)), Encoding.ASCII.GetBytes(Convert.ToHexString(epoch.WrappedKey)), Encoding.ASCII.GetBytes(Convert.ToHexStringLower(epoch.WrappedKey)) };

        await using var context = await _node.Contexts.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        var connection = context.Database.GetDbConnection();
        var tables = Strings(connection, "SELECT name FROM sqlite_master WHERE type = 'table'");
        Assert.Contains("content_store_items", tables);
        var cells = 0;
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\"";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    if (reader.IsDBNull(column)) continue;
                    cells++;
                    var value = reader.GetValue(column) switch { byte[] bytes => bytes, var other => Encoding.UTF8.GetBytes(Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture)!) };
                    foreach (var needle in needles) Assert.Equal(-1, value.AsSpan().IndexOf(needle));
                }
            }
        }

        Assert.True(cells > 0);
        Assert.True(File.Exists(Path.Combine(EpochDirectory, epoch.EpochId.ToString("N") + ".epoch")));
    }

    private static List<string> Strings(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }
}
