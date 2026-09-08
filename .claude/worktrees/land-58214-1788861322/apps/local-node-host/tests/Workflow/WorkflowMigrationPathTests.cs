using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.FinancialAp.Data;
using Harborline.Api.Blocks.FinancialAr.Data;
using Harborline.Api.Blocks.Banking.Data;
using Harborline.Api.Blocks.Docs.Data;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Blocks.FinancialPayments.Data;
using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Workflow;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// ADR 0135 GATE A — the durable process-engine's 3 tables (<c>workflow_instances</c>,
/// <c>workflow_events</c>, <c>workflow_step_idempotency</c>) are created by the COMMITTED EF MIGRATION
/// via <see cref="RelationalDatabaseFacadeExtensions.MigrateAsync"/> — the PRODUCTION schema path — not
/// only by <c>EnsureCreatedAsync</c> (which the slice-1/2 tests used).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this test is load-bearing.</b> Slices 1+2 added the workflow model (the
/// <see cref="WorkflowEntityModule"/>) and exercised it with <c>EnsureCreatedAsync</c>, which materializes
/// the WHOLE runtime model regardless of the committed migrations. Production schema, however, is applied
/// by <c>LocalNodeStoreEncryptionGuard.StartAsync</c> calling <c>ctx.Database.MigrateAsync()</c> on
/// <see cref="LocalNodeDbContext"/> — which replays ONLY the committed migrations. Before the
/// <c>AddWorkflowEngineTables</c> migration existed, the runtime model carried the 3 tables (the
/// DI-registered module) but <c>MigrateAsync</c> never created them, so the engine would silently write
/// against absent tables in production. This test fails-closed against that regression: it proves the
/// migration creates the tables (and the load-bearing <c>(InstanceId, Seq)</c> unique index + the
/// <c>DefinitionVersion</c> column) — NOT <c>EnsureCreatedAsync</c>.
/// </para>
/// <para>
/// <b>Faithful module set.</b> The context is built with the SAME full module set the migrations were
/// scaffolded against (the <c>DesignTimeLocalNodeDbContextFactory</c> set), so <c>MigrateAsync</c>'s
/// model↔migrations consistency check passes — exactly as it does in production. The
/// <see cref="WorkflowEntityModule"/> is the last entry, mirroring the design-time factory.
/// </para>
/// <para>
/// <b>SQLite assertions read <c>sqlite_master</c></b> — the catalog the migration actually wrote — so the
/// proof is at the physical-schema level, not the EF model level.
/// </para>
/// </remarks>
public sealed class WorkflowMigrationPathTests : IAsyncLifetime
{
    private string _dir = null!;
    private string _dbPath = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "adr0135-gateA-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "local-node.db");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The exact module set the <c>DesignTimeLocalNodeDbContextFactory</c> scaffolds against. The test
    /// controls the connection string while sharing the module authority with migration generation.
    /// </summary>
    private static IHarborlineEntityModule[] FullModuleSet() =>
        DesignTimeLocalNodeDbContextFactory.CreateMigrationModules();

    private IDbContextFactory<LocalNodeDbContext> NewMigratableFactory()
    {
        var services = new ServiceCollection();
        foreach (var module in FullModuleSet())
        {
            services.AddSingleton(module);
        }
        services.AddDbContextFactory<LocalNodeDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        return services.BuildServiceProvider().GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
    }

    [Fact(DisplayName = "GATE A: MigrateAsync (the production schema path) creates all 3 workflow tables — NOT EnsureCreatedAsync")]
    public async Task MigrateAsync_CreatesTheThreeWorkflowTables()
    {
        var factory = NewMigratableFactory();

        // THE PRODUCTION PATH — replay the committed migrations onto a fresh file (no EnsureCreatedAsync).
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            await ctx.Database.MigrateAsync();
        }

        var tables = await ReadSqliteMasterAsync("table");

        Assert.Contains("workflow_instances", tables);
        Assert.Contains("workflow_events", tables);
        Assert.Contains("workflow_step_idempotency", tables);
    }

    [Fact(DisplayName = "GATE A: the migration's AddWorkflowEngineTables is in the applied set (the table is the migration's, not a model-sync artifact)")]
    public async Task MigrateAsync_AppliesTheWorkflowMigration()
    {
        var factory = NewMigratableFactory();
        await using var ctx = await factory.CreateDbContextAsync();
        await ctx.Database.MigrateAsync();

        var applied = await ctx.Database.GetAppliedMigrationsAsync();

        // The named migration is in the applied set, and there are NO pending migrations after MigrateAsync —
        // i.e. the committed migrations fully realize the model (the proof MigrateAsync, not the runtime model,
        // built the schema).
        Assert.Contains(applied, m => m.EndsWith("AddWorkflowEngineTables", StringComparison.Ordinal));
        Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
    }

    [Fact(DisplayName = "GATE A: MigrateAsync creates the (InstanceId, Seq) UNIQUE index + the DefinitionVersion column the D7/append-only invariants depend on")]
    public async Task MigrateAsync_CreatesTheUniqueIndexAndDefinitionVersion()
    {
        var factory = NewMigratableFactory();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            await ctx.Database.MigrateAsync();
        }

        // The append-only invariant (ADR 0135 D2): the (InstanceId, Seq) unique index on workflow_events.
        var indexes = await ReadSqliteMasterAsync("index");
        Assert.Contains("ux_workflow_events_instance_seq", indexes);

        // D7: the DefinitionVersion column on workflow_instances (the pinned-version schema shape).
        var columns = await ReadTableColumnsAsync("workflow_instances");
        Assert.Contains("DefinitionVersion", columns);
    }

    [Fact(DisplayName = "GATE A end-to-end: after MigrateAsync, the recoverable NodeEfWorkflowStore writes + reads an instance against the MIGRATED tables (no EnsureCreatedAsync anywhere)")]
    public async Task MigrateAsync_ThenStoreRoundTrips()
    {
        var factory = NewMigratableFactory();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            await ctx.Database.MigrateAsync();
        }

        // The REAL production store rides the migrated context — prove a create+advance lands on the
        // migration-created tables (this would throw "no such table" before Gate A).
        var store = new NodeEfWorkflowStore(factory);
        await store.CreateInstanceAsync(new WorkflowInstanceRecord
        {
            Id = "gateA-1",
            TenantId = "local",
            DefinitionKey = "recurring-generation",
            DefinitionVersion = "v1",
            CurrentStep = "generate@2026-07-01",
            Status = WorkflowStatus.Running,
            StateJson = "{}",
        });

        await store.AdvanceAsync(
            key: new WorkflowStepKey("gateA-1", iteration: 0, "generate@2026-07-01"),
            effect: null,
            resultJson: "{}",
            eventType: "OccurrenceGenerated",
            eventDataJson: "{}",
            nextStep: "generate@2026-07-01",
            nextStatus: WorkflowStatus.Running);

        var loaded = await store.LoadAsync("gateA-1");
        Assert.NotNull(loaded);

        // The append-only event row landed on the migrated workflow_events table.
        await using var ctx2 = await factory.CreateDbContextAsync();
        var eventCount = await ctx2.Set<WorkflowEventRecord>().CountAsync(e => e.InstanceId == "gateA-1");
        Assert.Equal(1, eventCount);
    }

    // ── sqlite_master / pragma helpers (physical-schema assertions) ────────────────

    private async Task<HashSet<string>> ReadSqliteMasterAsync(string type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = $type;";
        cmd.Parameters.AddWithValue("$type", type);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private async Task<HashSet<string>> ReadTableColumnsAsync(string table)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }
        return columns;
    }
}
