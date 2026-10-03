using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

// Literal mechanism and evidence-retention controls; these do not assign the
// cause or owner of a production startup failure.
[Collection("Harborline process environment")]
public sealed class SqliteStartupLifecycleProbeTests
{
    [Fact]
    public void RawProbeOwnedBusyStatementIsVisibleAtTheActualPoolReturnFailure()
    {
        SQLitePCL.Batteries_V2.Init();
        var directory = Path.Combine(Path.GetTempPath(), "sqlite-lifecycle-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var probe = new SqliteStartupLifecycleProbe();
        using var scope = probe.EnterScope();
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(directory, "control.db") + ";Pooling=True");
        SQLitePCL.sqlite3_stmt? owned = null;
        try
        {
            connection.Open();
            Assert.Equal("e_sqlcipher", SQLitePCL.raw.GetNativeLibraryName());
            connection.CreateFunction("lifecycle_probe", () => 1);
            Assert.Equal(0, SQLitePCL.raw.sqlite3_prepare_v2(connection.Handle!, "SELECT 1 UNION ALL SELECT 2", out owned));
            Assert.Equal(100, SQLitePCL.raw.sqlite3_step(owned));
            probe.Capture("controlled-preclose", connection);
            var original = Assert.Throws<SqliteException>(connection.Close);
            Assert.Equal(5, original.SqliteErrorCode);
            Assert.Contains("Deactivate", original.StackTrace!);
            probe.Capture("controlled-failure", connection, exception: original);
            var failure = Assert.Single(probe.Events, entry => entry.Phase == "controlled-failure");
            Assert.True(failure.Native.Complete, System.Text.Json.JsonSerializer.Serialize(failure.Native));
            Assert.Equal(1, failure.Native.StatementsVisited);
            Assert.Equal(1, failure.Native.BusyVisited);
            Assert.Equal(5, failure.Error!.SqliteCode);
            Assert.Contains(probe.FirstChanceSqlite, entry => entry.Error.Id == failure.Error.Id && entry.Error.SqliteCode == 5);
            Assert.Equal(ConnectionState.Open, connection.State);
            for (var index = 0; index < 256; index++) probe.Capture("later-event");
            var retainedFailure = Assert.Single(probe.FailureEvents);
            Assert.Equal("controlled-failure", retainedFailure.Phase);
            Assert.Equal(1, retainedFailure.Native.BusyVisited);

            // Only this control-owned statement is released. The observer itself
            // never owns, resets, finalizes, or disposes an enumerated statement.
            Assert.Equal(0, SQLitePCL.raw.sqlite3_finalize(owned));
            owned = null;
            connection.Close();
            Assert.Equal(ConnectionState.Closed, connection.State);
        }
        finally
        {
            if (owned is not null) SQLitePCL.raw.sqlite3_finalize(owned);
            connection.Close();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* Pooled closed control handles may retain this temporary file. */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void ManagedBusyReaderIsDisposedByCloseRatherThanMisclassifiedAsALeak()
    {
        SQLitePCL.Batteries_V2.Init();
        var directory = Path.Combine(Path.GetTempPath(), "sqlite-managed-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var probe = new SqliteStartupLifecycleProbe();
        using var scope = probe.EnterScope();
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(directory, "control.db") + ";Pooling=True");
        try
        {
            connection.Open();
            connection.CreateFunction("lifecycle_probe", () => 1);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 UNION ALL SELECT 2";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            probe.Capture("managed-preclose", connection);
            var before = Assert.Single(probe.Events);
            Assert.True(before.Native.Complete, System.Text.Json.JsonSerializer.Serialize(before.Native));
            Assert.Equal(1, before.Native.BusyVisited);
            connection.Close();
            Assert.True(reader.IsClosed);
            Assert.Equal(ConnectionState.Closed, connection.State);
            Assert.Empty(probe.Failures);
        }
        finally
        {
            connection.Close();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void EvidenceRetainsStartupAndDisposalFailuresSeparatelyWhenExportFails()
    {
        using var probe = new SqliteStartupLifecycleProbe();
        var startup = new InvalidOperationException("controlled startup failure");
        var disposal = new IOException("controlled disposal failure");
        probe.CaptureFailure("startup", startup);
        probe.CaptureFailure("disposal", disposal);
        // A directory cannot be replaced by an evidence file. Failure is harmless.
        probe.ExportBestEffort(Path.GetTempPath());
        Assert.Equal(new[] { "startup", "disposal" }, probe.Failures.Select(entry => entry.Phase));
        Assert.Equal("System.InvalidOperationException", probe.Failures[0].Error.Type);
        Assert.Equal("System.IO.IOException", probe.Failures[1].Error.Type);
        Assert.NotEqual(probe.Failures[0].Error.Id, probe.Failures[1].Error.Id);
    }

    [Fact]
    public void StackSanitizationRemovesAnEntireSourcePathBeforeApplyingTheBound()
    {
        var stack = "   at ReviewedOwner.Start() in C:\\private\\" + new string('x', 5000) + ":line 23";
        Assert.Equal("   at ReviewedOwner.Start()", SqliteStartupLifecycleProbe.SanitizeStack(stack));
    }

    [Fact]
    public async Task HostedOutputRetainsEvidenceAndOutputFailurePreservesTheOriginalException()
    {
        var original = new InvalidOperationException("private payload must not be exported");
        string? output = null;
        var propagated = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqliteStartupLifecycleProbe.RunObservedAsync(() => Task.FromException(original),
                value => output = value));
        Assert.Same(original, propagated);
        Assert.StartsWith("SQLITE_STARTUP_LIFECYCLE_V1 ", output);
        Assert.DoesNotContain("private payload", output);
        using var document = System.Text.Json.JsonDocument.Parse(output![28..]);
        Assert.Equal("test-propagated", document.RootElement.GetProperty("Failures")[0].GetProperty("Phase").GetString());
        var afterOutputFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqliteStartupLifecycleProbe.RunObservedAsync(() => Task.FromException(original),
                _ => throw new IOException("output unavailable")));
        Assert.Same(original, afterOutputFailure);
    }

    [Fact]
    public void EarlierObservedErrorsCannotEvictTheExplicitLifecycleFailures()
    {
        using var probe = new SqliteStartupLifecycleProbe();
        for (var index = 0; index < 64; index++)
            probe.Capture("earlier-observed-error", exception: new IOException("controlled observation"));
        probe.CaptureFailure("startup", new InvalidOperationException("controlled startup"));
        probe.CaptureFailure("disposal", new IOException("controlled disposal"));
        Assert.Equal(34, probe.Failures.Length);
        Assert.Equal("startup", probe.Failures[^2].Phase);
        Assert.Equal("disposal", probe.Failures[^1].Phase);
    }

    [Fact]
    public async Task FactoryRegistrationAttachesActualEfLifecycleHooksAndKeepsPoolingAndCipher()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sqlite-ef-hook-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var probe = new SqliteStartupLifecycleProbe();
        using var scope = probe.EnterScope();
        try
        {
            var services = new ServiceCollection();
            services.AddSqlCipherLocalNodeDbContextWithStoreDek(new byte[32], Path.Combine(directory, "control.db"));
            SqliteStartupLifecycleProbe.Combine(null)!(services);
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            await using var context = await factory.CreateDbContextAsync();
            var connection = Assert.IsType<SqliteConnection>(context.Database.GetDbConnection());
            Assert.True(new SqliteConnectionStringBuilder(connection.ConnectionString).Pooling);
            await context.Database.OpenConnectionAsync();
            Assert.Equal(1, await context.Database.SqlQueryRaw<int>("SELECT 1 AS Value").SingleAsync());
            await context.Database.CloseConnectionAsync();
            Assert.Contains(probe.Events, entry => entry.Phase == "opened" && entry.Native.Library == "e_sqlcipher");
            Assert.Contains(probe.Events, entry => entry.Phase == "reader-executing" && entry.CommandId is not null);
            Assert.Contains(probe.Events, entry => entry.Phase == "reader-disposing-before-release");
            Assert.Contains(probe.Events, entry => entry.Phase == "closing-before-provider-cleanup");
            Assert.Empty(probe.Failures);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
