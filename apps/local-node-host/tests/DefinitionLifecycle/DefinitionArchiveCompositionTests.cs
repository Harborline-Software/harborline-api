using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Capabilities;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Harborline.Blocks.BuilderDefinitions;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Definitions;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.DefinitionLifecycle;

public sealed class DefinitionArchiveCompositionTests
{
    internal const string ChildDirectoryVariable = "HARBORLINE_T305_ARCHIVE_CHILD_DIRECTORY";
    internal const string ChildStateVariable = "HARBORLINE_T305_ARCHIVE_CHILD_STATE";

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ArchiveMarkAndAudit_RollBackTogether(bool archive, bool auditFault)
    {
        using var database = new DefinitionArchiveDatabase();
        using var definitions = new ArchiveDefinitions();
        await definitions.SeedAsync("same", ArchiveDefinitions.TenantA);
        using var writer = database.Writer(TestRouteGate.AllowAll());
        if (!archive) await writer.SetFormArchivedAsync(definitions.Forms, "same", true, ArchiveDefinitions.Authority());
        var before = await database.OutboxRowsAsync();
        var target = auditFault ? "INSERT ON search_audit_outbox"
            : archive ? "INSERT ON definition_archive_marks" : "DELETE ON definition_archive_marks";
        await database.ExecuteAsync($"CREATE TRIGGER archive_fault BEFORE {target} BEGIN SELECT RAISE(ABORT, 'archive staging failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await writer.SetFormArchivedAsync(definitions.Forms, "same", archive, ArchiveDefinitions.Authority()));
        await using var db = database.Main.CreateDbContext();
        Assert.Equal(archive ? 0 : 1, await db.Set<DefinitionArchiveMark>().CountAsync());
        Assert.Equal(before.Select(row => row.AuditId), (await database.OutboxRowsAsync()).Select(row => row.AuditId));
    }

    [Theory]
    [InlineData(true, "DefinitionArchived")]
    [InlineData(false, "DefinitionUnarchived")]
    public async Task DeliveryFailure_PreservesCommittedState_AndReopenDeliversOnce(bool archive, string eventType)
    {
        using var database = new DefinitionArchiveDatabase();
        using var definitions = new ArchiveDefinitions();
        await definitions.SeedAsync("same", ArchiveDefinitions.TenantA);
        using var writer = database.Writer(TestRouteGate.AllowAll());
        if (!archive) await writer.SetWorkflowArchivedAsync(definitions.Workflows, "same", true, ArchiveDefinitions.Authority());
        await database.ExecuteAsync("CREATE TRIGGER archive_delivery_fault BEFORE INSERT ON search_audit_trail BEGIN SELECT RAISE(ABORT, 'archive delivery failure'); END;");
        Assert.NotNull(await writer.SetWorkflowArchivedAsync(definitions.Workflows, "same", archive, ArchiveDefinitions.Authority()));
        var owed = Assert.Single(await database.OutboxRowsAsync(), row => row.PublishedAtUnixMs is null);
        Assert.Equal(eventType, owed.EventType);
        Assert.Equal("archive-actor", owed.Actor);
        Assert.Equal("aaaaaaaa-0305-0000-0000-000000000001", owed.TenantId);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), owed.OccurredAt);
        Assert.Equal("scheduling", owed.TargetKind);
        Assert.Equal("same", owed.TargetId);
        Assert.Contains("scheduling:author", owed.Act);
        Assert.Contains("\"kind\":\"Workflows\"", owed.BodyJson);
        Assert.Contains("\"definition_key\":\"same\"", owed.BodyJson);
        Assert.NotNull(owed.AuthoritySnapshotJson);
        await database.ExecuteAsync("DROP TRIGGER archive_delivery_fault;");
        using var reopened = new DefinitionArchiveDatabase(database.DirectoryPath, migrate: false);
        await using (var db = reopened.Main.CreateDbContext())
            Assert.Equal(archive ? 1 : 0, await db.Set<DefinitionArchiveMark>().CountAsync());
        Assert.Equal(1, await reopened.Outbox.DrainAsync());
        Assert.Equal(0, await reopened.Outbox.DrainAsync());
        var delivered = Assert.Single(await reopened.AuditsAsync(), record => record.EventType.Value == eventType);
        Assert.Equal(Guid.Parse(owed.AuditId), delivered.AuditId);
        Assert.Equal(new ActorId("archive-actor"), delivered.Actor);
        Assert.NotNull(delivered.AuthoritySnapshot);
        await writer.SetWorkflowArchivedAsync(definitions.Workflows, "same", archive, ArchiveDefinitions.Authority());
        Assert.Equal(archive ? 1 : 2, (await database.OutboxRowsAsync()).Count);
    }

    [Fact]
    public async Task CompositeKey_KeepsBothKindsAndBothTenantsIndependent()
    {
        using var database = new DefinitionArchiveDatabase();
        using var definitions = new ArchiveDefinitions();
        using var writer = database.Writer(TestRouteGate.AllowAll());
        foreach (var tenant in new[] { ArchiveDefinitions.TenantA, ArchiveDefinitions.TenantB })
        {
            await definitions.SeedAsync("same", tenant);
            await writer.SetFormArchivedAsync(definitions.Forms, "same", true, ArchiveDefinitions.Authority(tenant));
            await writer.SetWorkflowArchivedAsync(definitions.Workflows, "same", true, ArchiveDefinitions.Authority(tenant));
        }
        await using var db = database.Main.CreateDbContext();
        Assert.Equal(4, await db.Set<DefinitionArchiveMark>().CountAsync());
        await writer.SetFormArchivedAsync(definitions.Forms, "same", false, ArchiveDefinitions.Authority());
        var remaining = await db.Set<DefinitionArchiveMark>().AsNoTracking().ToListAsync();
        Assert.Equal(new[]
        {
            "aaaaaaaa-0305-0000-0000-000000000001/Workflows/same",
            "bbbbbbbb-0305-0000-0000-000000000002/Forms/same",
            "bbbbbbbb-0305-0000-0000-000000000002/Workflows/same",
        }, remaining.Select(row => $"{row.TenantId}/{row.Kind}/{row.DefinitionKey}").Order(StringComparer.Ordinal));
        Assert.Equal(5, (await database.OutboxRowsAsync()).Count);
    }

    [Fact]
    public async Task ConcurrentRepeats_AndOppositeCommands_PreserveEveryCommittedTransition()
    {
        using var database = new DefinitionArchiveDatabase();
        using var definitions = new ArchiveDefinitions();
        await definitions.SeedAsync("same", ArchiveDefinitions.TenantA);
        using var writer = database.Writer(TestRouteGate.AllowAll());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => writer.SetFormArchivedAsync(
            definitions.Forms, "same", true, ArchiveDefinitions.Authority()).AsTask()));
        Assert.Single(await database.OutboxRowsAsync());
        await Task.WhenAll(
            writer.SetFormArchivedAsync(definitions.Forms, "same", false, ArchiveDefinitions.Authority()).AsTask(),
            writer.SetWorkflowArchivedAsync(definitions.Workflows, "same", true, ArchiveDefinitions.Authority()).AsTask());
        await using var db = database.Main.CreateDbContext();
        var mark = Assert.Single(await db.Set<DefinitionArchiveMark>().ToListAsync());
        Assert.Equal(DefinitionKind.Workflows, mark.Kind);
        Assert.Equal(3, (await database.OutboxRowsAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealMigrations_CreateArchiveTable_FromEmptyAndPreviousSchema(bool upgrade)
    {
        using var database = new DefinitionArchiveDatabase(migrate: false);
        await using var db = database.Main.CreateDbContext();
        if (upgrade)
            await db.GetService<IMigrator>().MigrateAsync("20261003000000_AddFormSubmitProjectionDefinition");
        await db.Database.MigrateAsync();
        await using var search = database.Search.CreateDbContext();
        await search.Database.MigrateAsync();
        Assert.Contains("20261029000000_AddDefinitionArchiveMarks", await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Set<DefinitionArchiveMark>().ToListAsync());
        Assert.Empty(await search.AuditOutbox.ToListAsync());
        using var definitions = new ArchiveDefinitions();
        await definitions.SeedAsync("same", ArchiveDefinitions.TenantA);
        using var writer = database.Writer(TestRouteGate.AllowAll());
        await writer.SetFormArchivedAsync(definitions.Forms, "same", true, ArchiveDefinitions.Authority());
        Assert.Single(await db.Set<DefinitionArchiveMark>().ToListAsync());
        Assert.Single(await search.AuditOutbox.ToListAsync());
    }

    [Fact]
    public void ProductionComposition_RequiresOneWriterAndClosesItOverBothHostedEndpoints()
    {
        using var database = new DefinitionArchiveDatabase();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestAuthorizationGate();
        services.AddTestNodeForms();
        services.AddSingleton(database.Main);
        services.AddSingleton(database.Outbox);
        using var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<NodeDefinitionArchiveWriter>();
        Assert.Same(writer, provider.GetRequiredService<NodeDefinitionArchiveWriter>());
        Assert.Single(services, item => item.ServiceType == typeof(NodeDefinitionArchiveWriter));
        foreach (var type in new[] { typeof(HostedFormsApiEndpoint), typeof(HostedWorkflowDefinitionApiEndpoint) })
        {
            var parameter = Assert.Single(type.GetConstructors().Single().GetParameters(),
                item => item.ParameterType == typeof(NodeDefinitionArchiveWriter));
            Assert.False(parameter.IsOptional);
        }
        var missing = new ServiceCollection();
        missing.AddTestAuthorizationGate();
        missing.AddTestNodeForms();
        var archiveOnly = new ServiceCollection();
        ((IServiceCollection)archiveOnly).Add(Assert.Single(missing,
            item => item.ServiceType == typeof(NodeDefinitionArchiveWriter)));
        using var incomplete = archiveOnly.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        Assert.Throws<InvalidOperationException>(() => incomplete.GetRequiredService<NodeDefinitionArchiveWriter>());
    }

    [Fact]
    public async Task HostedEndpoints_CloseOverOuterWriter_AndServeArchiveOnTheRealInnerListener()
    {
        using var database = new DefinitionArchiveDatabase();
        using var definitions = new ArchiveDefinitions();
        await definitions.SeedAsync("same", ArchiveDefinitions.TenantA);
        using var active = new ArchiveActiveTeam();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestDesktopOperator();
        services.AddTestAuthorizationGate();
        services.AddTestNodeForms();
        services.AddSingleton<IActiveTeamAccessor>(active);
        services.AddSingleton(new NodeCallerSessionToken("archive-composition-token"));
        services.AddSingleton(database.Main);
        services.AddSingleton(database.Outbox);
        using var provider = services.BuildServiceProvider();
        var writer = provider.GetRequiredService<NodeDefinitionArchiveWriter>();
        await using var listener = new SharedHostedWebApp(provider,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }), new LocalNodeExecutableEndpointRegistry(),
            NullLogger<SharedHostedWebApp>.Instance, TimeProvider.System);
        listener.MapApiRoutes(app => Assert.Null(app.Services.GetService<NodeDefinitionArchiveWriter>()));
        var forms = new HostedFormsApiEndpoint(listener, Substitute.For<IFormEngine>(),
            Substitute.For<IFormCapabilityIssuer>(), Substitute.For<IFormCapabilityVerifier>(), active,
            definitions.Forms, new InMemorySchemaRegistry(TimeProvider.System), NullLogger<HostedFormsApiEndpoint>.Instance,
            writer, timeProvider: TimeProvider.System);
        var workflows = new HostedWorkflowDefinitionApiEndpoint(listener, definitions.Workflows, active,
            NullLogger<HostedWorkflowDefinitionApiEndpoint>.Instance, TimeProvider.System, writer);
        await forms.StartAsync(CancellationToken.None);
        await workflows.StartAsync(CancellationToken.None);
        await listener.StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(listener.SelectedUrl!) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "archive-composition-token");
            foreach (var family in new[] { "forms", "workflows" })
            {
                var route = $"/api/local-node/{family}/definitions";
                Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(route + "/same/archive", null)).StatusCode);
                Assert.Empty((await client.GetFromJsonAsync<JsonElement>(route)).EnumerateArray());
                Assert.Single((await client.GetFromJsonAsync<JsonElement>(route + "?includeArchived=1")).EnumerateArray());
                Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(route + "/same/unarchive", null)).StatusCode);
                Assert.Single((await client.GetFromJsonAsync<JsonElement>(route)).EnumerateArray());
            }
            Assert.Equal(4, (await database.OutboxRowsAsync()).Count);
        }
        finally { await listener.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(true, "DefinitionArchived")]
    [InlineData(false, "DefinitionUnarchived")]
    public async Task KilledProcess_AfterCommit_FreshStartupDrainsExactlyOnce(bool archive, string eventType)
    {
        using var database = new DefinitionArchiveDatabase();
        var ready = Path.Combine(database.DirectoryPath, "ready");
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(DefinitionArchiveCompositionTests).Assembly.Location);
        start.Environment[ChildDirectoryVariable] = database.DirectoryPath;
        start.Environment[ChildStateVariable] = archive ? "archive" : "unarchive";
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Archive child did not start.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!File.Exists(ready))
            {
                if (child.HasExited) Assert.Fail("Archive child exited before commit: " + await stdout + await stderr);
                Assert.True(DateTime.UtcNow < deadline, "Archive child did not commit within 120 seconds.");
                await Task.Delay(50);
            }
            var owed = Assert.Single(await database.OutboxRowsAsync(), row => row.PublishedAtUnixMs is null);
            Assert.Equal(eventType, owed.EventType);
            await using (var db = database.Main.CreateDbContext())
                Assert.Equal(archive ? 1 : 0, await db.Set<DefinitionArchiveMark>().CountAsync());
            child.Kill(entireProcessTree: true);
            using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await child.WaitForExitAsync(exit.Token);
            Assert.NotEqual(4, child.ExitCode);
            using var reopened = new DefinitionArchiveDatabase(database.DirectoryPath, migrate: false);
            using var daemon = new NodeAuditOutboxDrainDaemon(reopened.Outbox, TimeProvider.System,
                NullLogger<NodeAuditOutboxDrainDaemon>.Instance);
            await daemon.StartAsync(CancellationToken.None);
            try
            {
                deadline = DateTime.UtcNow.AddSeconds(60);
                while ((await reopened.OutboxRowsAsync()).Any(row => row.PublishedAtUnixMs is null))
                {
                    Assert.True(DateTime.UtcNow < deadline, "Startup did not drain the committed archive audit.");
                    await Task.Delay(50);
                }
            }
            finally { await daemon.StopAsync(CancellationToken.None); }
            var delivered = Assert.Single(await reopened.AuditsAsync(), record => record.EventType.Value == eventType);
            Assert.Equal(Guid.Parse(owed.AuditId), delivered.AuditId);
            Assert.Equal(new ActorId("archive-actor"), delivered.Actor);
            Assert.Equal(new TenantId("aaaaaaaa-0305-0000-0000-000000000001"), delivered.TenantId);
            Assert.Equal(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), delivered.OccurredAt);
            Assert.Equal("same", delivered.Target!.Value.RecordId);
            Assert.Equal("forms", delivered.Target!.Value.RecordKind);
            Assert.Equal("Forms", delivered.Payload.Payload.Body["kind"]?.ToString());
            Assert.Equal("same", delivered.Payload.Payload.Body["definition_key"]?.ToString());
            Assert.NotNull(delivered.AuthoritySnapshot);
            using var second = new DefinitionArchiveDatabase(database.DirectoryPath, migrate: false);
            Assert.Equal(0, await second.Outbox.DrainAsync());
            Assert.Single(await second.AuditsAsync(), record => record.EventType.Value == eventType);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await child.WaitForExitAsync(exit.Token);
            }
        }
    }

    internal static async Task RunChildAsync(string directory)
    {
        using var database = new DefinitionArchiveDatabase(directory, migrate: false);
        using var definitions = new ArchiveDefinitions();
        await definitions.SeedAsync("same", ArchiveDefinitions.TenantA);
        var archive = Environment.GetEnvironmentVariable(ChildStateVariable) == "archive";
        if (!archive)
        {
            using var setup = database.Writer(TestRouteGate.AllowAll());
            await setup.SetFormArchivedAsync(definitions.Forms, "same", true, ArchiveDefinitions.Authority());
        }
        using var writer = database.Writer(TestRouteGate.AllowAll(), new StopBeforeReact(Path.Combine(directory, "ready")));
        await writer.SetFormArchivedAsync(definitions.Forms, "same", archive, ArchiveDefinitions.Authority());
        throw new InvalidOperationException("Archive child returned without stopping after commit.");
    }

    private sealed class StopBeforeReact(string ready) : IWritePipelineObserver
    {
        public void OnStage(WritePipelineStage stage)
        {
            if (stage != WritePipelineStage.React) return;
            File.WriteAllText(ready, "archive commit completed; react has not run");
            Thread.Sleep(TimeSpan.FromMinutes(5));
            Environment.Exit(4);
        }
    }
}

internal static class DefinitionArchiveChild
{
    [ModuleInitializer]
    internal static void RunWhenChild()
    {
        var directory = Environment.GetEnvironmentVariable(DefinitionArchiveCompositionTests.ChildDirectoryVariable);
        if (string.IsNullOrEmpty(directory)) return;
        try { DefinitionArchiveCompositionTests.RunChildAsync(directory).GetAwaiter().GetResult(); }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Environment.Exit(3);
        }
    }
}

internal sealed class ArchiveContextFactory<T>(Func<T> create) : IDbContextFactory<T> where T : DbContext
{
    public T CreateDbContext() => create();
}

internal sealed class DefinitionArchiveDatabase : IDisposable
{
    private readonly bool _ownsDirectory;
    private static int _providerBound;
    public string DirectoryPath { get; }
    public IDbContextFactory<LocalNodeDbContext> Main { get; }
    public IDbContextFactory<NodeLocalSearchDbContext> Search { get; }
    public NodeAuditOutbox Outbox { get; }
    private readonly AuthorityCapturingAuditTrail _trail;

    public DefinitionArchiveDatabase(string? directory = null, bool migrate = true)
    {
        if (Interlocked.Exchange(ref _providerBound, 1) == 0)
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());
        _ownsDirectory = directory is null;
        DirectoryPath = directory ?? Path.Combine(Path.GetTempPath(), "harborline-archive-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DirectoryPath, "local-node.db"), Password = "archive-fixture-key", Pooling = false,
        }.ToString();
        Main = new ArchiveContextFactory<LocalNodeDbContext>(() => new(
            new DbContextOptionsBuilder<LocalNodeDbContext>().UseSqlite(connection).Options,
            LocalNodePatternAModuleCatalog.CreateModules()));
        Search = new ArchiveContextFactory<NodeLocalSearchDbContext>(() => new(
            new DbContextOptionsBuilder<NodeLocalSearchDbContext>().UseSqlite(connection,
                sqlite => sqlite.MigrationsHistoryTable(NodeLocalSearchDbContext.MigrationsHistoryTableName)).Options));
        if (migrate)
        {
            using var main = Main.CreateDbContext();
            main.Database.Migrate();
            using var search = Search.CreateDbContext();
            search.Database.Migrate();
        }
        _trail = new AuthorityCapturingAuditTrail(new NodeAuditTrailStore(Search));
        Outbox = new NodeAuditOutbox(Search, _trail, _trail, new Ed25519Signer(KeyPair.Generate()),
            TimeProvider.System, NullLogger<NodeAuditOutbox>.Instance);
    }

    public NodeDefinitionArchiveWriter Writer(AuthorizationGate gate, IWritePipelineObserver? observer = null) =>
        new(Main, gate, Outbox, observer);

    public async Task<List<AuditOutboxRow>> OutboxRowsAsync()
    {
        await using var db = Search.CreateDbContext();
        return await db.AuditOutbox.AsNoTracking().OrderBy(row => row.AuditId).ToListAsync();
    }

    public async Task<List<AuditRecord>> AuditsAsync()
    {
        var records = new List<AuditRecord>();
        await foreach (var record in _trail.QueryAsync(new AuditQuery(ArchiveDefinitions.TenantA))) records.Add(record);
        return records;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var db = Search.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    public void Dispose()
    {
        Outbox.Dispose();
        if (!_ownsDirectory) return;
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var directory = Path.GetFullPath(DirectoryPath);
        if (Path.GetDirectoryName(directory) != root || !Path.GetFileName(directory).StartsWith("harborline-archive-test-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside this archive fixture.");
        Directory.Delete(directory, recursive: true);
    }
}

internal static class DefinitionArchiveTestComposition
{
    internal static IServiceCollection AddTestDefinitionArchives(this IServiceCollection services)
    {
        services.AddSingleton(_ => new DefinitionArchiveDatabase());
        services.AddSingleton(sp => sp.GetRequiredService<DefinitionArchiveDatabase>().Writer(
            sp.GetRequiredService<AuthorizationGate>(), sp.GetService<IWritePipelineObserver>()));
        return services;
    }
}
