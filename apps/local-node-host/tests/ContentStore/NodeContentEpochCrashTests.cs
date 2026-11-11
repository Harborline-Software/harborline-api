using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Harborline.Api.LocalNodeHost.Data.ContentStore;
using Harborline.Foundation.ContentStore;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ContentStore;

public sealed partial class NodeContentStoreTests
{
    private static readonly Lazy<Task<string>> CrashWorker = new(BuildCrashWorkerAsync);

    [Theory]
    [InlineData("epoch:file-flushed")]
    [InlineData("epoch:renamed")]
    [InlineData("epoch:directory-flushed")]
    [InlineData("current:file-flushed")]
    [InlineData("current:renamed")]
    [InlineData("current:directory-flushed")]
    [InlineData("batch:committed")]
    [InlineData("batch:checkpoint")]
    [InlineData("retire:fenced")]
    [InlineData("retire:checkpoint")]
    [InlineData("retire:pending-written")]
    [InlineData("destroy:file-deleted")]
    [InlineData("destroy:directory-flushed")]
    public async Task A_process_crash_at_each_rotation_step_keeps_every_real_database_row_readable(string step)
    {
        var storage = new NodeContentStoreStorage(_node.Contexts, _node.DataDirectory);
        var store = Store(storage: storage);
        var reference = await Commit(store, await Put(store));
        await Put(store, "still-staged"u8.ToArray());
        var first = (await storage.Epochs.GetCurrentAsync(Tenant))!.EpochId;
        File.SetLastWriteTimeUtc(Path.Combine(EpochDirectory, first.ToString("N") + ".epoch"), _clock.GetUtcNow().UtcDateTime);
        if (step.StartsWith("destroy:", StringComparison.Ordinal))
        {
            await store.RotateEpochAsync(Tenant, Write("sys.scheduler"));
            using var initial = new NodeContentKeyRotation(storage, new(), _clock);
            await initial.RunAsync(Tenant, store, () => Write("sys.scheduler"), batchSize: 1);
            _clock.Advance(TimeSpan.FromDays(30));
        }
        else
        {
            _clock.Advance(TimeSpan.FromDays(90));
        }

        var assembly = typeof(NodeContentStoreTests).Assembly.Location;
        var signal = Path.Combine(_node.DataDirectory, "crash-reached");
        var process = new ProcessStartInfo("dotnet", ["exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), await CrashWorker.Value,
            _node.DataDirectory, _clock.GetUtcNow().UtcTicks.ToString(CultureInfo.InvariantCulture), step, signal]);
        var outcome = await RunCrashProcess(process);
        Assert.True(outcome.ExitCode == 73, outcome.Output);
        Assert.Equal(step, await File.ReadAllTextAsync(signal));

        using var reopened = _node.Restart();
        var reopenedStore = Store(storage: reopened.Storage);
        await AssertRowsReadable(reopened.Storage, reopenedStore, reference, reopened.Contexts);
        using var resumed = new NodeContentKeyRotation(reopened.Storage, new(), _clock);
        await resumed.RunAsync(Tenant, reopenedStore, () => Write("sys.scheduler"), batchSize: 1);
        await AssertRowsReadable(reopened.Storage, reopenedStore, reference, reopened.Contexts);
    }

    private static async Task AssertRowsReadable(NodeContentStoreStorage storage, InMemoryContentStore store,
        ContentReference reference, IDbContextFactory<Harborline.Api.LocalNodeHost.Data.LocalNodeDbContext> contexts)
    {
        Assert.Equal("hello"u8.ToArray(), await ReadAll(store, reference));
        await using var context = await contexts.CreateDbContextAsync();
        var staged = Assert.Single(await context.Set<ContentStagedRow>().ToListAsync());
        await using var transaction = await storage.Bookkeeping.BeginTransactionAsync();
        var item = (await transaction.FindStagedForUpdateAsync(staged.ItemKey))!;
        CryptographicOperations.ZeroMemory(await OpenLeftoverWrap(storage, item));
        Assert.NotNull(await storage.Epochs.GetAsync(Tenant, staged.EpochId));
    }

    private static async Task<string> BuildCrashWorkerAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "tests.csproj"))) root = root.Parent;
        Assert.NotNull(root);
        var sdk = await RunCrashProcess(new("dotnet", ["--version"]));
        Assert.Equal(0, sdk.ExitCode);
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        var dotnet = new DirectoryInfo(runtime).Parent!.Parent!.Parent!.FullName;
        var compiler = Path.Combine(dotnet, "sdk", sdk.Output.Trim(), "Roslyn", "bincore", "csc.dll");
        var output = Path.Combine(AppContext.BaseDirectory, "T521EpochCrashWorker.dll");
        var references = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Where(path => !path.Equals(output, StringComparison.OrdinalIgnoreCase))
            .Concat(Directory.EnumerateFiles(runtime, "*.dll"))
            .Concat(Directory.EnumerateFiles(Path.GetDirectoryName(typeof(Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions).Assembly.Location)!, "*.dll"))
            .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        var response = Path.Combine(Path.GetTempPath(), "t521-crash-compiler-" + Guid.NewGuid().ToString("N") + ".rsp");
        try
        {
            await File.WriteAllLinesAsync(response, new[] { "-noconfig", "-nostdlib+", "-target:exe", "-langversion:preview", "-nullable:enable", "-out:\"" + output + "\"" }
                .Concat(references.Select(path => "-reference:\"" + path + "\""))
                .Append("\"" + Path.Combine(root.FullName, "ContentStore", "EpochCrashWorker.cs.txt") + "\""));
            var compiled = await RunCrashProcess(new("dotnet", ["exec", compiler, "@" + response]));
            Assert.True(compiled.ExitCode == 0, compiled.Output);
            return output;
        }
        finally
        {
            File.Delete(response);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCrashProcess(ProcessStartInfo start)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
