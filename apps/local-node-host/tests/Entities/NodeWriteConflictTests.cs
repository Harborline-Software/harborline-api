using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// OBS-2 — unit tests for <see cref="NodePersistenceConflict.IsDuplicate"/>, the classifier the node
/// write routes (JE / bill / invoice / payment) use to map a recoverable-store unique-constraint race
/// to a 409 Conflict instead of a 500. Validates the classifier against a REAL SQLite UNIQUE-constraint
/// failure — both the EF-wrapped <see cref="DbUpdateException"/> form (how a route actually sees it) and
/// the bare <see cref="SqliteException"/> form — plus negative cases.
/// </summary>
public sealed class NodeWriteConflictTests : IAsyncLifetime
{
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodeEfJournalStore _store = null!;

    private static readonly TenantId LocalTenantId = new("local");

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-write-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "conflict-test.db")};Pooling=False";

        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.LocalNodeHost.Data.Audit.AuditEventEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        var provider = services.BuildServiceProvider();

        _factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }
        _store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create());
    }

    public async Task DisposeAsync()
    {
        await Task.CompletedTask;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact(DisplayName = "OBS-2: a real EF-wrapped SQLite UNIQUE-constraint failure is classified as a unique violation")]
    public async Task IsUniqueViolation_WrappedDbUpdateException_True()
    {
        const string sourceRef = "obs2-dup-key";

        // First posted JE with a non-null SourceReference — lands on ux_journal_entries_tenant_source_ref.
        await _store.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-OBS2-1", 100m, sourceRef));

        // Second JE with the SAME (tenant, SourceReference) — the unique index rejects it; EF surfaces
        // a DbUpdateException wrapping the SqliteException (exactly what a route's catch sees).
        var ex = await Assert.ThrowsAsync<DbUpdateException>(
            () => _store.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-OBS2-2", 100m, sourceRef)));

        Assert.True(NodePersistenceConflict.IsDuplicate(ex),
            "an EF-wrapped SQLite UNIQUE-constraint failure must be classified as a unique violation (→ 409)");
    }

    [Fact(DisplayName = "OBS-2: a bare SqliteException UNIQUE-constraint failure is classified as a unique violation")]
    public void IsUniqueViolation_BareSqliteException_True()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t (k TEXT); CREATE UNIQUE INDEX ux_t_k ON t(k);";
            create.ExecuteNonQuery();
        }
        using (var first = connection.CreateCommand())
        {
            first.CommandText = "INSERT INTO t (k) VALUES ('x');";
            first.ExecuteNonQuery();
        }

        var ex = Assert.Throws<SqliteException>(() =>
        {
            using var dup = connection.CreateCommand();
            dup.CommandText = "INSERT INTO t (k) VALUES ('x');";
            dup.ExecuteNonQuery();
        });

        Assert.True(NodePersistenceConflict.IsDuplicate(ex),
            "a bare SQLite UNIQUE-constraint SqliteException must be classified as a unique violation");
    }

    [Fact(DisplayName = "OBS-2: a non-constraint exception is NOT classified as a unique violation")]
    public void IsUniqueViolation_UnrelatedException_False()
    {
        Assert.False(NodePersistenceConflict.IsDuplicate(new InvalidOperationException("boom")));
        Assert.False(NodePersistenceConflict.IsDuplicate(new DbUpdateException("save failed", new TimeoutException())));
        Assert.False(NodePersistenceConflict.IsDuplicate(null));
    }

    private static JournalEntry BalancedPosted(string id, decimal amount, string? sourceReference) =>
        new JournalEntry(
            id: new JournalEntryId(id),
            tenantId: LocalTenantId,
            entryDate: new DateOnly(2026, 6, 17),
            memo: "obs-2 unique-violation classifier test",
            lines: new List<JournalEntryLine>
            {
                new(new GLAccountId("1000"), amount, 0m),
                new(new GLAccountId("4000"), 0m, amount),
            },
            createdAtUtc: new Instant(DateTimeOffset.UtcNow),
            sourceReference: sourceReference)
        {
            Status = JournalEntryStatus.Posted,
        };
}
