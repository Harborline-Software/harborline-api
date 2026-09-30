using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.Banking.Matching;
using Harborline.Api.Blocks.Banking.Models;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Integrations.Payments;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Banking;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// DES-0029 kernel-core-ck-6 (T-988): accepting or un-matching a bank match commits the link and the statement line's
/// derived state in one transaction. A crash at the real line UPDATE leaves neither, a retry is a fresh accept, and a
/// duplicate for a completed link is refused on the link's own persisted state, even when another link on the same line
/// was interrupted.
/// </summary>
public sealed class BankMatchCrashResumeTests : IAsyncLifetime
{
    private static readonly TenantId Tenant = TenantId.FromString("tenant-bank-resume");

    private readonly LineUpdateFault _fault = new();
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private StatementLineId _lineId;
    private MatchLinkId _linkId;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harborline-ck6-bank-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var services = new ServiceCollection();
        services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.Banking.Data.BankingEntityModule>();
        services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(_dir, "bank.db")};Pooling=False").AddInterceptors(_fault));
        _factory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        var created = new Instant(DateTimeOffset.Parse("2026-03-02T09:00:00Z"));
        _lineId = StatementLineId.NewId();
        _linkId = MatchLinkId.NewId();
        await new NodeEfStatementLineRepository(_factory).AddAsync(new StatementLine(
            _lineId, Tenant, BankAccountId.NewId(), "txn-1", created, 125m, new CurrencyCode("USD"), "Deposit",
            Pending: false, ReconciliationState.Proposed, new ImportSourceRef(ImportSourceKind.FileImport, "batch-1", 0),
            RawProviderBlob: null, created));
        await new NodeEfMatchLinkRepository(_factory).AddAsync(new MatchLink(
            _linkId, Tenant, _lineId, new LedgerTransactionRef(new JournalEntryId("JE-1")), 125m,
            MatchLinkState.Proposed, AcceptedAt: null));
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "ck-6 bank: a crash at the line update leaves neither the link nor the line accepted; the retry accepts both")]
    public async Task Accept_CrashAtLineUpdate_CommitsNeither_RetryAccepts()
    {
        _fault.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Accept().AcceptAsync(Tenant, _linkId));
        Assert.Equal(MatchLinkState.Proposed, (await Link()).State);
        Assert.Null((await Link()).AcceptedAt);
        Assert.Equal(ReconciliationState.Proposed, (await Line()).State);

        await Accept().AcceptAsync(Tenant, _linkId);

        Assert.Equal(ReconciliationState.Matched, (await Line()).State);
        var duplicate = await Assert.ThrowsAsync<MatchAcceptException>(() => Accept().AcceptAsync(Tenant, _linkId));
        Assert.Equal(MatchAcceptRejectReason.LinkNotProposed, duplicate.Reason);
    }

    [Fact(DisplayName = "ck-6 bank: a crash at the line update leaves neither the link nor the line un-matched; the retry un-matches both")]
    public async Task UnMatch_CrashAtLineUpdate_CommitsNeither_RetryUnMatches()
    {
        await Accept().AcceptAsync(Tenant, _linkId);
        _fault.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => UnMatch().UnMatchAsync(Tenant, _linkId));
        Assert.Equal(MatchLinkState.Accepted, (await Link()).State);
        Assert.Equal(ReconciliationState.Matched, (await Line()).State);

        await UnMatch().UnMatchAsync(Tenant, _linkId);

        Assert.Equal(ReconciliationState.Unmatched, (await Line()).State);
        var duplicate = await Assert.ThrowsAsync<UnMatchException>(() => UnMatch().UnMatchAsync(Tenant, _linkId));
        Assert.Equal(UnMatchRejectReason.AlreadyReversed, duplicate.Reason);
    }

    [Fact(DisplayName = "T-988 bank: a duplicate accept of a completed link is refused although another link's accept was interrupted")]
    public async Task Accept_DuplicateOfCompletedLink_IsRefused_WhileAnotherLinkWasInterrupted()
    {
        // The line is 125: link A (50) completes, link B (75) is interrupted at the line update.
        var a = await AddLink(50m);
        var b = await AddLink(75m);
        await Accept().AcceptAsync(Tenant, a);
        _fault.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Accept().AcceptAsync(Tenant, b));

        var duplicate = await Assert.ThrowsAsync<MatchAcceptException>(() => Accept().AcceptAsync(Tenant, a));

        Assert.Equal(MatchAcceptRejectReason.LinkNotProposed, duplicate.Reason);
        Assert.Equal(MatchLinkState.Proposed, (await Link(b)).State);
        Assert.Equal(ReconciliationState.PartiallyMatched, (await Line()).State);
    }

    [Fact(DisplayName = "T-988 bank: a duplicate un-match of a completed link is refused although another link's un-match was interrupted")]
    public async Task UnMatch_DuplicateOfCompletedLink_IsRefused_WhileAnotherLinkWasInterrupted()
    {
        var a = await AddLink(50m);
        var b = await AddLink(75m);
        await Accept().AcceptAsync(Tenant, a);
        await Accept().AcceptAsync(Tenant, b);
        await UnMatch().UnMatchAsync(Tenant, a);
        _fault.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => UnMatch().UnMatchAsync(Tenant, b));

        var duplicate = await Assert.ThrowsAsync<UnMatchException>(() => UnMatch().UnMatchAsync(Tenant, a));

        Assert.Equal(UnMatchRejectReason.AlreadyReversed, duplicate.Reason);
        Assert.Equal(MatchLinkState.Accepted, (await Link(b)).State);
        Assert.Equal(ReconciliationState.PartiallyMatched, (await Line()).State);
    }

    [Fact(DisplayName = "T-988 bank: a duplicate un-match of a reversed link is refused and leaves a line a later full accept matched")]
    public async Task UnMatch_DuplicateAfterAnotherLinkFullyMatched_IsRefused_LineStaysMatched()
    {
        // Reverse A (50), then accept the 125 link for the full line amount: the line is Matched.
        var a = await AddLink(50m);
        await Accept().AcceptAsync(Tenant, a);
        await UnMatch().UnMatchAsync(Tenant, a);
        await Accept().AcceptAsync(Tenant, _linkId);

        var duplicate = await Assert.ThrowsAsync<UnMatchException>(() => UnMatch().UnMatchAsync(Tenant, a));

        Assert.Equal(UnMatchRejectReason.AlreadyReversed, duplicate.Reason);
        Assert.Equal(ReconciliationState.Matched, (await Line()).State);
    }

    [Fact(DisplayName = "T-988 bank: an un-match that leaves the full line amount accepted leaves the line Matched")]
    public async Task UnMatch_LeavingTheFullAmountAccepted_LeavesTheLineMatched()
    {
        // The 125 link matches the line in full; an extra 50 link makes it PartiallyMatched until it is reversed.
        var a = await AddLink(50m);
        await Accept().AcceptAsync(Tenant, _linkId);
        await Accept().AcceptAsync(Tenant, a);
        Assert.Equal(ReconciliationState.PartiallyMatched, (await Line()).State);

        await UnMatch().UnMatchAsync(Tenant, a);

        Assert.Equal(ReconciliationState.Matched, (await Line()).State);
    }

    [Fact(DisplayName = "T-988 bank: a transition whose link already left the expected state writes neither the link nor the line")]
    public async Task Transition_FromAStateTheLinkLeft_WritesNothing()
    {
        // A racing accept read Proposed, then another caller accepted first: the persisted state decides.
        await Accept().AcceptAsync(Tenant, _linkId);
        var repo = new NodeEfMatchLinkRepository(_factory);
        var stale = (await Link()) with { State = MatchLinkState.Reversed };

        var moved = await repo.TransitionWithLineAsync(
            stale, MatchLinkState.Proposed, (await Line()) with { State = ReconciliationState.Unmatched });

        Assert.False(moved);
        Assert.Equal(MatchLinkState.Accepted, (await Link()).State);
        Assert.Equal(ReconciliationState.Matched, (await Line()).State);
    }

    private async Task<MatchLinkId> AddLink(decimal amount)
    {
        var id = MatchLinkId.NewId();
        await new NodeEfMatchLinkRepository(_factory).AddAsync(new MatchLink(
            id, Tenant, _lineId, new LedgerTransactionRef(new JournalEntryId("JE-" + amount)), amount,
            MatchLinkState.Proposed, AcceptedAt: null));
        return id;
    }

    private AcceptMatchService Accept() => new(
        new NodeEfMatchLinkRepository(_factory), new NodeEfStatementLineRepository(_factory),
        new NodeEfReconciliationRepository(_factory), new NodeEfFiscalPeriodRepository(_factory), TimeProvider.System,
        new ReconciliationLockLease(TimeProvider.System));

    private UnMatchService UnMatch() => new(
        new NodeEfMatchLinkRepository(_factory), new NodeEfStatementLineRepository(_factory),
        new NodeEfReconciliationRepository(_factory), new ReconciliationLockLease(TimeProvider.System));

    private Task<MatchLink> Link() => Link(_linkId);

    private async Task<MatchLink> Link(MatchLinkId id) => (await new NodeEfMatchLinkRepository(_factory).GetByIdAsync(Tenant, id))!;

    private async Task<StatementLine> Line() => (await new NodeEfStatementLineRepository(_factory).GetByIdAsync(Tenant, _lineId))!;

    /// <summary>When armed, throws once before the statement-line UPDATE: a crash after the link committed.</summary>
    private sealed class LineUpdateFault : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Check(DbCommand command)
        {
            if (Armed && command.CommandText.Contains("UPDATE \"statement_lines\"", StringComparison.Ordinal))
            {
                Armed = false;
                throw new InvalidOperationException("ck-6 injected crash before the statement-line update");
            }
        }
    }
}
