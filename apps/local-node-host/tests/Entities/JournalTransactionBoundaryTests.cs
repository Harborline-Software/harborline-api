using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// DES-0029 kernel-core-ck-6 at the journal posting boundary: atomicity, rollback, replay and the
/// reversal transition proven against the real <see cref="NodeEfJournalStore"/> over on-disk SQLite, with
/// faults injected at named SQL commands (the durable store's own rollback, not a fake port's).
/// </summary>
public sealed class JournalTransactionBoundaryTests : IAsyncLifetime
{
    private static readonly TenantId Tenant =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    private readonly CommandFault _fault = new();
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodeEfJournalStore _store = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-ck6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "ck6.db")};Pooling=False;Default Timeout=1";

        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString).AddInterceptors(_fault));
        _factory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create());
        await SeedAccountAsync("1000", "Cash", GLAccountType.Asset, AccountSubtype.BankAccount);
        await SeedAccountAsync("4000", "Revenue", GLAccountType.Revenue, AccountSubtype.OperatingIncome);
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "ck-6 atomicity: a fault on the audit insert leaves neither the journal row nor the audit row")]
    public async Task AuditInsertFault_PersistsNeitherRecordNorAudit()
    {
        _fault.FailOn = "INSERT INTO \"node_audit_events\"";

        await Assert.ThrowsAsync<DbUpdateException>(
            () => _store.SaveAtomicForTestAsync(Tenant, Posted("JE-CK6-FAULT", sourceReference: null)));

        Assert.True(_fault.Fired);
        Assert.Equal((0, 0), await CountsAsync());
    }

    [Fact(DisplayName = "ck-6 replay: losing the source-reference race returns the first posting without a second write")]
    public async Task ReplayLosingTheUniqueIndexRace_ReturnsTheFirstPosting()
    {
        const string sourceReference = "invoice:CK6-RACE";
        var racing = new RacingStore(_store, Posted("JE-CK6-WINNER", sourceReference));
        var posting = new JournalPostingService(
            new NodeEfAccountResolver(_factory),
            new NodeEfPeriodResolver(_factory),
            racing,
            TestAuthorization.AllowGate());

        var result = await posting.PostAsync(Draft("JE-CK6-LOSER", sourceReference), TestAuthorization.Write(Tenant));

        Assert.True(racing.Raced);
        Assert.True(result.IsSuccess);
        Assert.Equal("JE-CK6-WINNER", result.Entry!.Id.Value);
        Assert.Equal((1, 1), await CountsAsync());
    }

    [Fact(DisplayName = "ck-6 atomicity: a reversal commits the reversing entry and the original's transition together")]
    public async Task Reversal_TransitionsTheOriginalInTheSameSave()
    {
        await _store.SaveAtomicForTestAsync(Tenant, Posted("JE-CK6-ORIG", sourceReference: null));

        await _store.SaveAtomicForTestAsync(Tenant, Reversal("JE-CK6-REV", "JE-CK6-ORIG"));

        var original = await FindAsync("JE-CK6-ORIG");
        Assert.Equal(JournalEntryStatus.Reversed, original!.Status);
        Assert.Equal("JE-CK6-REV", original.ReversedBy?.Value);
        Assert.Equal((2, 2), await CountsAsync());
    }

    [Fact(DisplayName = "ck-6 rollback: a fault on the original's transition leaves no reversal, no audit and the original Posted")]
    public async Task ReversalTransitionFault_PersistsNothing()
    {
        await _store.SaveAtomicForTestAsync(Tenant, Posted("JE-CK6-ORIG", sourceReference: null));
        _fault.FailOn = "UPDATE \"journal_entries\"";

        await Assert.ThrowsAsync<DbUpdateException>(
            () => _store.SaveAtomicForTestAsync(Tenant, Reversal("JE-CK6-REV", "JE-CK6-ORIG")));

        Assert.True(_fault.Fired);
        Assert.Equal(JournalEntryStatus.Posted, (await FindAsync("JE-CK6-ORIG"))!.Status);
        Assert.Null(await FindAsync("JE-CK6-REV"));
        Assert.Equal((1, 1), await CountsAsync());
    }

    [Fact(DisplayName = "ck-6 replay: a second reversal of the same original is refused inside the transaction")]
    public async Task SecondReversal_IsRefusedWithoutASecondWrite()
    {
        await _store.SaveAtomicForTestAsync(Tenant, Posted("JE-CK6-ORIG", sourceReference: null));
        await _store.SaveAtomicForTestAsync(Tenant, Reversal("JE-CK6-REV-1", "JE-CK6-ORIG"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.SaveAtomicForTestAsync(Tenant, Reversal("JE-CK6-REV-2", "JE-CK6-ORIG")));

        Assert.Null(await FindAsync("JE-CK6-REV-2"));
        Assert.Equal("JE-CK6-REV-1", (await FindAsync("JE-CK6-ORIG"))!.ReversedBy?.Value);
        Assert.Equal((2, 2), await CountsAsync());
    }

    [Fact(DisplayName = "ck-6 replay: a concurrent reversal cannot commit between the original's read and the reversal's save")]
    public async Task ConcurrentReversal_IsExcludedFromTheReadToSaveWindow()
    {
        await _store.SaveAtomicForTestAsync(Tenant, Posted("JE-CK6-ORIG", sourceReference: null));
        Exception? competitorError = null;
        _fault.AfterReaderOn = "FROM \"journal_entries\"";
        _fault.AfterReader = async () =>
        {
            try { await _store.SaveAtomicForTestAsync(Tenant, Reversal("JE-CK6-REV-2", "JE-CK6-ORIG")); }
            catch (Exception ex) { competitorError = ex; }
        };

        await _store.SaveAtomicForTestAsync(Tenant, Reversal("JE-CK6-REV-1", "JE-CK6-ORIG"));

        Assert.Equal(5, Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(competitorError).SqliteErrorCode);
        Assert.Null(await FindAsync("JE-CK6-REV-2"));
        Assert.Equal("JE-CK6-REV-1", (await FindAsync("JE-CK6-ORIG"))!.ReversedBy?.Value);
        Assert.Equal((2, 2), await CountsAsync());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(int Journal, int Audit)> CountsAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return (
            await ctx.Set<JournalEntry>().CountAsync(e => e.TenantId == Tenant),
            await ctx.Set<NodeAuditEventRow>().CountAsync(r => r.TenantId == Tenant.Value));
    }

    private async Task<JournalEntry?> FindAsync(string id)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().AsNoTracking()
            .SingleOrDefaultAsync(e => e.TenantId == Tenant && e.Id == new JournalEntryId(id));
    }

    private static JournalEntry Draft(string id, string? sourceReference) =>
        new(
            id: new JournalEntryId(id),
            tenantId: Tenant,
            entryDate: new DateOnly(2026, 6, 16),
            memo: "ck-6 boundary",
            lines: new List<JournalEntryLine>
            {
                new(new GLAccountId("1000"), 40m, 0m),
                new(new GLAccountId("4000"), 0m, 40m),
            },
            createdAtUtc: new Instant(TestAuthorization.Write(Tenant).At),
            sourceReference: sourceReference);

    private static JournalEntry Posted(string id, string? sourceReference) =>
        Draft(id, sourceReference) with { Status = JournalEntryStatus.Posted };

    private static JournalEntry Reversal(string id, string originalId) =>
        new JournalEntry(
            id: new JournalEntryId(id),
            tenantId: Tenant,
            entryDate: new DateOnly(2026, 6, 17),
            memo: $"Reversal of {originalId}",
            lines: new List<JournalEntryLine>
            {
                new(new GLAccountId("1000"), 0m, 40m),
                new(new GLAccountId("4000"), 40m, 0m),
            },
            createdAtUtc: new Instant(TestAuthorization.Write(Tenant).At))
        {
            Status = JournalEntryStatus.Posted,
            SourceKind = JournalEntrySource.Reversal,
            ReversalOf = new JournalEntryId(originalId),
        };

    private async Task SeedAccountAsync(string code, string name, GLAccountType type, AccountSubtype subtype)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Set<GLAccount>().Add(GLAccount.Create(
            id: new GLAccountId(code),
            chartId: new ChartOfAccountsId("CH-1"),
            code: code,
            name: name,
            type: type,
            subtype: subtype,
            currency: "USD",
            isPostable: true,
            createdAtUtc: new Instant(TimeProvider.System.GetUtcNow())));
        await ctx.SaveChangesAsync();
    }

    /// <summary>Throws before the first SQL command containing <see cref="FailOn"/>; runs <see cref="AfterReader"/> once after a matching read.</summary>
    private sealed class CommandFault : DbCommandInterceptor
    {
        public string? FailOn { get; set; }

        public bool Fired { get; private set; }

        public string? AfterReaderOn { get; set; }

        public Func<Task>? AfterReader { get; set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (AfterReader is { } hook && AfterReaderOn is { } marker &&
                command.CommandText.Contains(marker, StringComparison.Ordinal))
            {
                AfterReader = null;
                await hook();
            }

            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Check(DbCommand command)
        {
            if (FailOn is { } marker && command.CommandText.Contains(marker, StringComparison.Ordinal))
            {
                FailOn = null;
                Fired = true;
                throw new InvalidOperationException($"ck-6 injected fault before: {marker}");
            }
        }
    }

    /// <summary>
    /// A real-store decorator that lets a competing posting with the same source reference commit between the
    /// posting service's pre-write lookup and its save: the race the unique index is the backstop for.
    /// </summary>
    private sealed class RacingStore(NodeEfJournalStore inner, JournalEntry competitor) : IJournalStore
    {
        public bool Raced { get; private set; }

        public Task SaveAtomicAsync(
            TenantId tenantId,
            JournalEntry entry,
            AuthorizationDecision decision,
            CancellationToken cancellationToken = default) =>
            inner.SaveAtomicAsync(tenantId, entry, decision, cancellationToken);

        public IReadOnlyList<JournalEntry> Snapshot(TenantId tenantId) => inner.Snapshot(tenantId);

        public async Task<JournalEntry?> FindBySourceReferenceAsync(
            TenantId tenantId,
            string sourceReference,
            CancellationToken cancellationToken = default)
        {
            if (!Raced)
            {
                Raced = true;
                await inner.SaveAtomicForTestAsync(tenantId, competitor, cancellationToken);
                return null;
            }

            return await inner.FindBySourceReferenceAsync(tenantId, sourceReference, cancellationToken);
        }
    }
}
