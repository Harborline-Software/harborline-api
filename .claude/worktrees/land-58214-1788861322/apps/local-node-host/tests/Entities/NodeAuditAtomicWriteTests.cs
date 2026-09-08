using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Coordination;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// ADR 0126 T4 — atomic node audit-write tests. Proves the audit row commits in the SAME
/// <c>local-node.db</c> transaction as the journal-entry write (OQ2 = ATOMIC), lands ONLY in the
/// recoverable store, reads back via the node audit reader with the offline integrity verdict, and
/// rolls back atomically when the JE write fails.
/// </summary>
public sealed class NodeAuditAtomicWriteTests : IAsyncLifetime
{
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodeAuditWriteEnlister _enlister = null!;
    private NodeAuditEventReader _reader = null!;

    private static readonly TenantId LocalTenantId = new("local");

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-audit-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "audit-test.db")};Pooling=False";

        var services = new ServiceCollection();
        // The same module set the production node composes for the JE + audit tables.
        services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
        var provider = services.BuildServiceProvider();

        _factory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _enlister = new NodeAuditWriteEnlister();
        _reader = new NodeAuditEventReader(_factory);
    }

    public async Task DisposeAsync()
    {
        await Task.CompletedTask;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact(DisplayName = "Atomic: posting a JE through the audit-wired store writes the JE AND an audit row in one transaction")]
    public async Task SaveAtomic_WithEnlister_WritesJournalEntryAndAuditRowAtomically()
    {
        var store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: _enlister));
        var entry = BalancedPosted("JE-AUDIT-1", 100m);

        await store.SaveAtomicForTestAsync(LocalTenantId, entry);

        // The JE row landed.
        await using var ctx = await _factory.CreateDbContextAsync();
        var jeCount = await ctx.Set<JournalEntry>()
            .CountAsync(j => j.TenantId == LocalTenantId);
        Assert.Equal(1, jeCount);

        // The audit row landed in the SAME recoverable store.
        var auditRows = await ctx.Set<NodeAuditEventRow>()
            .Where(r => r.TenantId == LocalTenantId.Value)
            .ToListAsync();
        Assert.Single(auditRows);
        var row = auditRows[0];
        Assert.Equal(NodeAuditWriteEnlister.JournalPostedEventType, row.EventType);
        Assert.Equal(LocalTenantId.Value, row.TenantId);
        Assert.Null(row.Signature); // unsigned on the node (no IOperationSigner)
        Assert.Null(row.PrevHash);  // first record in the chain
        Assert.False(string.IsNullOrEmpty(row.Hash));
    }

    [Fact(DisplayName = "Reader: a JE-posted audit row reads back as NotSigned (chain-intact, unsigned) — never VerificationFailed")]
    public async Task Reader_ReturnsAuditRow_WithNotSignedState()
    {
        var store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: _enlister));
        await store.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-AUDIT-2", 250m));

        var page = await _reader.ListAsync(LocalTenantId.Value, new NodeAuditEventReaderQuery());
        var view = Assert.Single(page.Events);

        Assert.Equal(NodeAuditWriteEnlister.JournalPostedEventType, view.EventType);
        // Chain-intact + unsigned → NotSigned (NEVER VerificationFailed for an unsigned record).
        Assert.Equal(NodeAuditSignatureClassifier.NotSigned, view.SignatureState);
        Assert.NotEqual(NodeAuditSignatureClassifier.VerificationFailed, view.SignatureState);
        Assert.Equal("test-operator", view.Actor);

        // Detail-by-id returns the same record.
        var detail = await _reader.GetByIdAsync(LocalTenantId.Value, view.AuditId);
        Assert.NotNull(detail);
        Assert.Equal(view.AuditId, detail!.AuditId);
    }

    [Fact(DisplayName = "HashChain: multiple JE-posted audit rows form a verifiable offline hash chain")]
    public async Task MultiplePostings_FormVerifiableHashChain()
    {
        var store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: _enlister));
        await store.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-CHAIN-1", 10m));
        await store.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-CHAIN-2", 20m));
        await store.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-CHAIN-3", 30m));

        await using var ctx = await _factory.CreateDbContextAsync();
        var rows = await ctx.Set<NodeAuditEventRow>()
            .FromSql($"""
                SELECT * FROM node_audit_events
                WHERE "TenantId" = {LocalTenantId.Value}
                ORDER BY rowid ASC
                """)
            .ToListAsync();

        Assert.Equal(3, rows.Count);
        // The offline, key-independent chain verifies — the SC-4-safe integrity guarantee.
        Assert.True(NodeAuditHashChain.Verify(rows), "the node audit hash chain must verify offline");

        // Every row reads back Verified-chain (NotSigned, since unsigned) — none VerificationFailed.
        var page = await _reader.ListAsync(LocalTenantId.Value, new NodeAuditEventReaderQuery(PageSize: 50));
        Assert.Equal(3, page.Events.Count);
        Assert.All(page.Events, v =>
            Assert.NotEqual(NodeAuditSignatureClassifier.VerificationFailed, v.SignatureState));
    }

    [Fact(DisplayName = "HashChain: an older admitted act appended later still chains from the append tip")]
    public async Task OlderBusinessInstant_AppendedLater_DoesNotReplaceAppendOrdering()
    {
        var store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: _enlister));
        var newer = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
        var older = newer.AddDays(-7);

        await store.SaveAtomicForTestAsync(
            LocalTenantId,
            BalancedPostedAt("JE-CHAIN-NEWER", 10m, newer),
            new AuthorizationWriteContext(new ActorId("test:audit-chain"), LocalTenantId, newer));
        await store.SaveAtomicForTestAsync(
            LocalTenantId,
            BalancedPostedAt("JE-CHAIN-OLDER", 20m, older),
            new AuthorizationWriteContext(new ActorId("test:audit-chain"), LocalTenantId, older));

        await using var ctx = await _factory.CreateDbContextAsync();
        var appendOrder = await ctx.Set<NodeAuditEventRow>()
            .FromSqlRaw("SELECT * FROM node_audit_events ORDER BY rowid ASC")
            .AsNoTracking()
            .ToListAsync();

        Assert.Equal([newer, older], appendOrder.Select(row => row.OccurredAt));
        Assert.Equal(appendOrder[0].Hash, appendOrder[1].PrevHash);
        Assert.True(NodeAuditHashChain.Verify(appendOrder));
    }

    [Fact(DisplayName = "Atomic rollback: when the audit enlister throws, the JE write rolls back (no JE row, no audit row)")]
    public async Task SaveAtomic_EnlisterThrows_RollsBackJournalEntry()
    {
        var store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: new ThrowingEnlister()));
        var entry = BalancedPosted("JE-ROLLBACK-1", 99m);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.SaveAtomicForTestAsync(LocalTenantId, entry));

        // Neither the JE nor an audit row may persist — the whole unit-of-work rolled back.
        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId));
        Assert.Equal(0, await ctx.Set<NodeAuditEventRow>().CountAsync(r => r.TenantId == LocalTenantId.Value));
    }

    [Fact(DisplayName = "Atomic rollback (T4-audit C1): when the BUSINESS write fails mid-SaveChanges (unique-index race), the already-staged audit row rolls back — no orphan audit")]
    public async Task SaveAtomic_BusinessWriteFailsMidSaveChanges_AuditRowRollsBack()
    {
        // The complement of the enlister-throws test: here the enlister succeeds and stages a VALID
        // audit row onto the ctx, then the JE (business) write itself fails DURING SaveChangesAsync —
        // the second JE collides with ux_journal_entries_tenant_source_ref. Because the audit row was
        // staged onto the SAME ctx (ADR 0126 §D2 / OQ2 = ATOMIC), the single transaction must roll back
        // BOTH: a failed posting can never leave an orphan audit row behind.
        var store = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create(audit: _enlister));
        const string sourceRef = "atomic-c1-dup-key";

        // First posting: JE + its audit row both commit.
        await store.SaveAtomicForTestAsync(LocalTenantId, BalancedPostedWithSourceRef("JE-ATOMIC-C1-1", 50m, sourceRef));

        // Second posting with the SAME (tenant, SourceReference): the audit enlister stages its row,
        // then SaveChangesAsync throws on the unique index — the business write fails mid-save.
        await Assert.ThrowsAsync<DbUpdateException>(
            () => store.SaveAtomicForTestAsync(LocalTenantId, BalancedPostedWithSourceRef("JE-ATOMIC-C1-2", 50m, sourceRef)));

        await using var ctx = await _factory.CreateDbContextAsync();

        // Exactly ONE JE survived (the first) — the second rolled back.
        Assert.Equal(1, await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId));

        // And exactly ONE audit row survived — the second posting's staged audit row rolled back atomically
        // with its failed JE write. If the audit append rode a separate transaction this would be 2 (an
        // orphan audit for a posting that never committed); the atomic guarantee makes it 1.
        Assert.Equal(1, await ctx.Set<NodeAuditEventRow>().CountAsync(r => r.TenantId == LocalTenantId.Value));
    }

    [Fact(DisplayName = "Declared: a store without required enlistments refuses the journal save")]
    public async Task SaveAtomic_WithoutDeclaredEnlistments_RefusesJournalEntry()
    {
        var store = new NodeEfJournalStore(_factory, Array.Empty<IWriteEnlistment>());
        await Assert.ThrowsAsync<MissingWriteEnlistmentException>(
            () => store.SaveAtomicForTestAsync(LocalTenantId, BalancedPosted("JE-NOAUDIT-1", 42m)));

        await using var ctx = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId));
        Assert.Equal(0, await ctx.Set<NodeAuditEventRow>().CountAsync(r => r.TenantId == LocalTenantId.Value));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static JournalEntry BalancedPosted(string id, decimal amount) =>
        BalancedPostedAt(id, amount, DateTimeOffset.UtcNow);

    private static JournalEntry BalancedPostedAt(
        string id,
        decimal amount,
        DateTimeOffset at) =>
        new JournalEntry(
            id: new JournalEntryId(id),
            tenantId: LocalTenantId,
            entryDate: new DateOnly(2026, 6, 16),
            memo: "atomic audit test",
            lines: new List<JournalEntryLine>
            {
                new(new GLAccountId("1000"), amount, 0m),
                new(new GLAccountId("4000"), 0m, amount),
            },
            createdAtUtc: new Instant(at))
        {
            Status = JournalEntryStatus.Posted,
        };

    /// <summary>A balanced posted entry carrying a non-null SourceReference so a second save with the
    /// same key collides on ux_journal_entries_tenant_source_ref — the C1 mid-SaveChanges failure.</summary>
    private static JournalEntry BalancedPostedWithSourceRef(string id, decimal amount, string sourceReference) =>
        new JournalEntry(
            id: new JournalEntryId(id),
            tenantId: LocalTenantId,
            entryDate: new DateOnly(2026, 6, 16),
            memo: "atomic audit test (C1)",
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

    /// <summary>An enlister that throws AFTER the JE row is staged — exercises the atomic rollback.</summary>
    private sealed class ThrowingEnlister : IWriteEnlistment
    {
        public WriteInvariant Invariant => NodeWriteInvariants.Audit;

        public ValueTask<WriteEnlistmentOutcome> EnlistAsync(
            StagedWriteUnitOfWork unitOfWork,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("induced audit-enlist failure for rollback test");
    }
}
