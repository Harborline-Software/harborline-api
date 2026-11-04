using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using Harborline.Api.LocalNodeHost.Data.ContentStore;
using Harborline.Foundation.Assets.Common;
using Harborline.Foundation.ContentStore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.ContentStore;

public sealed partial class NodeContentStoreTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "t521-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Key = Enumerable.Repeat((byte)0x38, 32).ToArray();
    private static readonly TenantId Tenant = new("test-tenant");
    private sealed class Keys : ITenantContentKeyProvider
    {
        public byte[] GetKey(TenantId tenant) => SHA256.HashData("fixed-test-root"u8);
    }
    private static InMemoryContentStore Store(IContentStoreStorage backing) => new(new(300_000, 600_000, 600_000, TimeSpan.FromMinutes(1)), new Keys(), TimeProvider.System, backing);
    private static Guid Put(InMemoryContentStore store, byte[]? bytes = null) => store.Put(Tenant, new MemoryStream(bytes ?? "hello"u8.ToArray()), "text/plain", null);
    private SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_path, "content.db"), Pooling = false }.ToString());
        connection.Open();
        Execute(connection, $"PRAGMA key = \"x'{Convert.ToHexString(Key)}'\";");
        return connection;
    }
    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    [Fact]
    public void Save_failure_after_staging_delete_rolls_back_disk_and_memory_then_retry_survives_restart()
    {
        Guid stage;
        ContentReference reference;
        using (var backing = new NodeContentStoreStorage(_path, Key))
        {
            var store = Store(backing);
            stage = Put(store);
            using var database = OpenDatabase();
            Execute(database, "CREATE TRIGGER fail_item BEFORE INSERT ON entries WHEN NEW.kind='item' BEGIN SELECT RAISE(ABORT,'injected storage failure'); END");
            Assert.Throws<SqliteException>(() => store.Commit(Tenant, stage, new("record", "one"), "x.txt"));
            Assert.Equal(1, store.StagedCount);
            Assert.Equal(0, store.CommittedCount);
            using var command = database.CreateCommand(); command.CommandText = "SELECT kind FROM entries";
            Assert.Equal("stage", command.ExecuteScalar());
            Execute(database, "DROP TRIGGER fail_item");
            reference = store.Commit(Tenant, stage, new("record", "one"), "x.txt");
        }
        using var reopened = new NodeContentStoreStorage(_path, Key);
        var rebuilt = Store(reopened);
        using var body = rebuilt.Read(Tenant, reference, true).Body;
        using var plain = new MemoryStream(); body.CopyTo(plain);
        Assert.Equal("hello"u8.ToArray(), plain.ToArray());
        Assert.Equal(0, rebuilt.StagedCount);
    }

    [Fact]
    public void Duplicate_owner_change_and_nonfinal_removal_persist_and_count_logical_quota_after_restart()
    {
        ContentReference first, second;
        using (var backing = new NodeContentStoreStorage(_path, Key))
        {
            var store = Store(backing);
            first = store.Commit(Tenant, Put(store), new("r1", "one"), "a.txt");
            second = store.Commit(Tenant, Put(store), new("r2", "two"), "b.txt");
            Assert.Equal(1, store.CommittedCount);
        }
        using (var backing = new NodeContentStoreStorage(_path, Key))
        {
            var store = new InMemoryContentStore(new(100, 100, 10, TimeSpan.FromMinutes(1)), new Keys(), TimeProvider.System, backing);
            var stage = Put(store);
            Assert.Equal("content.tenant-quota", Assert.Throws<ContentRefusedException>(() => store.Commit(Tenant, stage, new("r3", "three"), "c.txt")).Code);
            Assert.False(store.RemoveReference(Tenant, first, HoldStatus.None, "remove-one"));
        }
        using var final = new NodeContentStoreStorage(_path, Key);
        var rebuilt = Store(final);
        Assert.Throws<ContentNotFoundException>(() => rebuilt.Read(Tenant, first, true));
        Assert.Equal(5, rebuilt.Read(Tenant, second, true).Body.Length);
        Assert.True(rebuilt.RemoveReference(Tenant, second, HoldStatus.None, "remove-last"));
        Assert.Equal("last-reference-removed", Assert.Single(rebuilt.Reclamations).Cause);
    }

    [Fact]
    public void Only_one_writer_may_cache_the_store_and_wrong_keys_refuse_reopen()
    {
        using (var backing = new NodeContentStoreStorage(_path, Key))
            Assert.Throws<IOException>(() => new NodeContentStoreStorage(_path, Key));
        Assert.Throws<SqliteException>(() => new NodeContentStoreStorage(_path, Enumerable.Repeat((byte)0x39, 32).ToArray()));
        using var recovered = new NodeContentStoreStorage(_path, Key);
    }

    [Fact]
    public void Database_file_is_encrypted_at_rest()
    {
        using (var backing = new NodeContentStoreStorage(_path, Key))
        {
            var store = Store(backing);
            store.Commit(Tenant, Put(store), new("record", "one"), "x.txt");
        }
        // A plaintext SQLite file starts with this 16-byte header (sqlite.org/fileformat.html section 1.3).
        var header = File.ReadAllBytes(Path.Combine(_path, "content.db"))[..16];
        Assert.NotEqual("SQLite format 3\0"u8.ToArray(), header);
    }

    [Fact]
    public void Composition_resolves_the_durable_ports_and_runtime_quota_tracks_sparse_allocations()
    {
        var services = new ServiceCollection().AddNodeContentStore(_path, Key);
        using var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<NodeContentStoreStorage>();
        Assert.Same(storage, provider.GetRequiredService<IContentStoreStorage>());
        var quota = provider.GetRequiredService<IContentStoreQuotaBase>();
        var beforeFree = new DriveInfo(storage.DirectoryPath).AvailableFreeSpace;
        var baseline = quota.GetQuotaBaseBytes();
        var afterFree = new DriveInfo(storage.DirectoryPath).AvailableFreeSpace;
        Assert.InRange(baseline, Math.Min(beforeFree, afterFree), Math.Max(beforeFree, afterFree) + 1024 * 1024);
        // A sparse logical gigabyte must not be counted as a physical gigabyte (Q22).
        using (var sparse = File.Create(Path.Combine(storage.DirectoryPath, "allocation-probe")))
        {
            MarkSparse(sparse);
            sparse.SetLength(1L << 30);
        }
        var sparseFree = new DriveInfo(storage.DirectoryPath).AvailableFreeSpace;
        // Free space can move either way between the two reads, so the tolerance is symmetric (16 MiB).
        Assert.InRange(quota.GetQuotaBaseBytes() - sparseFree, -16L * 1024 * 1024, 16L * 1024 * 1024);
        File.Delete(Path.Combine(storage.DirectoryPath, "allocation-probe"));
    }

    [Fact]
    public void An_all_zero_key_is_refused_and_a_second_provider_reopens_with_the_derived_key()
    {
        Assert.Throws<ArgumentException>(() => new NodeContentStoreStorage(_path, new byte[32]));
        var services = new ServiceCollection().AddNodeContentStore(_path, Key);
        ContentReference reference;
        using (var first = services.BuildServiceProvider())
        {
            var store = Store(first.GetRequiredService<IContentStoreStorage>());
            reference = store.Commit(Tenant, Put(store), new("record", "one"), "x.txt");
        }
        // A second provider from the same collection must open the same database, not a new one under a cleared key.
        using var second = services.BuildServiceProvider();
        var reopened = Store(second.GetRequiredService<IContentStoreStorage>());
        using var body = reopened.Read(Tenant, reference, true).Body;
        using var plain = new MemoryStream(); body.CopyTo(plain);
        Assert.Equal("hello"u8.ToArray(), plain.ToArray());
    }

    [Fact]
    public void Allocated_bytes_match_the_operating_system_block_count_for_dense_and_sparse_files()
    {
        Directory.CreateDirectory(_path);
        // Dense: one byte past 1 MiB, so allocation rounds up to a block and differs from st_size.
        var dense = Path.Combine(_path, "dense.bin");
        var bytes = new byte[(1 << 20) + 1];
        RandomNumberGenerator.Fill(bytes);
        using (var stream = new FileStream(dense, FileMode.Create)) { stream.Write(bytes); stream.Flush(true); }
        // Sparse: one written byte, 64 MiB logical length.
        var sparse = Path.Combine(_path, "sparse.bin");
        using (var stream = new FileStream(sparse, FileMode.Create)) { MarkSparse(stream); stream.WriteByte(1); stream.SetLength(64L << 20); stream.Flush(true); }
        foreach (var file in new[] { dense, sparse })
        {
            long measured;
            using (var handle = File.OpenHandle(file)) measured = NodeContentStoreQuotaBase.AllocatedBytes(handle);
            if (OperatingSystem.IsWindows())
            {
                // GetFileInformationByHandleEx is the documented API; with the sparse marker, the 64 MiB probe allocates
                // at most one 1 MiB run and the dense file at least its written length.
                Assert.InRange(measured, file == dense ? bytes.Length : 1, file == dense ? bytes.Length + (1 << 20) : 1 << 20);
                continue;
            }
            // Independent oracle: stat(1)'s allocated block count, never the struct offsets under test.
            Assert.Equal(StatAllocatedBytes(file), measured);
        }
    }

    private static long StatAllocatedBytes(string file)
    {
        var start = OperatingSystem.IsMacOS()
            ? new ProcessStartInfo("stat", ["-f", "%b 512", file])
            : new ProcessStartInfo("stat", ["-c", "%b %B", file]);
        start.RedirectStandardOutput = true;
        using var process = Process.Start(start)!;
        var fields = process.StandardOutput.ReadToEnd().Trim().Split(' ');
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return long.Parse(fields[0], CultureInfo.InvariantCulture) * long.Parse(fields[1], CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Durable_commit_dedup_timing_is_measured_with_interleaved_equal_sized_samples()
    {
        using var backing = new NodeContentStoreStorage(_path, Key);
        var store = Store(backing);
        var bytes = Enumerable.Repeat((byte)'a', 65_536).ToArray();
        var duplicate = new List<double>(); var promotion = new List<double>();
        // Warm up crypto, serializer and database before collecting observations.
        for (var i = 0; i < 60; i++)
        {
            bytes[0] = (byte)('a' + i % 26); bytes[1] = (byte)('a' + i / 26);
            var stage = Put(store, bytes);
            var start = Stopwatch.GetTimestamp();
            var reference = store.Commit(Tenant, stage, new("record", "one"), "x.txt");
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var same = Put(store, bytes);
            start = Stopwatch.GetTimestamp();
            var repeated = store.Commit(Tenant, same, new("record", "two"), "x.txt");
            var dedupElapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.Equal(reference.ContentId, repeated.ContentId);
            if (i >= 10) { promotion.Add(elapsed); duplicate.Add(dedupElapsed); }
            store.RemoveReference(Tenant, reference, HoldStatus.None, "cleanup");
            store.RemoveReference(Tenant, repeated, HoldStatus.None, "cleanup");
        }
        promotion.Sort(); duplicate.Sort();
        output.WriteLine($"Durable SQLite commit, 50 samples per path: promotion median={promotion[25]:F3}ms p95={promotion[47]:F3}ms; duplicate median={duplicate[25]:F3}ms p95={duplicate[47]:F3}ms");
        // Measurement only; DES-0037 gate-host qualification is the separately ruled follow-on.
        Assert.All(promotion.Concat(duplicate), value => Assert.True(value > 0));
    }

    // NTFS allocates clusters on SetLength unless the file is first marked sparse (FSCTL_SET_SPARSE,
    // https://learn.microsoft.com/windows/win32/api/winioctl/ni-winioctl-fsctl_set_sparse). Unix filesystems
    // used here leave unwritten ranges unallocated without a marker.
    private static void MarkSparse(FileStream stream)
    {
        if (!OperatingSystem.IsWindows()) return;
        const uint FsctlSetSparse = 0x000900C4;
        Assert.True(DeviceIoControl(stream.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero),
            $"FSCTL_SET_SPARSE failed: {Marshal.GetLastPInvokeError()}");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize,
        IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, true); }
}
