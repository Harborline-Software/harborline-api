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
/// DES-0029 kernel-core-ck-6: accepting or un-matching a bank match commits the link, then the statement line's
/// derived state. A crash at the real line UPDATE after the link committed must not leave the line wrong for good:
/// the retry finishes the derived step, and a true duplicate is still refused.
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

    [Fact(DisplayName = "ck-6 bank: an accept retried after a crash between the link and the line leaves the line Matched")]
    public async Task Accept_RetryAfterCrash_FinishesTheLine()
    {
        _fault.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Accept().AcceptAsync(Tenant, _linkId));
        Assert.Equal(MatchLinkState.Accepted, (await Link()).State);

        await Accept().AcceptAsync(Tenant, _linkId);

        Assert.Equal(ReconciliationState.Matched, (await Line()).State);
        var duplicate = await Assert.ThrowsAsync<MatchAcceptException>(() => Accept().AcceptAsync(Tenant, _linkId));
        Assert.Equal(MatchAcceptRejectReason.LinkNotProposed, duplicate.Reason);
    }

    [Fact(DisplayName = "ck-6 bank: an un-match retried after a crash between the link and the line leaves the line Unmatched")]
    public async Task UnMatch_RetryAfterCrash_FinishesTheLine()
    {
        await Accept().AcceptAsync(Tenant, _linkId);
        _fault.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => UnMatch().UnMatchAsync(Tenant, _linkId));
        Assert.Equal(MatchLinkState.Reversed, (await Link()).State);

        await UnMatch().UnMatchAsync(Tenant, _linkId);

        Assert.Equal(ReconciliationState.Unmatched, (await Line()).State);
        var duplicate = await Assert.ThrowsAsync<UnMatchException>(() => UnMatch().UnMatchAsync(Tenant, _linkId));
        Assert.Equal(UnMatchRejectReason.AlreadyReversed, duplicate.Reason);
    }

    private AcceptMatchService Accept() => new(
        new NodeEfMatchLinkRepository(_factory), new NodeEfStatementLineRepository(_factory),
        new NodeEfReconciliationRepository(_factory), new NodeEfFiscalPeriodRepository(_factory), TimeProvider.System,
        new ReconciliationLockLease(TimeProvider.System));

    private UnMatchService UnMatch() => new(
        new NodeEfMatchLinkRepository(_factory), new NodeEfStatementLineRepository(_factory),
        new NodeEfReconciliationRepository(_factory), new ReconciliationLockLease(TimeProvider.System));

    private async Task<MatchLink> Link() => (await new NodeEfMatchLinkRepository(_factory).GetByIdAsync(Tenant, _linkId))!;

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
