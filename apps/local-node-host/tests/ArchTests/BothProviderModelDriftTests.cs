using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Harborline.Api.Blocks.FinancialAp.Data;
using Harborline.Api.Blocks.FinancialAr.Data;
using Harborline.Api.Blocks.Banking.Data;
using Harborline.Api.Blocks.Docs.Data;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Blocks.FinancialPayments.Data;
using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Council condition C2 (ADR 0114): every shared EF entity module must compile and
/// produce a correct, non-diverged model on BOTH the Npgsql (Bridge) and the SQLite
/// (local-node-host) providers.
/// </summary>
/// <remarks>
/// <para>
/// The test builds the EF model twice — once against a Npgsql provider and once
/// against a SQLite provider — using the same set of <see cref="IHarborlineEntityModule"/>
/// instances. It then asserts:
/// <list type="number">
///   <item>Both models contain the same set of entity types (no type lost in the
///     post-config sweep).</item>
///   <item>Both models contain the same set of column names per entity type (no
///     column renamed or dropped in the sweep).</item>
///   <item>jsonb columns in the Npgsql model are rewritten to TEXT on SQLite;
///     bytea columns are rewritten to BLOB on SQLite.</item>
///   <item>No remaining PG-quoted filter predicates (double-quoted identifiers)
///     survive on the SQLite model.</item>
/// </list>
/// </para>
/// <para>
/// This test does NOT run migrations or touch a real database. It is a pure
/// model-compilation and metadata-inspection test.
/// </para>
/// </remarks>
public sealed class BothProviderModelDriftTests
{
    // ── Shared module set ──────────────────────────────────────────────────

    private static IReadOnlyList<IHarborlineEntityModule> AllModules() =>
    [
        new FinancialLedgerEntityModule(),
        new Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule(),
        new ArEntityModule(),
        new ApEntityModule(),
        new PaymentsEntityModule(),
        new BankingEntityModule(),
        new PeopleEntityModule(),
        new DocsEntityModule(),
    ];

    // ── Model builders ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds the EF model using the SQLite provider (the local-node-host path).
    /// </summary>
    private static IModel BuildSqliteModel()
    {
        var modules = AllModules();
        var options = new DbContextOptionsBuilder<LocalNodeDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var ctx = new LocalNodeDbContext(options, modules);
        return ctx.Model;
    }

    /// <summary>
    /// Builds the EF model using the Npgsql provider (the Bridge path).
    /// This mirrors what <c>DesignTimeDbContextFactory</c> in signal-bridge does.
    /// </summary>
    private static IModel BuildNpgsqlModel()
    {
        var modules = AllModules();
        // Use a fake connection string — Npgsql reads it only to determine the
        // server version; model compilation doesn't open a real connection.
        var options = new DbContextOptionsBuilder<NpgsqlProbeContext>()
            .UseNpgsql("Host=localhost;Database=probe;Username=probe;Password=probe")
            .Options;
        using var ctx = new NpgsqlProbeContext(options, modules);
        return ctx.Model;
    }

    // ── Tests ──────────────────────────────────────────────────────────────

    [Fact(DisplayName = "C2: shared modules compile on both SQLite and Npgsql providers")]
    public void SharedModules_CompileOnBothProviders()
    {
        // This test simply asserts that both model builds succeed (no exception).
        var sqliteModel = BuildSqliteModel();
        var npgsqlModel = BuildNpgsqlModel();

        Assert.NotNull(sqliteModel);
        Assert.NotNull(npgsqlModel);
    }

    [Fact(DisplayName = "C2: both-provider models contain the same entity types")]
    public void BothModels_ContainSameEntityTypes()
    {
        var sqliteModel = BuildSqliteModel();
        var npgsqlModel = BuildNpgsqlModel();

        // Compare by CLR type FullName — stable across providers.
        var sqliteTypes = sqliteModel.GetEntityTypes()
            .Select(e => e.ClrType.FullName!)
            .Order()
            .ToList();

        var npgsqlTypes = npgsqlModel.GetEntityTypes()
            .Select(e => e.ClrType.FullName!)
            .Order()
            .ToList();

        Assert.Equal(npgsqlTypes, sqliteTypes);
    }

    [Fact(DisplayName = "C2: both-provider models contain the same columns per entity type")]
    public void BothModels_ContainSameColumnsPerEntityType()
    {
        var sqliteModel = BuildSqliteModel();
        var npgsqlModel = BuildNpgsqlModel();

        var drifts = new List<string>();

        foreach (var npgsqlEntity in npgsqlModel.GetEntityTypes())
        {
            var sqliteEntity = sqliteModel.FindEntityType(npgsqlEntity.ClrType);
            if (sqliteEntity is null)
            {
                drifts.Add($"{npgsqlEntity.ClrType.Name}: entity missing from SQLite model");
                continue;
            }

            // Use property CLR name (stable across providers) to detect added/missing properties.
            var npgsqlCols = npgsqlEntity.GetProperties()
                .Select(p => p.Name)
                .Order()
                .ToList();

            var sqliteCols = sqliteEntity.GetProperties()
                .Select(p => p.Name)
                .Order()
                .ToList();

            if (!npgsqlCols.SequenceEqual(sqliteCols))
            {
                drifts.Add(
                    $"{npgsqlEntity.ClrType.Name}: property set differs.\n" +
                    $"  Npgsql:  {string.Join(", ", npgsqlCols)}\n" +
                    $"  SQLite:  {string.Join(", ", sqliteCols)}");
            }
        }

        if (drifts.Count > 0)
        {
            throw new InvalidOperationException(
                $"C2 column-drift detected ({drifts.Count} entities):\n\n" +
                string.Join("\n\n", drifts));
        }
    }

    [Fact(DisplayName = "C2: SQLite post-config sweep rewrites jsonb → TEXT")]
    public void SqliteModel_JsonbColumnsAreText()
    {
        var sqliteModel = BuildSqliteModel();
        var npgsqlModel = BuildNpgsqlModel();

        var violations = new List<string>();

        // Every jsonb column in the Npgsql model must be TEXT in the SQLite model.
        foreach (var npgsqlEntity in npgsqlModel.GetEntityTypes())
        {
            var sqliteEntity = sqliteModel.FindEntityType(npgsqlEntity.ClrType);
            if (sqliteEntity is null) continue;

            foreach (var npgsqlProp in npgsqlEntity.GetProperties())
            {
                if (!"jsonb".Equals(npgsqlProp.GetColumnType(), StringComparison.OrdinalIgnoreCase))
                    continue;

                var sqliteProp = sqliteEntity.FindProperty(npgsqlProp.Name);
                var sqliteColType = sqliteProp?.GetColumnType();

                if (!"TEXT".Equals(sqliteColType, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add(
                        $"{npgsqlEntity.ClrType.Name}.{npgsqlProp.Name}: " +
                        $"Npgsql=jsonb → SQLite={sqliteColType ?? "(null)"} (expected TEXT)");
                }
            }
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"C2 jsonb→TEXT sweep failed ({violations.Count} columns):\n" +
                string.Join("\n", violations));
        }
    }

    /// <summary>
    /// C2 bytea→BLOB sweep — <b>FORWARD-COVERAGE ONLY (currently a vacuous set).</b>
    /// </summary>
    /// <remarks>
    /// As of the ADR 0114/0115 cohort-1a.2 module set there are ZERO
    /// <c>HasColumnType("bytea")</c> columns owned by any shared entity module
    /// (the migration emits 358 TEXT, 0 BLOB; the only binary-ish column,
    /// <c>raw_provider_blob</c>, is a <c>string</c>→TEXT, and the fleet's one real
    /// bytea — <c>TenantRegistration.TeamPublicKey</c> — is configured inline in
    /// the Bridge control-plane DbContext, not contributed by a shared module, so
    /// it never reaches the local node). This assertion therefore iterates an
    /// EMPTY set today: it guards the sweep's bytea branch against a FUTURE
    /// module-owned bytea column, but proves nothing about the current model.
    /// Per the .NET-architect SPOT-CHECK (verdict 2026-06-13, condition 1) it is
    /// annotated as forward-coverage; the corrected tally is "358 TEXT, 0 BLOB".
    /// The explicit zero-column observation below makes the vacuity visible rather
    /// than silently green.
    /// </remarks>
    [Fact(DisplayName = "C2: SQLite post-config sweep rewrites bytea → BLOB (forward-coverage; 0 bytea columns today)")]
    public void SqliteModel_ByteaColumnsAreBlob()
    {
        var sqliteModel = BuildSqliteModel();
        var npgsqlModel = BuildNpgsqlModel();

        var violations = new List<string>();
        var byteaColumnsObserved = 0;

        // Every bytea column in the Npgsql model must be BLOB in the SQLite model.
        foreach (var npgsqlEntity in npgsqlModel.GetEntityTypes())
        {
            var sqliteEntity = sqliteModel.FindEntityType(npgsqlEntity.ClrType);
            if (sqliteEntity is null) continue;

            foreach (var npgsqlProp in npgsqlEntity.GetProperties())
            {
                if (!"bytea".Equals(npgsqlProp.GetColumnType(), StringComparison.OrdinalIgnoreCase))
                    continue;

                byteaColumnsObserved++;

                var sqliteProp = sqliteEntity.FindProperty(npgsqlProp.Name);
                var sqliteColType = sqliteProp?.GetColumnType();

                if (!"BLOB".Equals(sqliteColType, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add(
                        $"{npgsqlEntity.ClrType.Name}.{npgsqlProp.Name}: " +
                        $"Npgsql=bytea → SQLite={sqliteColType ?? "(null)"} (expected BLOB)");
                }
            }
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"C2 bytea→BLOB sweep failed ({violations.Count} columns):\n" +
                string.Join("\n", violations));
        }

        // Forward-coverage marker: the current module set owns NO bytea columns,
        // so this assertion is vacuous by design. If a future module introduces a
        // module-owned bytea column, this observed-count assertion will need to be
        // raised (and the assertion above starts doing real work). Documented as
        // a non-blocking C2 wording correction (verdict 2026-06-13, condition 1).
        Assert.Equal(0, byteaColumnsObserved);
    }

    [Fact(DisplayName = "C2: SQLite model has no PG-quoted filter predicates")]
    public void SqliteModel_HasNoPgQuotedIndexFilters()
    {
        var sqliteModel = BuildSqliteModel();

        var violations = new List<string>();

        foreach (var entityType in sqliteModel.GetEntityTypes())
        {
            foreach (var index in entityType.GetIndexes())
            {
                var filter = index.GetFilter();
                if (filter is not null && filter.Contains('"'))
                {
                    violations.Add(
                        $"{entityType.ClrType.Name} index {index.GetDatabaseName()}: " +
                        $"PG-quoted filter not stripped: {filter}");
                }
            }
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"C2 PG-quoted index filter found on SQLite model ({violations.Count}):\n" +
                string.Join("\n", violations));
        }
    }

    [Fact(DisplayName = "C2: payment reversal uniqueness filter is provider-consistent")]
    public void PaymentReversalUniqueIndex_FilterMatchesProvider()
    {
        const string indexName = "ux_payment_applications_tenant_reverses";
        const string npgsqlFilter = "\"ReversesApplicationId\" IS NOT NULL";

        var sqliteIndex = BuildSqliteModel()
            .FindEntityType(typeof(PaymentApplication))!
            .GetIndexes()
            .Single(index => index.GetDatabaseName() == indexName);
        var npgsqlIndex = BuildNpgsqlModel()
            .FindEntityType(typeof(PaymentApplication))!
            .GetIndexes()
            .Single(index => index.GetDatabaseName() == indexName);

        Assert.Null(sqliteIndex.GetFilter());
        Assert.Equal(npgsqlFilter, npgsqlIndex.GetFilter());
    }

    // ── Npgsql probe context ───────────────────────────────────────────────

    /// <summary>
    /// Minimal DbContext for Npgsql model-build. Does NOT reference SQLite.
    /// Accepts the same <see cref="IHarborlineEntityModule"/> set as
    /// <see cref="LocalNodeDbContext"/> but skips the SQLite post-config sweep.
    /// </summary>
    private sealed class NpgsqlProbeContext : DbContext
    {
        private readonly IEnumerable<IHarborlineEntityModule> _modules;

        public NpgsqlProbeContext(
            DbContextOptions<NpgsqlProbeContext> options,
            IEnumerable<IHarborlineEntityModule> modules) : base(options)
        {
            _modules = modules;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Apply every module — same as SignalBridgeDbContext, no sweep.
            foreach (var module in _modules)
            {
                module.Configure(modelBuilder);
            }
        }
    }
}
