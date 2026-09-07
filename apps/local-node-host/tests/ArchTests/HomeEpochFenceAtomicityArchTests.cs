using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialAr.Data;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Tests.Entities; // NodeTestActiveTeam

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// <b>HomeEpochFenceAtomicityArchTests</b> — the <b>G-4 arch fence</b> (code-reviewer verdict earlier repository ticket #1365
/// Finding 1). The load-bearing guarantee the security verdict makes the binding pre-condition for the MD-3
/// doctype flip is that <em>the home-epoch fence read and the invariant-bearing effect commit are ATOMIC</em>
/// — they run inside ONE explicit transaction whose write lock is held across the read, so a concurrent
/// promotion cannot commit a higher epoch in the gap (no TOCTOU). This arch-test guards that property against
/// regression in two complementary ways:
/// <list type="number">
/// <item><b>Structural single-source.</b> All fenced sites (<c>NodeEfJournalStore.SaveAtomicAsync</c>,
/// <c>NodeEfInvoiceNumberingService.NextNumberAsync</c>, and <c>NodeEfGrantStore</c>) route their fenced
/// unit-of-work through the SINGLE <see cref="HomeEpochFenceTransaction"/> helper (no second, drift-prone
/// copy of the transaction open/commit — anti-pattern A4), and that helper opens a <c>BEGIN IMMEDIATE</c>
/// transaction
/// (<c>deferred: false</c>) — NOT a plain/DEFERRED begin, which would not hold the write lock before the
/// read.</item>
/// <item><b>Behavioural lock-held.</b> While a fenced write is mid-flight (paused inside its
/// read-through-write window), a concurrent writer on a SEPARATE connection is genuinely BLOCKED
/// (<c>SQLITE_BUSY</c>) — proving the explicit transaction really holds the write lock across the fence read,
/// not merely that the source mentions a transaction.</item>
/// </list>
/// </summary>
// Shares the process-global HomeEpochFence.AfterReadHookForTests with HomeEpochFenceTests; this collection
// serializes the two so xUnit never runs them in parallel and they cannot race on that static hook.
[Collection(HomeEpochFenceStaticHookCollection.Name)]
public sealed class HomeEpochFenceAtomicityArchTests
{
    // ── (1) STRUCTURAL: single-source BEGIN IMMEDIATE, referenced by both fenced sites ──────────────────

    [Fact(DisplayName = "G-4 arch: the BEGIN IMMEDIATE fence transaction is a SINGLE source (HomeEpochFenceTransaction) using deferred:false, referenced by ALL fenced sites")]
    public void FenceTransaction_IsSingleSource_BeginImmediate_ReferencedByBothSites()
    {
        var root = LocateHostSourceRoot();

        // The single source: HomeEpochFenceTransaction opens BEGIN IMMEDIATE (deferred:false), the only place
        // the explicit fence transaction is constructed.
        var helper = File.ReadAllText(Path.Combine(root, "Data", "HomeEpoch", "HomeEpochFenceTransaction.cs"));
        Assert.Contains("deferred: false", helper); // BEGIN IMMEDIATE — write lock taken before the fence read
        Assert.Contains("BeginTransaction(", helper);
        // Guard against silently reverting to a DEFERRED/lazy begin (the bug — write lock taken too late).
        Assert.DoesNotContain("deferred: true", helper);

        // Every fenced site must drive its fenced unit-of-work through that ONE helper (no drift copy).
        foreach (var site in new[]
                 {
                     Path.Combine(root, "Data", "Financial", "NodeEfJournalStore.cs"),
                     Path.Combine(root, "Data", "Financial", "NodeEfInvoiceNumberingService.cs"),
                     Path.Combine(root, "Data", "Search", "Vector", "NodeEfGrantStore.cs"),
                 })
        {
            var text = File.ReadAllText(site);
            Assert.True(
                text.Contains("HomeEpochFenceTransaction.RunAsync"),
                $"Fenced site '{Path.GetFileName(site)}' must run its fenced unit-of-work through " +
                $"HomeEpochFenceTransaction.RunAsync (the single BEGIN IMMEDIATE source) so the fence read " +
                $"holds the write lock — otherwise the read runs in its own autocommit statement and the " +
                $"G-4 TOCTOU re-opens (verdict shipyard#1365 Finding 1).");

            // And neither site may open its OWN ad-hoc transaction for the fence (that would be the A4 drift
            // the single-source helper exists to prevent). EF's BeginTransactionAsync has no deferred flag
            // and would issue a DEFERRED begin — banned for the fence path.
            Assert.DoesNotContain(".BeginTransaction(", text);
            Assert.DoesNotContain("Database.BeginTransactionAsync", text);
        }
    }

    // ── (2) BEHAVIOURAL: the write lock is genuinely held across the fence read ──────────────────────────

    [Fact(DisplayName = "G-4 arch (behavioural): while a fenced JE post is inside its read-through-write window, a concurrent writer is BLOCKED (the BEGIN IMMEDIATE write lock is really held across the fence read)")]
    public async Task FencedWrite_HoldsWriteLock_AcrossFenceRead_ConcurrentWriterIsBlocked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harborline-g4-archfence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var connectionString = $"Data Source={Path.Combine(dir, "g4.db")};Pooling=False";
        try
        {
            var services = new ServiceCollection();
            services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
            services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
            services.AddSingleton<IHarborlineEntityModule, ArEntityModule>();
            services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
            services.AddSingleton<IHarborlineEntityModule, HomeEpochEntityModule>();
            services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
            var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();

            await using (var ctx = await factory.CreateDbContextAsync())
            {
                await ctx.Database.EnsureCreatedAsync();
            }

            var store = new NodeEfJournalStore(
                factory,
                NodeJournalWriteAdapters.Create(homeEpoch: new HomeEpochFenceEnlister()));

            // While the fenced write is paused INSIDE its read-through-write window (the hook), an
            // independent writer on a SEPARATE connection tries to write with a 0ms busy timeout. If the
            // fenced write genuinely holds the write lock (the fix), this concurrent write fails-fast with
            // SQLITE_BUSY. If it does NOT (the bug — autocommit read), the concurrent write succeeds.
            var concurrentWriterWasBlocked = false;
            HomeEpochFence.AfterReadHookForTests = async () =>
            {
                // Schema-independent probe: try to take the WRITE lock on a SEPARATE connection with no wait
                // (busy_timeout=0) via BEGIN IMMEDIATE. If the fenced write genuinely holds the write lock
                // across its fence read (the fix), this fails-fast with SQLITE_BUSY. If it does NOT (the bug
                // — the fence read ran in its own autocommit statement and released its lock), this BEGIN
                // IMMEDIATE succeeds.
                await using var raw = new SqliteConnection(connectionString + ";Pooling=False");
                await raw.OpenAsync();
                await using (var pragma = raw.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA busy_timeout=0;"; // do not wait — surface the contention now
                    await pragma.ExecuteNonQueryAsync();
                }
                await using var cmd = raw.CreateCommand();
                cmd.CommandText = "BEGIN IMMEDIATE;"; // acquire the write/RESERVED lock NOW
                try
                {
                    await cmd.ExecuteNonQueryAsync();
                    // Got the write lock — the fenced write was NOT holding it (the bug). Release it.
                    await using var rollback = raw.CreateCommand();
                    rollback.CommandText = "ROLLBACK;";
                    await rollback.ExecuteNonQueryAsync();
                    concurrentWriterWasBlocked = false;
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 5 /* SQLITE_BUSY */)
                {
                    concurrentWriterWasBlocked = true;  // blocked — the BEGIN IMMEDIATE write lock IS held
                }
            };

            try
            {
                var tenant = ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);
                using (HomeEpochWriteScope.Enter(
                    new PendingHomeEpochAssertion(tenant.Value, AssertedEpoch: 1, "device-1")))
                {
                    // No home epoch is established, so the fence read returns null and the write COMMITS —
                    // but the read still happens inside the BEGIN IMMEDIATE transaction, which is exactly the
                    // window the hook probes. (We only care that the lock is held during the read.)
                    await store.SaveAtomicForTestAsync(tenant, BalancedPosted(tenant, "JE-G4-ARCH", 10m, "invoice:g4"));
                }
            }
            finally
            {
                HomeEpochFence.AfterReadHookForTests = null;
            }

            Assert.True(concurrentWriterWasBlocked,
                "G-4 atomicity violated: a concurrent writer was NOT blocked while a fenced write was inside " +
                "its read-through-write window — the fence read does not run under a held write lock, so the " +
                "TOCTOU is open (verdict shipyard#1365 Finding 1). The fenced path must run inside the " +
                "BEGIN IMMEDIATE transaction (HomeEpochFenceTransaction).");
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static JournalEntry BalancedPosted(TenantId tenant, string id, decimal amount, string sourceReference) =>
        new JournalEntry(
            id: new JournalEntryId(id),
            tenantId: tenant,
            entryDate: new DateOnly(2026, 6, 16),
            memo: "g4 arch-fence test",
            lines: new System.Collections.Generic.List<JournalEntryLine>
            {
                new(new GLAccountId("1100"), amount, 0m),
                new(new GLAccountId("4000"), 0m, amount),
            },
            createdAtUtc: new Instant(DateTimeOffset.UtcNow),
            sourceReference: sourceReference)
        {
            SourceKind = JournalEntrySource.Invoice,
            Status = JournalEntryStatus.Posted,
        };

    /// <summary>Walks up from the test assembly to the local-node-host source root.</summary>
    private static string LocateHostSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Harborline.LocalNodeHost.csproj");
            if (File.Exists(candidate)) return dir.FullName;
            var sibling = Path.Combine(dir.FullName, "apps", "local-node-host", "Harborline.LocalNodeHost.csproj");
            if (File.Exists(sibling)) return Path.GetDirectoryName(sibling)!;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the local-node-host source root from " + AppContext.BaseDirectory);
    }
}
