using System.Reflection;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.LocalNodeHost.Data.ContentStore;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.CryptoShred;

public sealed class FileSystemTenantKeyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tenant-key-durability-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Acceptance_3_flushes_to_disk_before_atomic_publication_and_directory_sync()
    {
        var path = RecordPath;
        var steps = new List<string>();
        var store = CreateStore(temporary => new ObservedFileStream(temporary, flushToDisk =>
        {
            Assert.True(flushToDisk);
            Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(temporary));
            Assert.NotEqual(path, temporary);
            Assert.False(File.Exists(path));
            steps.Add("file-flushed");
        }), directory =>
        {
            Assert.Equal("wrapped-key", File.ReadAllText(path));
            NodeContentByteStore.SyncDirectory(directory);
            steps.Add("directory-flushed");
        });

        Assert.True(await store.TryCreateAsync("tenant:v2", "wrapped-key"u8.ToArray(), default));

        Assert.Equal("file-flushed", steps[0]);
        Assert.Equal("directory-flushed", steps[1]);
        Assert.Single(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
        Assert.Equal("wrapped-key"u8.ToArray(), (await new FileSystemTenantKeyStore(_directory).ReadAsync("tenant:v2", default))!.Value.ToArray());
    }

    [Fact]
    public async Task Acceptance_3_a_disk_flush_failure_prevents_publication_and_success()
    {
        var failure = new IOException("injected disk flush failure");
        var store = CreateStore(path => new ObservedFileStream(path, flushToDisk =>
        {
            if (flushToDisk) throw failure;
        }));

        var thrown = await Assert.ThrowsAsync<IOException>(() => store.TryCreateAsync("tenant:v2", "wrapped-key"u8.ToArray(), default));

        Assert.Same(failure, thrown);
        Assert.False(File.Exists(RecordPath));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acceptance_3_syncs_parent_entries_even_after_interrupted_directory_creation(bool alreadyCreated)
    {
        if (alreadyCreated) Directory.CreateDirectory(Path.GetDirectoryName(RecordPath)!);
        var synced = new List<string>();
        var store = CreateStore(syncDirectory: directory =>
        {
            Assert.True(File.Exists(RecordPath));
            NodeContentByteStore.SyncDirectory(directory);
            synced.Add(directory);
        });

        Assert.True(await store.TryCreateAsync("tenant:v2", "wrapped-key"u8.ToArray(), default));

        Assert.Equal(alreadyCreated
            ? new[] { Path.Combine(_directory, "tenant"), _directory }
            : new[] { Path.Combine(_directory, "tenant"), _directory, Path.GetDirectoryName(_directory)! }, synced);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acceptance_3_a_parent_sync_failure_blocks_success_including_when_the_record_exists(bool alreadyExists)
    {
        if (alreadyExists)
            Assert.True(await new FileSystemTenantKeyStore(_directory).TryCreateAsync("tenant:v2", "winner"u8.ToArray(), default));
        var failure = new IOException("injected parent sync failure");
        var store = CreateStore(syncDirectory: directory =>
        {
            if (directory == _directory) throw failure;
            NodeContentByteStore.SyncDirectory(directory);
        });

        var thrown = await Assert.ThrowsAsync<IOException>(() => store.TryCreateAsync("tenant:v2", "wrapped-key"u8.ToArray(), default));

        Assert.Same(failure, thrown);
        Assert.Single(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Concurrent_creators_keep_one_complete_winner_and_clean_up_temporary_files()
    {
        var first = new FileSystemTenantKeyStore(_directory);
        var second = new FileSystemTenantKeyStore(_directory);
        var results = await Task.WhenAll(
            first.TryCreateAsync("tenant:v2", "first-key"u8.ToArray(), default),
            second.TryCreateAsync("tenant:v2", "second-key"u8.ToArray(), default));

        Assert.Single(results, created => created);
        Assert.Equal(results[0] ? "first-key" : "second-key", await File.ReadAllTextAsync(RecordPath));
        Assert.Single(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
        Assert.False(await first.TryCreateAsync("tenant:v2", "replacement"u8.ToArray(), default));
        Assert.Equal(results[0] ? "first-key" : "second-key", await File.ReadAllTextAsync(RecordPath));
    }

    [Fact]
    public async Task Cancellation_does_not_publish_a_record_or_leave_a_temporary_file()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var store = new FileSystemTenantKeyStore(_directory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryCreateAsync("tenant:v2", "wrapped-key"u8.ToArray(), cancellation.Token));

        Assert.False(File.Exists(RecordPath));
        if (Directory.Exists(_directory)) Assert.Empty(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Read_delete_and_prefix_delete_keep_the_existing_slot_contract()
    {
        var store = new FileSystemTenantKeyStore(_directory);
        Assert.Null(await store.ReadAsync("tenant:v2", default));
        Assert.True(await store.TryCreateAsync("tenant:v2", "wrapped-key"u8.ToArray(), default));
        Assert.True(await store.TryCreateAsync("other:v2", "other-key"u8.ToArray(), default));

        await store.DeleteByPrefixAsync("tenant:", default);

        Assert.Null(await store.ReadAsync("tenant:v2", default));
        Assert.Equal("other-key"u8.ToArray(), (await store.ReadAsync("other:v2", default))!.Value.ToArray());
        await store.DeleteAsync("other:v2", default);
        Assert.Null(await store.ReadAsync("other:v2", default));
    }

    [Theory]
    [InlineData("../tenant:v2")]
    [InlineData("tenant:v2:")]
    public async Task Invalid_slots_are_rejected_before_creating_files(string slot)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new FileSystemTenantKeyStore(_directory).TryCreateAsync(slot, "wrapped-key"u8.ToArray(), default));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task Existing_root_write_never_syncs_above_root_and_read_retries_the_barrier()
    {
        Directory.CreateDirectory(_directory);
        var synced = new List<string>();
        var store = CreateStore(syncDirectory: directory =>
        {
            if (directory != _directory && directory != Path.Combine(_directory, "tenant"))
                throw new IOException("above root");
            synced.Add(directory);
        });
        Assert.True(await store.TryCreateAsync("tenant:v2", "wrapped-key"u8.ToArray(), default));
        Assert.Equal(new[] { Path.Combine(_directory, "tenant"), _directory }, synced);
        synced.Clear();
        Assert.Equal("wrapped-key"u8.ToArray(), (await store.ReadAsync("tenant:v2", default))!.Value.ToArray());
        Assert.Equal(new[] { Path.Combine(_directory, "tenant"), _directory }, synced);
        var failing = CreateStore(syncDirectory: _ => throw new IOException("read barrier"));
        await Assert.ThrowsAsync<IOException>(() => failing.ReadAsync("tenant:v2", default));
    }

    [Fact]
    public async Task Read_of_an_existing_visible_record_requires_a_successful_bounded_barrier()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RecordPath)!);
        await File.WriteAllTextAsync(RecordPath, "visible-key");
        var synced = new List<string>();
        var store = CreateStore(syncDirectory: synced.Add);
        Assert.Equal("visible-key"u8.ToArray(), (await store.ReadAsync("tenant:v2", default))!.Value.ToArray());
        Assert.Equal(new[] { Path.Combine(_directory, "tenant"), _directory }, synced);
        var failure = new IOException("injected read sync failure");
        var failing = CreateStore(syncDirectory: _ => throw failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => failing.ReadAsync("tenant:v2", default)));
    }

    // Oracle: M11 control 1389 and ADR 0116 d5 require the same real flush/publication/barrier as async create.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Synchronous_create_flushes_before_publication_and_syncs_the_exact_bounded_chain(bool rootExists)
    {
        if (rootExists) Directory.CreateDirectory(_directory);
        var flushed = false;
        var synced = new List<string>();
        var store = CreateStore(path => new ObservedFileStream(path, toDisk =>
        {
            Assert.True(toDisk);
            Assert.False(File.Exists(RecordPath));
            flushed = true;
        }), directory =>
        {
            Assert.True(flushed);
            Assert.Equal("sync-key", File.ReadAllText(RecordPath));
            if (rootExists && directory != _directory && directory != Path.Combine(_directory, "tenant"))
                throw new IOException("above root");
            synced.Add(directory);
            NodeContentByteStore.SyncDirectory(directory);
        });
        Assert.True(store.TryCreate("tenant:v2", "sync-key"u8.ToArray(), default));
        Assert.Equal(rootExists ? new[] { Path.Combine(_directory, "tenant"), _directory }
            : new[] { Path.Combine(_directory, "tenant"), _directory, Path.GetDirectoryName(_directory)! }, synced);
        synced.Clear();
        Assert.Equal("sync-key"u8.ToArray(), store.Read("tenant:v2", default)!.Value.ToArray());
        Assert.Equal(new[] { Path.Combine(_directory, "tenant"), _directory }, synced);
        Assert.Single(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
        Assert.False(new FileSystemTenantKeyStore(_directory).TryCreate("tenant:v2", "replacement"u8.ToArray(), default));
        Assert.Equal("sync-key", File.ReadAllText(RecordPath));
    }

    [Fact]
    public void Synchronous_flush_failure_prevents_publication_and_cleans_temporary_files()
    {
        var failure = new IOException("disk failure");
        var store = CreateStore(path => new ObservedFileStream(path, toDisk => { if (toDisk) throw failure; }));
        Assert.Same(failure, Assert.Throws<IOException>(() => store.TryCreate("tenant:v2", "sync-key"u8.ToArray(), default)));
        Assert.False(File.Exists(RecordPath));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Synchronous_directory_failure_refuses_create_and_a_later_read_retries_the_barrier()
    {
        var failure = new IOException("directory failure");
        var store = CreateStore(syncDirectory: _ => throw failure);
        Assert.Same(failure, Assert.Throws<IOException>(() => store.TryCreate("tenant:v2", "sync-key"u8.ToArray(), default)));
        Assert.Same(failure, Assert.Throws<IOException>(() => store.Read("tenant:v2", default)));
        var synced = new List<string>();
        Assert.Equal("sync-key"u8.ToArray(), CreateStore(syncDirectory: synced.Add).Read("tenant:v2", default)!.Value.ToArray());
        Assert.Equal(new[] { Path.Combine(_directory, "tenant"), _directory }, synced);
        Assert.Single(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Synchronous_competing_creators_preserve_one_complete_record()
    {
        var first = new FileSystemTenantKeyStore(_directory);
        var second = new FileSystemTenantKeyStore(_directory);
        using var start = new ManualResetEventSlim();
        var firstCreate = Task.Run(() => { start.Wait(); return first.TryCreate("tenant:v2", "first"u8.ToArray(), default); });
        var secondCreate = Task.Run(() => { start.Wait(); return second.TryCreate("tenant:v2", "second"u8.ToArray(), default); });
        start.Set();
        var results = await Task.WhenAll(firstCreate, secondCreate);
        Assert.Single(results, result => result);
        Assert.Equal(results[0] ? "first" : "second", File.ReadAllText(RecordPath));
        Assert.Single(Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Synchronous_cancelled_create_does_not_create_a_directory_or_record()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var store = new FileSystemTenantKeyStore(_directory);
        Assert.Throws<OperationCanceledException>(() => store.TryCreate("tenant:v2", "sync-key"u8.ToArray(), cancelled.Token));
        Assert.False(Directory.Exists(_directory));
    }

    private string RecordPath => Path.Combine(_directory, "tenant", "v2.key");

    private FileSystemTenantKeyStore CreateStore(
        Func<string, FileStream>? createStream = null, Action<string>? syncDirectory = null) =>
        (FileSystemTenantKeyStore)Activator.CreateInstance(
            typeof(FileSystemTenantKeyStore), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[]
            {
                _directory,
                createStream ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true)),
                syncDirectory ?? NodeContentByteStore.SyncDirectory
            }, null)!;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class ObservedFileStream(string path, Action<bool> onFlush)
        : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true)
    {
        public override void Flush(bool flushToDisk)
        {
            onFlush(flushToDisk);
            base.Flush(flushToDisk);
        }
    }
}
